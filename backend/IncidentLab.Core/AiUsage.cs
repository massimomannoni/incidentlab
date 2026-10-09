using System.Text.Json.Nodes;

namespace IncidentLab;

public static class AiUsage
{
    public static readonly string[] TokenKeys = ["input_tokens", "cached_input_tokens", "output_tokens", "reasoning_output_tokens"];
    public static JsonObject Safe(JsonObject value)
    {
        var usage = new JsonObject();
        foreach (var (key, v) in value)
            if (TokenKeys.Contains(key) && v != null && long.TryParse(v.ToJsonString(), out var n) && n is >= 0 and <= 100_000_000) usage[key] = n;
        foreach (var (subset, total) in new[] { ("cached_input_tokens", "input_tokens"), ("reasoning_output_tokens", "output_tokens") })
            if (usage.ContainsKey(subset) && usage.ContainsKey(total) && usage.N(subset) > usage.N(total)) usage.Remove(subset);
        return usage;
    }
    public static void Close(LabDb db, AgentJob job, string status, string error)
    {
        foreach (var row in db.Sessions.Where(s => s.JobId == job.Id && s.Status == "running"))
        { row.Status = status; row.Ended = J.Now(); row.Error = J.Clip(error, 100); }
    }
    public static List<JsonObject> Sessions(LabDb db, IEnumerable<AgentJob>? jobs = null)
    {
        var byJob = (jobs ?? db.Jobs.ToList()).ToDictionary(j => j.Id);
        var ids = byJob.Keys.ToArray();
        var rows = db.Sessions.Where(s => ids.Contains(s.JobId)).ToList().Select(J.Obj).ToList();
        var indexed = rows.Select(r => (r.S("job_id"), r.S("role"))).ToHashSet();
        foreach (var ev in db.AgentEvents.Where(e => ids.Contains(e.JobId) && e.Message == "Codex invocation started.").ToList())
        {
            if (indexed.Contains((ev.JobId, ev.Stage))) continue;
            var job = byJob[ev.JobId]; var report = ev.Stage == "investigator" ? job.Investigation : job.Decision;
            rows.Add(J.Obj(new { id = "legacy-" + ev.Id, job_id = job.Id, incident_id = job.IncidentId, role = ev.Stage, started = ev.Ts, ended = (double?)null,
                status = report != null ? "completed" : job.Status, config = new { provider = "codex", model_id = job.Settings.S("model_id"), model_family = "unknown", reasoning_effort = "unknown" },
                provider_session_id = (string?)null, usage = Safe(job.Usage.O(ev.Stage)), report = (object?)null, error = (string?)null, legacy = true }));
        }
        foreach (var row in rows)
        {
            var job = byJob[row.S("job_id")]; var usage = Safe(row.O("usage"));
            row["rule"] = job.Packet.S("rule"); row["job_status"] = job.Status; row["legacy"] = row.B("legacy"); row["usage"] = usage;
            var status = usage.ContainsKey("input_tokens") && usage.ContainsKey("output_tokens") ? "reported" : usage.Count > 0 ? "partial" : "unavailable";
            row["usage_status"] = status;
            row["budget_cost_usd"] = row.O("config")["budget_cost_usd"]?.DeepClone();
            row["reserved_usd"] = row.O("config").S("budget_status") == "reserved" ? row.O("config")["reserved_usd"]?.DeepClone() : null;
            row["total_tokens"] = status == "reported" ? (long?)(usage.N("input_tokens") + usage.N("output_tokens")) : null;
            row["duration_seconds"] = row["ended"] != null ? Math.Round(Math.Max(0, row.N("ended") - row.N("started")), 2) : null;
        }
        return rows.OrderByDescending(r => r.N("started")).ToList();
    }
    public static JsonObject Totals(IEnumerable<JsonObject> source)
    {
        var rows = source.ToList(); var counters = new JsonObject();
        foreach (var key in TokenKeys) counters[key] = rows.Any(r => r.O("usage").ContainsKey(key)) ? (long?)rows.Sum(r => r.O("usage").N(key)) : null;
        var reported = rows.Count(r => r.S("usage_status") == "reported");
        var known = rows.Any(r => r.O("usage").ContainsKey("input_tokens") || r.O("usage").ContainsKey("output_tokens"));
        return J.Obj(new { sessions = rows.Count, completed = rows.Count(r => r.S("status") == "completed"), failed = rows.Count(r => r.S("status") == "failed"),
            cancelled = rows.Count(r => r.S("status") == "cancelled"), running = rows.Count(r => r.S("status") == "running"), usage_reported = reported,
            usage_incomplete = rows.Count - reported, tokens = counters,
            reported_tokens = known || rows.Count == 0 ? (long?)(counters.N("input_tokens") + counters.N("output_tokens")) : null, cost_usd = (double?)null,
            budget_cost_usd = rows.Any(r => r["budget_cost_usd"] != null) || rows.Count == 0 ? (decimal?)rows.Sum(r => BedrockBudget.Amount(r, "budget_cost_usd")) : null,
            reserved_usd = rows.Sum(r => BedrockBudget.Amount(r, "reserved_usd")),
            cost_unavailable_sessions = rows.Count(r => r["budget_cost_usd"] == null) });
    }
    public static JsonObject Report(LabDb db, string? role, string? incidentId, double? since)
    {
        var rows = Sessions(db).Where(r => (role == null || DiagnosticEvidence.Role(r.S("role")) == role) && (incidentId == null || r.S("incident_id") == incidentId) && (since == null || r.N("started") >= since)).ToList();
        JsonArray Groups(Func<JsonObject, string> key) => J.Arr(rows.GroupBy(key).Select(g => { var t = Totals(g); t["key"] = g.Key; return t; }));
        var reports = rows.Select(r => { var copy = r.Copy(); copy.Remove("report"); return copy; });
        return J.Obj(new { generated_at = J.Now(), filters = new { role, incident_id = incidentId, since }, totals = Totals(rows),
            by_agent = Groups(r => DiagnosticEvidence.Role(r.S("role"))), by_incident = Groups(r => r.S("incident_id")), by_job = Groups(r => r.S("job_id")),
            by_model = Groups(r => r.O("config").S("model_id") is { Length: > 0 } m ? m : "CLI default (unresolved)"), sessions = reports,
            billing = BedrockBudget.Billing + " This laboratory only, not whole-account usage.",
            accounting = "Total = input + output. Cached input and reasoning output are subsets, never added twice. Missing usage is unknown, not zero. Attempts can fail before inference." });
    }
}
