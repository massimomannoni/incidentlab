using System.Text.Json.Nodes;

namespace IncidentLab;

public sealed record RuntimeState(List<JsonObject> Events, double Started, double? LastCycle = null, double? LastSource = null, string Health = "waiting", string? Error = null);

public sealed class MonitorRuntime(LabConfig config, Store store, ITelemetry telemetry, Agents agents)
{
    public RuntimeState State { get; set; } = new([], J.Now());
    public string? AiError { get; private set; }
    public async Task Tick(CancellationToken ct = default)
    {
        var now = J.Now(); var previous = State;
        try
        {
            await telemetry.Sync(ct); var events = await telemetry.Read(now - 120, ct: ct);
            var metrics = events.Where(e => e.S("kind") == "metric" && e.N("ts") <= now).Select(e => e.N("ts")).ToList();
            double? lastSource = metrics.Count > 0 ? metrics.Max() : previous.LastSource;
            var stale = now - (lastSource ?? previous.Started) > config.StaleSeconds;
            var findings = Detection.Evaluate(events, now, config.Window);
            if (stale) findings.Add(new("source-stale", "monitoring", "high", "target-api", "telemetry", "Service telemetry is stale",
                Math.Round(now - (lastSource ?? previous.Started), 1), config.StaleSeconds, [], "Recent samples are missing. Silence does not demonstrate recovery."));
            var health = stale ? "degraded" : metrics.Count > 0 ? "healthy" : "waiting";
            Incidents.Apply(store, findings, now, health == "healthy", config.RecoverySeconds, config.Window);
            State = new(events, previous.Started, now, lastSource, health);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: telemetry store unavailable or analysis window incomplete";
            State = previous with { Health = "degraded", Error = error };
            var findings = new List<Finding> { new("storage-unavailable", "monitoring", "high", "monitor", "storage", "Telemetry analysis suspended", 1, 1, [], error) };
            using var db = store.Open();
            findings.AddRange(db.Incidents.Where(i => i.Category == "monitoring" && i.Status != "resolved" && i.Rule != "storage-unavailable").ToList()
                .Select(i => new Finding(i.Rule, i.Category, i.Severity, "target-api", i.Subject, i.Title, 1, 1, [], i.Report.S("summary"))));
            Incidents.Apply(store, findings, now, false, config.RecoverySeconds, config.Window);
        }
    }
    public void Schedule()
    {
        try { agents.Reconcile(); AiError = null; }
        catch (Exception) { AiError = "AI queue scheduling failed; deterministic detection continues."; }
    }
    public JsonObject Overview()
    {
        var now = J.Now(); var state = State;
        var events = state.Events.Where(e => now - 120 <= e.N("ts") && e.N("ts") <= now).ToList();
        var sourceLag = now - state.LastSource; var cycleLag = now - state.LastCycle;
        var dataState = state.Error != null || (state.Health == "degraded" && sourceLag == null) ? "unavailable" : sourceLag == null ? "waiting" :
            sourceLag > config.StaleSeconds || cycleLag == null || cycleLag > Math.Max(config.StaleSeconds, 2 * config.Interval) ? "stale" : "live";
        var live = dataState == "live"; var recent = events.Where(e => e.S("kind") == "request" && e.N("ts") >= now - 20).ToList();
        var metric = events.Where(e => e.S("kind") == "metric").OrderByDescending(e => e.N("ts")).FirstOrDefault();
        using var db = store.Open();
        return J.Obj(new { health = live ? "healthy" : dataState == "waiting" ? "waiting" : "degraded", error = state.Error, last_cycle = state.LastCycle,
            source_lag = sourceLag.HasValue ? Math.Round(sourceLag.Value, 1) : (double?)null, backend = config.TelemetryBackend,
            total_events = db.Telemetry.Count(), pending_events = config.TelemetryBackend == "opensearch" ? db.Telemetry.Count(r => !r.Sent) : 0,
            data_state = dataState, data_origin = "target-telemetry", measurement_scope = "process", request_count = live ? (int?)recent.Count : null,
            request_window_seconds = 20, requests_per_second = live ? Math.Round(recent.Count / 20d, 1) : (double?)null,
            error_rate = live && recent.Count > 0 ? Math.Round(100d * recent.Count(e => e.N("status") >= 500) / recent.Count, 1) : (double?)null,
            resources = live ? metric : null, histogram = live ? Detection.Histogram(events, now) : [], active_incidents = db.Incidents.Count(i => i.Status != "resolved"),
            ai = agents.Summary(false), incidents = db.Incidents.OrderByDescending(i => i.Opened).Take(100).ToList(), activity = db.Activities.OrderByDescending(a => a.Ts).Take(20).ToList() });
    }
}
