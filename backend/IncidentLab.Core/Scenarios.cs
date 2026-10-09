using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IncidentLab;

public sealed record Scenario(string Id, string Name, string Category, string Icon, string Description, string[] Expected);

public sealed class ScenarioRunner(LabConfig config, Store store, HttpClient client) : IAsyncDisposable
{
    public static readonly Scenario[] Scenarios = [
        new("normal", "Normal traffic", "Baseline", "activity", "Clients read data and execute different commands.", []),
        new("retry", "Retry loop", "Behavior", "repeat", "The same client repeatedly sends the same command without a meaningful pause.", ["repeat-command"]),
        new("probing", "HTTP probing", "Security", "shield", "Requests to many nonexistent paths on the laboratory API.", ["http-probing"]),
        new("errors", "Application errors", "Application", "warning", "The service returns HTTP 500 errors for some requests.", ["server-errors"]),
        new("latency", "Slow responses", "Application", "timer", "A controlled delay increases response time.", ["slow-responses"]),
        new("cpu", "CPU pressure", "Resources", "cpu", "Bounded compute requests increase process CPU usage.", ["process-cpu"]),
        new("memory", "Memory pressure", "Resources", "cpu", "The process temporarily allocates 128 MiB, then releases it.", ["process-memory"]),
        new("telemetry_gap", "Telemetry interruption", "Monitoring", "radio", "The API continues responding but temporarily stops sending telemetry.", ["source-stale"])
    ];
    readonly SemaphoreSlim startGate = new(1, 1);
    Task? task;
    CancellationTokenSource? cancellation;
    public static JsonObject InitialStats() => J.Obj(new { sent = 0, responses = 0, transport_errors = 0, cancelled_requests = 0, http_errors = 0, phase = "warmup", response_samples = Array.Empty<object>() });

