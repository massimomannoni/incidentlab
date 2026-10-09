using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IncidentLab.Worker;

public sealed class WorkerHost
{
    public static Uri ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("MONITOR_URL must be a base URL without credentials, path or query");
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.Host is "127.0.0.1" or "localhost" or "[::1]" or "::1"))
            throw new ArgumentException("Use HTTPS or a loopback SSM tunnel; insecure remote HTTP is refused");
        return uri;
    }
    public async Task Run(CancellationToken ct)
    {
        var url = ValidateUrl(Environment.GetEnvironmentVariable("MONITOR_URL") ?? "http://127.0.0.1:8010");
        var key = Environment.GetEnvironmentVariable("LAB_WORKER_KEY") ?? "";
        if (key.Length < 24) throw new ArgumentException("LAB_WORKER_KEY must contain at least 24 characters");
        var identity = J.Obj(new { worker_id = Environment.GetEnvironmentVariable("LAB_WORKER_ID") ?? "mac-codex-worker" });
        Contracts.Validate(identity, Contracts.WorkerSchema("claim"));
        var launcher = new CliLauncher(CliLauncher.FindExecutable()); var invocation = new CodexInvocation(launcher);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = url, Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("X-Lab-Worker-Key", key);
        async Task<JsonObject> Post(string path, JsonObject value)
        {
            using var response = await client.PostAsJsonAsync(path, value, ct); response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidDataException("Empty monitor response");
        }
        Console.WriteLine("IncidentLab C# Codex worker ready. Idle polling uses no LLM. Ctrl+C to stop.");
        try
        {
            var models = await new ModelCatalog(launcher).Discover(ct); var body = identity.Copy(); body["models"] = models;
            await Post("/api/worker/catalog", body); Console.WriteLine($"Model catalog loaded: {models.Count} models. No inference used.");
        }
        catch (Exception) when (!ct.IsCancellationRequested) { Console.WriteLine("Model discovery unavailable. Restart worker to refresh; no fallback model is invented."); }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var claimed = await Post("/api/worker/claim", identity);
                if (claimed["job"] is JsonObject job)
                {
                    var receipt = identity.Copy(); receipt["lease"] = job["lease"]?.DeepClone(); var prefix = $"/api/worker/jobs/{job.S("id")}";
                    Console.WriteLine($"Job {J.Clip(job.S("id"), 8)} claimed."); var metadata = new JsonObject();
                    try
                    {
                        for (var attempt = 0; attempt < 4 && job.S("status") == "running"; attempt++)
                        {
                            var role = job.S("stage");
                            metadata = new();
                            var (report, usage) = await invocation.Invoke(job, role, async () => { await Post(prefix + "/heartbeat", receipt); },
                                async () => { await Post(prefix + "/started", receipt); }, metadata, ct);
                            var result = receipt.Copy(); result["stage"] = role; result["report"] = report; result["usage"] = usage;
                            result["provider_session_id"] = metadata["provider_session_id"]?.DeepClone();
                            job = (await Post(prefix + "/result", result)).O("job");
                            Console.WriteLine($"{role}: report saved.");
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        var reason = ex is WorkerFailure ? ex.Message : "codex_failed";
                        Console.WriteLine($"Job stopped: {reason}. No automatic retry.");
                        var failure = receipt.Copy(); failure["reason"] = reason;
                        foreach (var (k, v) in metadata) failure[k] = v?.DeepClone();
                        try { await Post(prefix + "/failed", failure); } catch (Exception) when (!ct.IsCancellationRequested) { }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            { Console.WriteLine("Monitor unavailable or worker credential rejected; no model invoked."); await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        }
    }
}
