using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IncidentLab.Worker;

public sealed class CodexInvocation(ICliLauncher launcher)
{
    public const string Instructions = AgentPrompts.Instructions;
    public static List<string> Arguments(string directory, string schema, string model, string effort = "default")
    {
        var args = new List<string> { "exec", "--ignore-user-config", "--ephemeral", "--skip-git-repo-check", "--sandbox", "read-only",
            "--json", "--color", "never", "-C", directory, "--output-schema", schema, "-c", "approval_policy=\"never\"", "-c", "web_search=\"disabled\"" };
        foreach (var feature in new[] { "shell_tool", "unified_exec", "shell_snapshot", "apps", "plugins", "browser_use", "computer_use", "multi_agent",
            "code_mode", "code_mode_host", "image_generation", "view_image", "hooks", "memories", "skill_search" }) args.AddRange(["--disable", feature]);
        args.AddRange(["--enable", "skip_host_skill_discovery"]);
        if (model.Length > 0) args.AddRange(["--model", model]);
        if (effort != "default") args.AddRange(["-c", "model_reasoning_effort=" + JsonSerializer.Serialize(effort)]);
        args.Add("-"); return args;
    }
    public static string Prompt(JsonObject job, string role)
    {
        try { return AgentPrompts.Prompt(job, role); }
        catch (LabException) { throw new WorkerFailure("output_limit"); }
    }
    public async Task<(JsonObject report, JsonObject usage)> Invoke(JsonObject job, string role, Func<Task> heartbeat, Func<Task> onStart,
        JsonObject metadata, CancellationToken ct = default)
    {
        var prompt = Prompt(job, role); var schema = Contracts.ReportSchema(role, DiagnosticEvidence.Enabled(job.O("packet")));
        var temp = Directory.CreateTempSubdirectory("incidentlab-agent-");
        try
        {
            var path = Path.Combine(temp.FullName, "report.schema.json"); await File.WriteAllTextAsync(path, schema.ToJsonString(), ct);
            var settings = job.O("settings"); var profile = settings.O("resolved_profiles")[DiagnosticEvidence.Role(role)] as JsonObject ?? J.Obj(new { model_id = settings.S("model_id"), reasoning_effort = "default" });
            await onStart();
            await using var process = launcher.Start(Arguments(temp.FullName, path, profile.S("model_id"), profile.S("reasoning_effort", "default")), temp.FullName);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(TimeSpan.FromSeconds(settings.N("timeout_seconds", 45)));
            string? output = null; JsonObject usage = new(); bool completed = false;
            async Task Consume()
            {
                await process.Input.WriteAsync(prompt.AsMemory(), stop.Token); await process.Input.FlushAsync(stop.Token); process.Input.Close();
                await foreach (var line in BoundedOutput.Lines(process.Output, 256000, stop.Token))
                {
                    JsonObject message;
                    try { if (JsonNode.Parse(line) is not JsonObject obj) continue; message = obj; } catch (JsonException) { continue; }
                    var kind = message.S("type"); var item = message.O("item");
                    if (kind == "thread.started") metadata["provider_session_id"] = message["thread_id"]?.DeepClone();
                    if (item.S("type") is "command_execution" or "mcp_tool_call" or "web_search" or "file_change") throw new WorkerFailure("unexpected_tool");
                    // Reasoning events are not retained, logged or exposed through the API.
                    if (kind == "item.completed" && item.S("type") == "agent_message") output = item.S("text");
                    if (kind == "turn.completed") { completed = true; usage = AiUsage.Safe(message.O("usage")); metadata["usage"] = usage.Copy(); }
                    if (kind is "turn.failed" or "error") throw new WorkerFailure("codex_failed");
                }
                if (await process.Wait(stop.Token) != 0 || !completed || string.IsNullOrEmpty(output)) throw new WorkerFailure("codex_failed");
            }
            async Task Pulse()
            {
                while (true) { await Task.Delay(TimeSpan.FromSeconds(4), stop.Token); await heartbeat(); }
            }
            var read = Consume(); var pulse = Pulse();
            try
            {
                var done = await Task.WhenAny(read, pulse); await done;
                if (!read.IsCompletedSuccessfully) throw new WorkerFailure("worker_stopped");
                try
                {
                    var report = Contracts.Validate(JsonNode.Parse(output!), schema).AsObject();
                    if (Encoding.UTF8.GetByteCount(report.ToJsonString()) > 14000) throw new WorkerFailure("output_limit");
                    return (report, usage);
                }
                catch (Exception ex) when (ex is JsonException or LabException) { throw new WorkerFailure("invalid_output"); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new WorkerFailure("timeout"); }
            finally
            {
                await stop.CancelAsync(); await process.Stop();
                try { await Task.WhenAll(read, pulse); } catch (Exception) { /* Already surfaced as the bounded failure above. */ }
            }
        }
        finally { temp.Delete(recursive: true); }
    }
}
