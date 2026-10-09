using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace IncidentLab;

/// <summary>Read-only incident context. Never changes a job's hashed evidence or invokes AI.</summary>
public static class IncidentContext
{
    public static JsonObject Read(LabDb db, Incident incident, AgentJob? job, double now)
    {
        var packet = job?.Packet;
        var evidence = packet ?? incident.Evidence;
        var captured = packet?.N("captured_at") ?? incident.LastSignal;
        var exact = evidence["window_start"] != null && evidence["window_end"] != null;
        var start = exact ? evidence.N("window_start") : captured - (packet?.N("detector_window_seconds", 20) ?? 20);
        var end = exact ? evidence.N("window_end") : captured;
        var windowKnown = incident.Category != "monitoring" && end > start && end - start <= 120;
        var center = windowKnown ? (start + end) / 2 : captured;
        var from = center - 60;
        var until = center + 60;
        var service = incident.Evidence.S("service");
        var resource = incident.Rule is "process-cpu" or "process-memory";
        // The durable ingest mirror is the same measured telemetry forwarded to OpenSearch.
        // Read at most 10,001 rows; never turn a truncated query into a plausible chart.
        var rows = db.Telemetry.AsNoTracking().Where(x => x.Ts >= from && x.Ts <= until)
            .OrderBy(x => x.Ts).Take(10001).Select(x => x.Payload).ToList();
        var complete = rows.Count <= 10000;
        var scoped = complete && service.Length > 0 ? rows.Where(e => e.S("service") == service && e.N("ts") <= now
            && (!resource || e.S("instance") == incident.Subject)).ToList() : [];
        var bins = new JsonArray();
        if (complete && service.Length > 0)
            for (var i = 0; i < 24; i++)
            {
                var ts = from + i * 5;
                var samples = scoped.Where(e => e.N("ts") >= ts && e.N("ts") < ts + 5).ToList();
                var requests = samples.Where(e => e.S("kind") == "request" && e.S("path") != "/health").ToList();
                var observed = samples.Any(e => e.S("kind") is "request" or "metric");
                var cpu = samples.Where(e => e.S("kind") == "metric" && e["cpu_percent"] != null).ToList();
                var memory = samples.Where(e => e.S("kind") == "metric" && e["memory_mb"] != null).ToList();
                bins.Add(J.Obj(new { ts, observed, requests = observed ? (int?)requests.Count : null,
                    errors = observed ? (int?)requests.Count(e => e.N("status") >= 400) : null,
                    http_404 = observed ? (int?)requests.Count(e => e.N("status") == 404) : null,
                    cpu_percent = cpu.Count > 0 ? (double?)cpu.Max(e => e.N("cpu_percent")) : null,
                    memory_mb = memory.Count > 0 ? (double?)memory.Max(e => e.N("memory_mb")) : null }));
            }
        var runs = new JsonArray();
        foreach (var id in incident.Evidence.A("run_ids").Select(x => x!.GetValue<string>()).Distinct().Take(30))
        {
            var run = db.Runs.Find(id);
            if (run != null) runs.Add(J.Obj(new { id = run.Id, scenario = run.Scenario, started = run.Started, status = run.Status, correlation = "tagged-request-evidence" }));
        }
        return J.Obj(new { service = service.Length > 0 ? service : null,
            instances = scoped.Select(e => e.S("instance")).Where(s => s.Length > 0).Distinct().Take(10).ToArray(),
            runs, source = "persisted-ingest-telemetry", scope = resource
                ? "Affected instance only. Measured process peaks per bucket, not host-wide metrics."
                : "Service traffic; excludes /health. Not limited to one client or simulation. Process metrics, not host metrics.",
            from, until, bin_seconds = 5, captured_at = captured,
            window_start = windowKnown ? (double?)start : null, window_end = windowKnown ? (double?)end : null,
            window_precision = windowKnown ? exact ? "recorded" : "estimated-legacy" : "unavailable",
            data_state = !complete ? "capacity-exceeded" : service.Length == 0 ? "unknown-service" : scoped.Count == 0 ? "unavailable" : "available",
            histogram = bins });
    }
}
