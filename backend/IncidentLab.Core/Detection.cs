using System.Text.Json.Nodes;

namespace IncidentLab;

public sealed record Finding(string Rule, string Category, string Severity, string Service, string Subject,
    string Title, double Value, double Threshold, string[] EvidenceIds, string Detail)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Key => $"{Service}:{Rule}:{Subject}";
}

public static class Detection
{
    public static readonly JsonArray Rules = J.Arr(new[] {
        new { id = "repeat-command", category = "behavior", name = "Repeated command", condition = "≥ 12 requests with the same signature and client in 20 s" },
        new { id = "http-probing", category = "security", name = "HTTP probing", condition = "≥ 12 HTTP 404 responses across ≥ 8 distinct paths per source in 20 s" },
        new { id = "server-errors", category = "application", name = "Server errors", condition = "≥ 5 HTTP 5xx responses and an error rate ≥ 20% in 20 s" },
        new { id = "slow-responses", category = "application", name = "Slow responses", condition = "≥ 8 responses taking at least 500 ms in 20 s" },
        new { id = "process-cpu", category = "resources", name = "Process CPU", condition = "Last 3 samples ≥ 60% of one core" },
        new { id = "process-memory", category = "resources", name = "Process memory", condition = "Last 3 samples ≥ 160 MiB RSS" }
    });

    public static List<Finding> Evaluate(IEnumerable<JsonObject> events, double now, int window = 20)
    {
        var findings = new List<Finding>();
        string[] Ids(IEnumerable<JsonObject> rows) => rows.Take(8).Select(e => e.S("id")).ToArray();
        string Client(JsonObject e) => e.S("client_id") is { Length: > 0 } c ? c : e.S("source_ip") is { Length: > 0 } ip ? ip : "unknown";
        foreach (var service in events.Where(e => now - window <= e.N("ts") && e.N("ts") <= now).GroupBy(e => e.S("service")))
        {
            var requests = service.Where(e => e.S("kind") == "request" && e.S("path") != "/health").ToList();
            foreach (var group in requests.Where(e => e.S("command_signature").Length > 0).GroupBy(e => (Client(e), e.S("command_signature"))))
                if (group.Count() >= 12) findings.Add(new("repeat-command", "behavior", "warning", service.Key, J.Clip($"{group.Key.Item1}/{group.Key.Item2}", 160),
                    "Unusual repetition of the same command", group.Count(), 12, Ids(group), "The signature and client match. Malicious intent is not established; check retries and progress."));
            foreach (var group in requests.Where(e => e.N("status") == 404).GroupBy(e => e.S("source_ip") is { Length: > 0 } ip ? ip : "unknown"))
            {
                var paths = group.Select(e => e.S("path")).Distinct().Count();
                if (group.Count() >= 12 && paths >= 8) findings.Add(new("http-probing", "security", "high", service.Key, group.Key,
                    "Repeated requests to nonexistent HTTP paths", group.Count(), 12, Ids(group), $"{paths} distinct paths. The pattern is consistent with probing; it does not establish a compromise."));
            }
            var errors = requests.Where(e => e.N("status") >= 500).ToList();
            if (errors.Count >= 5 && (double)errors.Count / Math.Max(1, requests.Count) >= .2)
                findings.Add(new("server-errors", "application", "high", service.Key, "api", "Increased service error rate", errors.Count, 5, Ids(errors),
                    $"{errors.Count}/{requests.Count} requests returned HTTP 5xx within the window."));
            var slow = requests.Where(e => e.N("duration_ms") >= 500).ToList();
            if (slow.Count >= 8) findings.Add(new("slow-responses", "application", "warning", service.Key, "api", "Slow application responses", slow.Count, 8, Ids(slow),
                "Response time is at least 500 ms; check saturation and dependencies."));
            foreach (var instance in service.Where(e => e.S("kind") == "metric").GroupBy(e => e.S("instance")))
            {
                var last = instance.OrderByDescending(e => e.N("ts")).Take(3).ToList();
                foreach (var (field, threshold, rule, title) in new[] { ("cpu_percent", 60, "process-cpu", "High process CPU usage"), ("memory_mb", 160, "process-memory", "High process memory usage") })
                    if (last.Count == 3 && last.All(e => e[field] != null && e.N(field) >= threshold))
                        findings.Add(new(rule, "resources", "warning", service.Key, instance.Key, title, Math.Round(last.Average(e => e.N(field)), 1), threshold, Ids(last),
                            "Process measurement, not host-wide usage. Three consecutive samples exceeded the threshold."));
            }
        }
        return findings;
    }