    public async Task Faults(JsonObject? values = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.TargetUrl.TrimEnd('/') + "/internal/faults") { Content = JsonContent.Create(values ?? new()) };
        request.Headers.Add("X-Lab-Control-Key", config.ControlKey);
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
    }
    public async Task<string> Start(JsonObject input, CancellationToken ct = default, bool master = false)
    {
        var spec = Contracts.Validate(input, Contracts.RunSchema).AsObject();
        await startGate.WaitAsync(ct);
        try
        {
            if (task is { IsCompleted: false }) throw new LabException(409, "An experiment is already running");
            using (var db = store.Open()) if (!master) SimulationQuota.Check(db, J.Now());
            using var response = await client.GetAsync(config.TargetUrl.TrimEnd('/') + "/health", ct); response.EnsureSuccessStatusCode();
            await Faults(ct: ct);
            var run = new Experiment { Scenario = spec.S("scenario"), Config = spec.Copy(), Result = InitialStats() };
            if (master) run.Config["master_quota_exempt"] = true;
            store.Write(db => { if (!master) SimulationQuota.Check(db, run.Started); db.Runs.Add(run); });
            cancellation?.Dispose(); cancellation = new();
            task = Execute(run.Id, spec, cancellation.Token);
            store.Log("test.started", "Experiment started: " + run.Scenario);
            return run.Id;
        }
        finally { startGate.Release(); }
    }
    void Update(string id, JsonObject stats, string? status = null, double? ended = null) => store.Write(db =>
    {
        var row = db.Runs.Find(id)!; row.Result = stats;
        if (status != null) row.Status = status;
        if (ended != null) row.Ended = ended;
    });
    async Task Execute(string id, JsonObject spec, CancellationToken ct)
    {
        var stats = InitialStats(); var statsGate = new object(); var watch = Stopwatch.StartNew();
        var requests = new List<Task>(); int clients = (int)spec.N("clients");
        using var sem = new SemaphoreSlim(clients); using var requestCancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string finalStatus = "completed"; string? failure = null; bool anomalyOn = false;
        JsonObject Snapshot() { lock (statsGate) return stats.Copy(); }
        void Increment(string key) { lock (statsGate) stats[key] = (int)stats.N(key) + 1; }
        async Task Request(int number, bool abnormal)
        {
            try { await sem.WaitAsync(requestCancel.Token); } catch (OperationCanceledException) { return; }
            try
            {
                var scenario = spec.S("scenario"); var clientId = scenario == "retry" && abnormal ? "client-0" : $"client-{number % clients}";
                var method = HttpMethod.Get; var path = "/items"; JsonObject? body = null;
                if (abnormal && scenario == "probing") path = $"/lab-probe/missing-{number}";
                else if (abnormal && scenario == "cpu") { method = HttpMethod.Post; path = "/compute"; body = J.Obj(new { milliseconds = 200 }); }
                else if (number % 2 != 0 || (abnormal && scenario == "retry"))
                { method = HttpMethod.Post; path = "/commands"; body = J.Obj(new { command = "refresh", resource = abnormal && scenario == "retry" ? "same-item" : $"item-{id[..6]}-{number}" }); }
                using var request = new HttpRequestMessage(method, config.TargetUrl.TrimEnd('/') + path);
                if (body != null) request.Content = JsonContent.Create(body);
                request.Headers.Add("X-Lab-Client", clientId);
                request.Headers.Add("X-Lab-Run", id);
                Increment("sent"); var timer = Stopwatch.StartNew();
                try
                {
                    using var response = await client.SendAsync(request, requestCancel.Token);
                    lock (statsGate)
                    {
                        Increment("responses"); if ((int)response.StatusCode >= 400) Increment("http_errors");
                        var samples = stats.A("response_samples"); while (samples.Count > 7) samples.RemoveAt(0);
                        samples.Add(J.Obj(new { request_id = response.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() ?? "" : "",
                            method = method.Method, path, status = (int)response.StatusCode, client_duration_ms = Math.Round(timer.Elapsed.TotalMilliseconds, 2) }));
                    }
                }
                catch (OperationCanceledException) { Increment(requestCancel.IsCancellationRequested ? "cancelled_requests" : "transport_errors"); }
                catch (HttpRequestException) { Increment("transport_errors"); }
            }
            finally { sem.Release(); }
        }
        try
        {
            int number = 0;
            while (watch.Elapsed.TotalSeconds < spec.N("duration"))
            {
                ct.ThrowIfCancellationRequested(); var elapsed = watch.Elapsed.TotalSeconds;
                var abnormal = spec.N("warmup") <= elapsed && elapsed < spec.N("duration") - 5 && spec.S("scenario") != "normal";
                if (abnormal != anomalyOn)
                {
                    await Faults(abnormal ? J.Obj(new { error_rate = spec.S("scenario") == "errors" ? .6 : 0, delay_ms = spec.S("scenario") == "latency" ? 700 : 0,
                        memory_mb = spec.S("scenario") == "memory" ? 128 : 0, drop_telemetry = spec.S("scenario") == "telemetry_gap", lease_seconds = (int)spec.N("duration") }) : null, ct);
                    anomalyOn = abnormal;
                }
                lock (statsGate) stats["phase"] = abnormal ? "anomaly" : elapsed < spec.N("warmup") ? "warmup" : "recovery";
                requests.RemoveAll(t => t.IsCompletedSuccessfully);
                if (requests.Any(t => t.IsFaulted)) await Task.WhenAll(requests);
                if (requests.Count < clients * 2) requests.Add(Request(number++, abnormal));
                Update(id, Snapshot());
                // No catch-up burst: the configured rate is an upper bound, not a load target.
                await Task.Delay(TimeSpan.FromSeconds(1 / spec.N("rps")), ct);
            }
            await Task.WhenAll(requests); await Faults(ct: ct);
            lock (statsGate) stats["phase"] = "settling";
            Update(id, Snapshot(), "settling");
            await Task.Delay(TimeSpan.FromSeconds(config.Window + config.RecoverySeconds + 2), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { finalStatus = "stopped"; }
        catch (Exception ex) { finalStatus = "failed"; failure = ex.GetType().Name; }
        finally
        {
            await requestCancel.CancelAsync();
            try { await Task.WhenAll(requests); } catch (Exception) { /* The experiment is already failing or stopping. */ }
            try { await Faults(); }
            catch (Exception) { finalStatus = "failed"; failure = "Recovery not confirmed; faults will expire automatically on the target"; }
            using var db = store.Open(); var run = db.Runs.Find(id)!;
            var incidents = db.Incidents.Where(i => i.LastSignal >= run.Started).ToList();
            var expected = Scenarios.Single(s => s.Id == spec.S("scenario")).Expected;
            var detected = incidents.Select(i => i.Rule).Distinct().Order().ToArray();
            var unresolved = incidents.Where(i => i.Status != "resolved").Select(i => i.Rule).Distinct().Order().ToArray();
            stats["phase"] = "finished"; stats["expected"] = J.Arr(expected); stats["detected"] = J.Arr(detected); stats["unresolved"] = J.Arr(unresolved);
            stats["recovery_confirmed"] = unresolved.Length == 0; stats["missing"] = J.Arr(expected.Except(detected).Order()); stats["additional"] = J.Arr(detected.Except(expected).Order());
            stats["evaluation"] = finalStatus != "completed" ? "not-complete" : expected.ToHashSet().SetEquals(detected) && unresolved.Length == 0 ? "matched" : "review";
            stats["error"] = failure; Update(id, Snapshot(), finalStatus, J.Now()); store.Log("test.finished", $"Experiment {spec.S("scenario")}: {finalStatus}");
        }
    }
    public async Task Stop()
    {
        await startGate.WaitAsync();
        try { if (task is { IsCompleted: false }) { await cancellation!.CancelAsync(); await task; } }
        finally { startGate.Release(); }
    }
    public async ValueTask DisposeAsync() { await Stop(); cancellation?.Dispose(); startGate.Dispose(); }
}
