using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace IncidentLab;

/// <summary>Bounded, immutable diagnostic material from measured ingest telemetry; never reads tester expectations.</summary>
public static class DiagnosticEvidence
{
    public static readonly string[] Checks = ["endpoint_breakdown", "command_repetition", "resource_timeline"];
    public const string FollowupStage = "investigator_followup";
    public static string Role(string stage) => stage == FollowupStage ? "investigator" : stage;
    public static bool Enabled(JsonObject packet) => packet.O("diagnostics").N("version") == 1;
    static string Alias(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
    static string Route(JsonObject e) => e.S("path") switch {
        "/items" => "/items", "/commands" => "/commands", "/compute" => "/compute",
        var p when p.StartsWith("/commands/", StringComparison.Ordinal) => "/commands/{id}", _ => "other/redacted"
    };
    static string Method(JsonObject e) => e.S("method") is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS" ? e.S("method") : "unknown/redacted";
    static double? Mean(List<JsonObject> rows, string field) => rows.Any(e => e[field] != null)
        ? Math.Round(rows.Where(e => e[field] != null).Average(e => e.N(field)), 2) : null;
    static JsonObject Summary(List<JsonObject> rows, double start, double end, string id)
    {
        var requests = rows.Where(e => e.S("kind") == "request" && e.S("path") != "/health").ToList();
        var metrics = rows.Where(e => e.S("kind") == "metric" && e.S("scope") == "process").ToList();
        var slots = rows.Where(e => e.S("kind") is "metric" or "request").Select(e => (int)((e.N("ts") - start) / 5)).Distinct().Count();
        var coverage = Math.Min(1, slots / Math.Ceiling((end - start) / 5));
        var latencies = requests.Select(e => e.N("duration_ms")).Order().ToArray();
        return J.Obj(new { id, start, end, observations = rows.Count, observed_bucket_fraction = Math.Round(coverage, 2),
            requests = rows.Count > 0 ? (int?)requests.Count : null,
            requests_per_second = coverage >= .8 ? (double?)Math.Round(requests.Count / (end - start), 2) : null,
            http_5xx = rows.Count > 0 ? (int?)requests.Count(e => e.N("status") >= 500) : null,
            error_rate_percent = requests.Count > 0 ? (double?)Math.Round(100d * requests.Count(e => e.N("status") >= 500) / requests.Count, 2) : null,
            p95_ms = latencies.Length > 0 ? (double?)Math.Round(latencies[(int)Math.Ceiling(.95 * latencies.Length) - 1], 2) : null,
            process_cpu_mean = Mean(metrics, "cpu_percent"), process_memory_mean_mb = Mean(metrics, "memory_mb") });
    }
    public static JsonObject Capture(LabDb db, Incident incident, double capturedAt)
    {
        var end = incident.Evidence.N("window_end", capturedAt);
        var start = incident.Evidence.N("window_start", end - 20);
        var service = incident.Evidence.S("service");
        var result = J.Obj(new { version = 1, source = "persisted-ingest-telemetry", captured_at = capturedAt,
            data_state = "unavailable", scope = "Same service; resource incidents restrict to the affected instance. Metrics describe the process, not the host.",
            limitations = new[] { "Temporal association is not proof of causation.", "No CPU profiler, thread stacks, GC, dependency traces or deployment history are collected.",
                "Coverage counts occupied five-second buckets, not proof of lossless telemetry. No observations means unknown, not zero." },
            service_map = "GET /items reads sample data; POST /commands records a command; POST /compute performs bounded CPU work. No scenario or expected answer is supplied.",
            checks = new JsonObject() });
        if (service.Length == 0 || end > capturedAt + 1 || start >= end || end - start > 120 || !double.IsFinite(start + end)) return result;
        var from = start - 60;
        var rows = db.Telemetry.AsNoTracking().Where(r => r.Ts >= from && r.Ts < end && r.Received <= capturedAt)
            .OrderBy(r => r.Ts).Take(10001).Select(r => r.Payload).ToList();
        if (rows.Count > 10000) { result["data_state"] = "capacity-exceeded"; return result; }
        var scoped = rows.Where(e => e.S("service") == service &&
            (incident.Rule is not ("process-cpu" or "process-memory") || e.S("instance") == incident.Subject)).ToList();
        if (scoped.Count == 0) return result;
        result["data_state"] = "available";
        var before = scoped.Where(e => e.N("ts") < start).ToList();
        var during = scoped.Where(e => e.N("ts") >= start).ToList();
        result["baseline"] = Summary(before, from, start, "ctx-baseline");
        result["during"] = Summary(during, start, end, "ctx-during");
        JsonArray Endpoints(List<JsonObject> source, double a, double b) => J.Arr(source.Where(e => e.S("kind") == "request" && e.S("path") != "/health")
            .GroupBy(e => (route: Route(e), method: Method(e))).OrderByDescending(g => g.Count()).Take(5).Select(g => {
                var v = Summary(g.ToList(), a, b, "unused");
                return J.Obj(new { route = g.Key.route, method = g.Key.method, requests = g.Count(), http_2xx = g.Count(e => e.N("status") is >= 200 and < 300),
                    http_4xx = g.Count(e => e.N("status") is >= 400 and < 500), http_5xx = v["http_5xx"], p95_ms = v["p95_ms"] });
            }));
        var checks = result.O("checks");
        checks["endpoint_breakdown"] = J.Obj(new { id = "check-endpoints", before = Endpoints(before, from, start), during = Endpoints(during, start, end),
            note = "Top five method/route groups per window; other groups may be omitted. Known routes only; unknown paths grouped and identifiers removed. Check method/status before attributing work. Durations are wall-clock time, not per-request CPU." });
        var commands = during.Where(e => e.S("kind") == "request" && e.S("path") == "/commands" && e.S("command_signature").Length > 0).ToList();
        checks["command_repetition"] = J.Obj(new { id = "check-commands", requests_with_signature = commands.Count,
            groups = commands.GroupBy(e => (e.S("client_id"), e.S("command_signature"))).OrderByDescending(g => g.Count()).Take(3)
                .Select(g => new { signature_alias = Alias(g.Key.Item2), client_alias = g.Key.Item1.Length > 0 ? Alias(g.Key.Item1) : "unknown",
                    count = g.Count(), first = g.Min(e => e.N("ts")), last = g.Max(e => e.N("ts")) }).ToArray(),
            note = "Bounded observed repetition, not malicious intent; request contents and client identifiers are excluded." });
        var timeline = new JsonArray();
        // At most twelve ten-second intervals, ending at the detector window end.
        for (var ts = Math.Max(from, end - 120); ts < end; ts += 10)
        {
            var v = Summary(scoped.Where(e => e.N("ts") >= ts && e.N("ts") < ts + 10).ToList(), ts, Math.Min(ts + 10, end), "unused");
            timeline.Add(J.Obj(new { start = ts, end = Math.Min(ts + 10, end), observations = v["observations"], requests = v["requests"],
                cpu = v["process_cpu_mean"], memory_mb = v["process_memory_mean_mb"], p95_ms = v["p95_ms"] }));
        }
        checks["resource_timeline"] = J.Obj(new { id = "check-timeline", buckets = timeline, note = "Before/during only. No subsequent recovery or host-wide claim." });
        return result;
    }
    public static JsonObject VisibleDiagnostics(JsonObject job, string stage)
    {
        var source = job.O("packet").O("diagnostics"); var visible = source.Copy(); visible.Remove("checks");
        var selected = job.O("investigation").S("follow_up", "none");
        if (stage != "investigator" && Checks.Contains(selected) && source.O("checks")[selected] is JsonObject answer)
            visible["check_result"] = J.Obj(new { name = selected, result = answer });
        else if (stage == "investigator") visible["available_checks"] = J.Arr(source.O("checks").Select(x => x.Key));
        return visible;
    }
    public static HashSet<string> References(JsonObject packet, string? selected = null)
    {
        var refs = packet.A("events").Select(e => e.S("id")).ToHashSet();
        var d = packet.O("diagnostics");
        foreach (var field in new[] { "baseline", "during" }) if (d.O(field).S("id") is { Length: > 0 } id) refs.Add(id);
        if (selected != null && Checks.Contains(selected) && d.O("checks").O(selected).S("id") is { Length: > 0 } check) refs.Add(check);
        return refs;
    }
}