    public static JsonArray Histogram(IEnumerable<JsonObject> events, double now, int bins = 24, int width = 5)
    {
        var source = events.ToList(); var result = new JsonArray();
        for (int i = 0; i < bins; i++)
        {
            var start = now - (bins - i) * width;
            var samples = source.Where(e => start <= e.N("ts") && e.N("ts") < start + width).ToList();
            var rows = samples.Where(e => e.S("kind") == "request").ToList();
            var observed = samples.Any(e => e.S("kind") is "request" or "metric");
            result.Add(J.Obj(new { ts = start, observed, requests = observed ? (int?)rows.Count : null, errors = observed ? (int?)rows.Count(e => e.N("status") >= 400) : null }));
        }
        return result;
    }
}

public static class Incidents
{
    public static void Apply(Store store, IEnumerable<Finding> findings, double now, bool allowRecovery, int recoverySeconds, int windowSeconds = 20) => store.Write(db =>
    {
        var active = db.Incidents.Where(i => i.Status != "resolved").ToDictionary(i => i.Fingerprint);
        var keys = new HashSet<string>();
        foreach (var f in findings)
        {
            if (!keys.Add(f.Key)) continue;
            var priorRuns = active.TryGetValue(f.Key, out var previous) ? previous.Evidence.A("run_ids").Select(x => x!.GetValue<string>()).ToArray() : [];
            if (!active.TryGetValue(f.Key, out var incident))
            {
                incident = new() { Fingerprint = f.Key, Severity = f.Severity, Category = f.Category, Rule = f.Rule, Subject = f.Subject,
                    Title = f.Title, Opened = now, Updated = now, LastSignal = now, Evidence = J.Obj(f),
                    Report = J.Obj(new { origin = "deterministic-template", ai_status = "separate-agent-record", summary = f.Detail, conclusion = "Rule threshold exceeded; cause and intent are undetermined." }) };
                db.Incidents.Add(incident);
                db.Notices.Add(new() { IncidentId = incident.Id, Ts = now, Kind = "opened", Body = J.Obj(new { title = f.Title, severity = f.Severity, message = f.Detail, ai = "Not run" }) });
                db.Activities.Add(new() { Ts = now, Type = "incident.opened", Message = f.Title });
            }
            else
            {
                incident.Status = "active"; incident.Updated = incident.LastSignal = now; incident.HealthySince = null;
                incident.Evidence = J.Obj(f); incident.Report["summary"] = f.Detail;
            }
            // Record the detector's actual clock, not the later AI capture/finish time.
            if (f.Category != "monitoring")
            {
                incident.Evidence["window_start"] = now - windowSeconds;
                incident.Evidence["window_end"] = now;
            }
            // Correlation is based on tagged evidence, never merely overlapping timestamps.
            var taggedRuns = f.EvidenceIds.Select(id => db.Telemetry.Find(id)?.Payload.S("run_id") ?? "")
                .Where(id => id.Length > 0 && db.Runs.Find(id) != null);
            incident.Evidence["run_ids"] = J.Arr(priorRuns.Concat(taggedRuns).Distinct().Take(30));
        }
        foreach (var (key, incident) in active)
        {
            if (keys.Contains(key)) continue;
            if (!allowRecovery && incident.Category != "monitoring") { incident.HealthySince = null; incident.Status = "active"; continue; }
            if (incident.HealthySince == null) { incident.HealthySince = now; incident.Status = "recovering"; }
            else if (now - incident.HealthySince >= recoverySeconds)
            {
                incident.Status = "resolved"; incident.Closed = incident.Updated = now;
                db.Notices.Add(new() { IncidentId = incident.Id, Ts = now, Kind = "resolved", Body = J.Obj(new { title = incident.Title, severity = incident.Severity,
                    message = "The condition has cleared with fresh telemetry and a confirmed healthy period.", ai = "Not run" }) });
                db.Activities.Add(new() { Ts = now, Type = "incident.resolved", Message = incident.Title });
            }
        }
    });
}
