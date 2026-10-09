using System.Text;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;

namespace IncidentLab.Api;

public sealed record BedrockAnswer(string Text, JsonObject Usage, string? RequestId, string StopReason);
public interface IBedrockInference
{
    Task<BedrockAnswer> Converse(string prompt, int maxOutputTokens, CancellationToken ct);
}

public sealed class BedrockInference(LabConfig config) : IBedrockInference
{
    public async Task<BedrockAnswer> Converse(string prompt, int maxOutputTokens, CancellationToken ct)
    {
        // Explicit instance-profile credentials: no API key, bearer token, user profile or
        // interactive account is accepted. Credentials rotate through IMDSv2 on the AWS host.
        using var credentials = new InstanceProfileAWSCredentials();
        using var client = new AmazonBedrockRuntimeClient(credentials, new AmazonBedrockRuntimeConfig
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(config.BedrockRegion), MaxErrorRetry = 0,
            AuthSchemePreference = ["sigv4"],
            Timeout = TimeSpan.FromSeconds(180)
        });
        var result = await client.ConverseAsync(new ConverseRequest
        {
            // Official EU profile ID; the SDK analyzer rejects this documented profile.
#pragma warning disable BedrockRuntime1002
            ModelId = BedrockBudget.ModelId,
#pragma warning restore BedrockRuntime1002
            Messages = [new Message { Role = ConversationRole.User, Content = [new ContentBlock { Text = prompt }] }],
            InferenceConfig = new InferenceConfiguration { MaxTokens = maxOutputTokens, Temperature = 0 }
            // Deliberately no tools, guardrail calls, cache writes, inference fallback or retries.
        }, ct);
        var content = result.Output?.Message?.Content;
        var text = content == null ? "" : string.Concat(content.Select(c => c.Text ?? ""));
        var usage = new JsonObject();
        if (result.Usage?.InputTokens is int input) usage["input_tokens"] = input;
        if (result.Usage?.OutputTokens is int output) usage["output_tokens"] = output;
        return new(text, usage, result.ResponseMetadata?.RequestId, result.StopReason?.Value ?? "unknown");
    }
}

public sealed class BedrockExecutor(Agents agents, IBedrockInference inference, LabConfig config)
{
    const string Mode = "cloud_api";
    readonly JsonObject identity = J.Obj(new { worker_id = "aws-bedrock-executor" });
    public async Task<bool> RunOne(CancellationToken ct)
    {
        if (!config.BedrockEnabled) return false;
        var claimed = agents.Claim(identity, Mode);
        if (claimed["job"] is not JsonObject job) return false;
        var receipt = identity.Copy(); receipt["lease"] = job.S("lease");
        var id = job.S("id"); var settings = job.O("settings");
        var usage = new JsonObject(); string? requestId = null;
        try
        {
            for (var attempt = 0; attempt < 4 && job.S("status") == "running"; attempt++)
            {
                var role = job.S("stage");
                usage = new(); requestId = null;
                // Nova Lite does not offer constrained JSON decoding. Require JSON in the
                // prompt, then apply the same strict schema/evidence checks as the Codex path.
                var prompt = AgentPrompts.Prompt(job, role) + "\nReturn a JSON object matching this schema, without Markdown fences:\n" + Contracts.ReportSchema(role, DiagnosticEvidence.Enabled(job.O("packet"))).ToJsonString();
                if (Encoding.UTF8.GetByteCount(prompt) > settings.N("max_prompt_bytes")) throw new LabException(422, "prompt_limit");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.N("timeout_seconds")));
                ct.ThrowIfCancellationRequested();
                agents.Started(id, receipt, Mode); // Commits the reservation BEFORE network I/O.
                async Task Pulse()
                {
                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4), timeout.Token);
                        try { agents.Heartbeat(id, receipt, Mode); }
                        catch (LabException) { await timeout.CancelAsync(); return; }
                    }
                }
                var pulse = Pulse(); BedrockAnswer answer;
                try { answer = await inference.Converse(prompt, (int)settings.N("max_output_tokens"), timeout.Token); }
                finally
                {
                    await timeout.CancelAsync();
                    try { await pulse; } catch (OperationCanceledException) { }
                }
                usage = AiUsage.Safe(answer.Usage); requestId = answer.RequestId;
                if (answer.StopReason != "end_turn" || Encoding.UTF8.GetByteCount(answer.Text) > 14000) throw new LabException(422, "invalid_output");
                var report = ParseReport(answer.Text);
                var result = receipt.Copy(); result["stage"] = role; result["report"] = report; result["usage"] = usage.Copy(); result["provider_session_id"] = requestId;
                job = agents.Result(id, result, Mode).O("job");
            }
        }
        catch (Exception ex)
        {
            // Retain a provider correlation ID, never its raw error message or payload.
            if (ex is AmazonServiceException service && Guid.TryParse(service.RequestId, out var correlation)) requestId = correlation.ToString();
            var failure = receipt.Copy(); failure["usage"] = usage.Copy(); failure["provider_session_id"] = requestId;
            failure["reason"] = ex switch
            {
                LabException { Status: 429 } => "budget_exhausted",
                LabException { Status: 409 } => "execution_stopped",
                LabException { Status: 422 } error when error.Message is "report_unknown_reference" or "report_check_unavailable" or "report_second_check" or "report_cause_without_reference" => "bedrock_" + error.Message,
                LabException { Status: 422 } => "bedrock_invalid_report",
                System.Text.Json.JsonException => "bedrock_invalid_json",
                OperationCanceledException => "timeout_or_shutdown",
                AmazonServiceException provider => ProviderFailure(provider),
                _ => "invalid_output_or_configuration"
            };
            // Do not emit raw provider errors, prompts, keys or private model reasoning.
            // If disabled/expired meanwhile, the existing reservation remains conservative.
            try { agents.Failed(id, failure, Mode); } catch (LabException) { }
        }
        return true;
    }
    public static JsonObject ParseReport(string text)
    {
        var json = text.Trim();
        // Text-only models can wrap otherwise valid JSON in one Markdown code fence.
        // Accept that exact envelope, not prose, extracted substrings or multiple blocks.
        if (json.StartsWith("```json\n", StringComparison.Ordinal) && json.EndsWith("\n```", StringComparison.Ordinal)) json = json[8..^4];
        else if (json.StartsWith("```json\r\n", StringComparison.Ordinal) && json.EndsWith("\r\n```", StringComparison.Ordinal)) json = json[9..^5];
        return JsonNode.Parse(json) as JsonObject ?? throw new LabException(422, "invalid_output");
    }
    static string ProviderFailure(AmazonServiceException error) => error.ErrorCode switch
    {
        "AccessDeniedException" => "bedrock_access_denied",
        "ValidationException" => "bedrock_validation_error",
        "ThrottlingException" => "bedrock_throttled",
        "ServiceQuotaExceededException" => "bedrock_service_quota_exceeded",
        "ResourceNotFoundException" => "bedrock_resource_not_found",
        "ModelNotReadyException" => "bedrock_model_not_ready",
        "ModelTimeoutException" => "bedrock_model_timeout",
        "ServiceUnavailableException" => "bedrock_service_unavailable",
        "InternalServerException" => "bedrock_internal_error",
        "ModelErrorException" => "bedrock_model_error",
        _ => "bedrock_request_failed"
    };
}

public sealed class BedrockService(BedrockExecutor executor, ILogger<BedrockService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await executor.RunOne(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Bedrock executor unavailable: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
