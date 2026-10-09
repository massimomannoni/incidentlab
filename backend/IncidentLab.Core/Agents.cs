using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace IncidentLab;

public sealed class Agents(Store store, LabConfig config)
{
    public static JsonObject Settings(LabDb db) => Contracts.Settings(db.SettingValue("ai") ?? new());
    public static JsonObject Catalog(LabDb db) => db.SettingValue("ai_catalog") ?? J.Obj(new { models = Array.Empty<object>(), updated = (double?)null, source = "Worker catalog unavailable" });
    public static JsonObject Profiles(JsonObject settings, JsonObject catalog)
    {
        if (BedrockBudget.IsBedrock(settings))
        {
            var selected = new JsonObject();
            foreach (var role in Contracts.Roles.Where(r => r != "judge" || settings.B("judge_enabled"))) selected[role] = BedrockBudget.Profile;
            return selected;
        }
        var profiles = settings.O("agent_profiles"); var models = catalog.A("models"); var result = new JsonObject();
        foreach (var role in Contracts.Roles)
        {
            if (role == "judge" && !settings.B("judge_enabled")) continue;
            var profile = profiles.O(role); var id = profile.S("model_id");
            var model = models.FirstOrDefault(m => id.Length > 0 ? m.S("id") == id : m.B("is_default"));
            void Invalid(string why) => throw new LabException(422, $"{role}: {why}");
            if (id.Length > 0 && model == null) Invalid("model is not in the worker catalog; restart the worker to refresh it");
            if (profile.S("model_family") != "auto" && (model == null || profile.S("model_family") != model.S("family"))) Invalid("model does not match the selected family");
            if (profile.S("reasoning_effort") != "default" && (model == null || !model.A("efforts").Any(e => e!.GetValue<string>() == profile.S("reasoning_effort")))) Invalid("reasoning effort is not supported by the selected model");
            var selected = profile.Copy(); selected["provider"] = "codex";
            selected["model_id"] = model?.S("id") ?? id; selected["model_family"] = model?.S("family") ?? profile.S("model_family");
            selected["reasoning_effort"] = model != null && profile.S("reasoning_effort") == "default" ? model.S("default_effort") : profile.S("reasoning_effort");
            selected["selection_source"] = model != null ? "worker_catalog" : "cli_default_unresolved"; result[role] = selected;
        }
        return result;
    }
    static void Event(LabDb db, AgentJob job, string stage, string message)
    {
        db.AgentEvents.Add(new() { JobId = job.Id, Stage = stage, Message = J.Clip(message, 500) }); job.Updated = J.Now();
    }
    public JsonObject SaveSettings(JsonObject input)
    {
        var value = Contracts.Settings(input);
        if (value.B("enabled") && value.S("execution_mode") != "codex_local" && !BedrockBudget.IsBedrock(value)) throw new LabException(422, "Select Codex local worker or Amazon Bedrock");
        if (value.B("enabled") && value.S("execution_mode") == "codex_local" && config.WorkerKey.Length < 24) throw new LabException(422, "Configure LAB_WORKER_KEY (at least 24 characters) on monitor and worker first");
        if (value.B("enabled") && BedrockBudget.IsBedrock(value)) BedrockBudget.Validate(value, config);
        store.Write(db =>
        {
            if (value.B("enabled"))
            {
                if (value.S("execution_mode") == "codex_local" && value.S("provider") != "codex") throw new LabException(422, "Codex local mode requires the Codex provider");
                Profiles(value, Catalog(db));
            }
            var old = Settings(db);
            if (value.B("enabled") && value.B("auto_analyze") && (!(old.B("enabled") && old.B("auto_analyze")) || old.S("execution_mode") != value.S("execution_mode"))) db.PutSetting("ai_auto_since", J.Obj(new { ts = J.Now() }));
            if (!value.B("enabled") || old.S("execution_mode") != value.S("execution_mode"))
                foreach (var job in db.Jobs.Where(j => j.Status == "queued" || j.Status == "running").ToList())
                {
                    job.Status = "cancelled"; job.Error = "AI disabled by operator"; AiUsage.Close(db, job, "cancelled", job.Error);
                    Event(db, job, job.Stage, "Cancelled by operator. Worker stops at its next heartbeat; an in-flight request may already consume usage.");
                }
            db.PutSetting("ai", value);
        });
        store.Log("settings.updated", $"AI mode: {value.S("execution_mode")}; enabled={value.B("enabled")}; automatic={value.B("auto_analyze")}");
        return value;
    }
    public JsonObject Packet(LabDb db, Incident incident, int limit)
    {
        var events = new JsonArray(); var evidence = incident.Evidence.A("evidence_ids");
        foreach (var id in evidence.Take(limit))
        {
            var row = db.Telemetry.Find(id!.GetValue<string>()); if (row == null) continue;
            var e = new JsonObject();
            foreach (var key in new[] { "id", "ts", "kind", "scope", "status", "duration_ms", "cpu_percent", "memory_mb", "request_id" }) e[key] = row.Payload[key]?.DeepClone();
            events.Add(e);
        }
        return J.Obj(new { incident_id = incident.Id, rule = incident.Rule, category = incident.Category, severity = incident.Severity,
            status_at_capture = incident.Status, captured_at = J.Now(), opened = incident.Opened, observed_value = incident.Evidence["value"], threshold = incident.Evidence["threshold"], events,
            detector_window_seconds = config.Window, window_start = incident.Evidence["window_start"], window_end = incident.Evidence["window_end"],
            rule_condition = Detection.Rules.FirstOrDefault(r => r.S("id") == incident.Rule)?.S("condition").Replace("20 s", $"{config.Window} s") ?? "Monitoring availability rule",
            rule_summary = J.Clip(incident.Report.S("summary"), 1500), scope = "Controlled laboratory; fault injection is possible, not proven by this evidence. Metrics are process-scoped.",
            missing_evidence_count = Math.Max(0, evidence.Count - events.Count), diagnostics = DiagnosticEvidence.Capture(db, incident, J.Now()) });
    }
    public AgentJob Enqueue(LabDb db, Incident incident, JsonObject settings, double now)
    {
        var prior = db.Jobs.Local.FirstOrDefault(j => j.IncidentId == incident.Id) ?? db.Jobs.FirstOrDefault(j => j.IncidentId == incident.Id);
        if (prior != null) return prior;
        var day = now - now % 86400;
        var pendingAdded = db.Jobs.Local.Count(j => db.Entry(j).State == Microsoft.EntityFrameworkCore.EntityState.Added && j.Ts >= day);
        if (db.Jobs.Count(j => j.Ts >= day) + pendingAdded >= settings.N("daily_job_limit")) throw new LabException(429, "Daily job limit reached (UTC). No model was invoked.");
        var snapshot = settings.Copy(); snapshot["resolved_profiles"] = Profiles(settings, Catalog(db));
        var job = new AgentJob { IncidentId = incident.Id, Ts = now, Updated = now, Settings = snapshot, Packet = Packet(db, incident, (int)settings.N("max_evidence_events")) };
        db.Jobs.Add(job);
        Event(db, job, "detector", "Incident admitted from a real rule detection; one AI job per incident.");
        Event(db, job, "evidence", "Bounded evidence snapshot captured; free-form logs and request contents excluded.");
        Event(db, job, "context", "Baseline and incident aggregates captured from measured telemetry. At most one frozen read-only diagnostic check; collection uses no model calls.");
        Event(db, job, "queue", BedrockBudget.IsBedrock(settings) ? "Waiting for the AWS Bedrock executor and budget admission. No model call yet." : "Waiting for an authenticated local Codex worker. No model call yet.");
        return job;
    }
    static void Expire(LabDb db, double now)
    {
        foreach (var job in db.Jobs.Where(j => j.Status == "running" && j.Deadline < now).ToList())
        {
            job.Status = "failed"; job.Error = "Worker lease expired. No automatic retry; usage may be incomplete.";
            AiUsage.Close(db, job, "failed", "lease_expired"); Event(db, job, job.Stage, job.Error);
        }
    }
    public void Reconcile() => store.Write(db =>
    {
        var now = J.Now(); Expire(db, now); var cfg = Settings(db); var since = db.SettingValue("ai_auto_since");
        if (!cfg.B("enabled") || cfg.S("execution_mode") is not ("codex_local" or "cloud_api") || !cfg.B("auto_analyze") || since == null) return;
        var ts = since.N("ts");
        foreach (var incident in db.Incidents.Where(i => i.Opened >= ts && i.Status != "resolved").OrderBy(i => i.Opened).ToList())
            try { Enqueue(db, incident, cfg, now); } catch (LabException) { break; }
    });
    public JsonObject Analyze(string id) => store.Write(db =>
    {
        var cfg = Settings(db);
        if (!cfg.B("enabled") || cfg.S("execution_mode") is not ("codex_local" or "cloud_api")) throw new LabException(409, "Enable an AI execution provider in Settings first");
        var incident = db.Incidents.Find(id) ?? throw new LabException(404, "Incident not found");
        var job = Enqueue(db, incident, cfg, J.Now()); return J.Obj(new { id = job.Id, status = job.Status });
    });
    public static JsonObject PublicJob(LabDb db, AgentJob job)
    {
        var data = J.Obj(job); data.Remove("lease");
        data["timeline"] = J.Arr(db.AgentEvents.Where(e => e.JobId == job.Id).OrderBy(e => e.Ts).ToList());
        data["packet_sha256"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(J.Canonical(job.Packet)))).ToLowerInvariant();
        var sessions = AiUsage.Sessions(db, [job]); data["sessions"] = J.Arr(sessions); data["judge"] = sessions.FirstOrDefault(s => s.S("role") == "judge")?["report"]?.DeepClone();
        return data;
    }
    public JsonObject Summary(bool includeJobs = true)
    {
        using var db = store.Open(); var cfg = Settings(db); var jobs = db.Jobs.OrderByDescending(j => j.Ts).ToList();
        var state = db.SettingValue(BedrockBudget.IsBedrock(cfg) ? "bedrock_worker" : "ai_worker"); var usage = AiUsage.Totals(AiUsage.Sessions(db, jobs));
        var data = J.Obj(new { enabled = cfg.B("enabled"), mode = cfg.S("execution_mode"), calls = db.AgentEvents.Count(e => e.Message == "Codex invocation started." || e.Message == BedrockBudget.StartMessage),
            tokens = usage["reported_tokens"], usage, cost_usd = usage["budget_cost_usd"], billing = BedrockBudget.Billing, worker = state,
            budget = BedrockBudget.Snapshot(db, config, J.Now()),
            worker_online = state != null && J.Now() - state.N("last_seen") < 20, queued = jobs.Count(j => j.Status == "queued"), running = jobs.Count(j => j.Status == "running") });
        if (includeJobs) data["jobs"] = J.Arr(jobs.Take(50).Select(j => PublicJob(db, j)));
        return data;
    }
    public void Authorize(string key)
    {
        if (config.WorkerKey.Length < 24 || !J.SecretEquals(key, config.WorkerKey)) throw new LabException(401, "Invalid worker credential");
    }
    static AgentJob Owned(LabDb db, string id, JsonObject body, string executionMode)
    {
        var job = db.Jobs.Find(id);
        if (job == null || job.WorkerId != body.S("worker_id") || job.Lease == null || !J.SecretEquals(job.Lease, body.S("lease"))) throw new LabException(409, "Job is not owned by this worker");
        if (job.Status != "running" || job.Deadline == null || job.Deadline < J.Now()) throw new LabException(409, "Job no longer running or lease expired");
        var cfg = Settings(db);
        if (!cfg.B("enabled") || cfg.S("execution_mode") != executionMode || job.Settings.S("execution_mode") != executionMode) throw new LabException(409, "AI execution is disabled or its provider changed");
        return job;
    }
    public JsonObject PutCatalog(JsonObject body) => store.Write(db =>
    {
        var models = body.A("models");
        if (models.Select(m => m.S("id")).Distinct().Count() != models.Count || models.Count(m => m.B("is_default")) > 1 || models.Any(m => !m.A("efforts").Any(e => e!.GetValue<string>() == m.S("default_effort"))))
            throw new LabException(422, "Invalid model catalog");
        db.PutSetting("ai_catalog", J.Obj(new { models, updated = J.Now(), source = "Codex app-server model/list; catalog, not an entitlement guarantee", worker_id = body.S("worker_id") }));
        return J.Obj(new { models = models.Count });
    });
    public JsonObject Claim(JsonObject body, string executionMode = "codex_local") => store.Write<JsonObject>(db =>
    {
        var now = J.Now(); db.PutSetting(executionMode == "cloud_api" ? "bedrock_worker" : "ai_worker", J.Obj(new { worker_id = body.S("worker_id"), last_seen = now })); Expire(db, now);
        // Persist expired states before querying the next claim, inside this transaction.
        db.SaveChanges(); var cfg = Settings(db);
        if (!cfg.B("enabled") || cfg.S("execution_mode") != executionMode || db.Jobs.Any(j => j.Status == "running")) return new() { ["job"] = null };
        var job = db.Jobs.Where(j => j.Status == "queued").OrderBy(j => j.Ts).ToList().FirstOrDefault(j => j.Settings.S("execution_mode") == executionMode);
        if (job == null) return new() { ["job"] = null };
        job.Status = "running"; job.Stage = "investigator"; job.WorkerId = body.S("worker_id"); job.Lease = J.Id() + J.Id();
        job.Deadline = now + (job.Settings.B("judge_enabled") ? 4 : 3) * job.Settings.N("timeout_seconds") + 60;
        Event(db, job, "investigator", "Worker claimed this incident. Investigator ready."); return new() { ["job"] = J.Obj(job) };
    });
    public JsonObject Heartbeat(string id, JsonObject body, string executionMode = "codex_local") => store.Write(db =>
    {
        var job = Owned(db, id, body, executionMode); db.PutSetting(executionMode == "cloud_api" ? "bedrock_worker" : "ai_worker", J.Obj(new { worker_id = body.S("worker_id"), last_seen = J.Now() }));
        return J.Obj(new { stage = job.Stage });
    });
    public JsonObject Started(string id, JsonObject body, string executionMode = "codex_local") => store.Write(db =>
    {
        var job = Owned(db, id, body, executionMode);
        if (db.Sessions.Any(s => s.JobId == id && s.Role == job.Stage) || db.AgentEvents.Any(e => e.JobId == id && e.Stage == job.Stage && e.Message == "Codex invocation started.")) throw new LabException(409, "Stage already started; automatic retries are prohibited");
        var profile = job.Settings.O("resolved_profiles")[DiagnosticEvidence.Role(job.Stage)]?.AsObject() ?? J.Obj(new { provider = "codex", model_id = job.Settings.S("model_id"), model_family = "unknown", reasoning_effort = "default" });
        var session = new AgentSession { JobId = id, IncidentId = job.IncidentId, Role = job.Stage, Config = profile.Copy() };
        if (executionMode == "cloud_api") BedrockBudget.Reserve(db, session, job.Settings, config, J.Now());
        db.Sessions.Add(session); Event(db, job, job.Stage, executionMode == "cloud_api" ? BedrockBudget.StartMessage : "Codex invocation started.");
        return J.Obj(new { stage = job.Stage, session_id = session.Id });
    });
    public JsonObject Result(string id, JsonObject body, string executionMode = "codex_local") => store.Write(db =>
    {
        var job = Owned(db, id, body, executionMode); var stage = body.S("stage");
        if (job.Stage != stage) throw new LabException(409, "Unexpected stage");
        if (!db.Sessions.Any(s => s.JobId == id && s.Role == stage)) throw new LabException(409, "Stage was not started");
        JsonObject report;
        try
        {
            report = Contracts.Validate(body["report"], Contracts.ReportSchema(stage, DiagnosticEvidence.Enabled(job.Packet))).AsObject();
            if (Encoding.UTF8.GetByteCount(report.ToJsonString()) > 14000) throw new LabException(422, "Report too large");
            var selected = stage == "investigator" ? null : job.Investigation?.S("follow_up");
            var known = DiagnosticEvidence.References(job.Packet, selected);
            var investigator = DiagnosticEvidence.Role(stage) == "investigator";
            var references = investigator ? report.A("findings").SelectMany(f => f.A("evidence_ids"))
                .Concat(report.O("diagnosis").A("evidence_refs"))
                .Concat(report.A("hypotheses").SelectMany(h => h.A("supporting_refs").Concat(h.A("contradicting_refs"))))
                : stage == "judge" ? report.A("evidence_ids") : Enumerable.Empty<JsonNode?>();
            if (references.Any(e => !known.Contains(e!.GetValue<string>()))) throw new LabException(422, "report_unknown_reference");
            if (investigator)
            {
                var check = report.S("follow_up", "none");
                if (check != "none" && (!DiagnosticEvidence.Enabled(job.Packet) || job.Packet.O("diagnostics").O("checks")[check] == null))
                    throw new LabException(422, "report_check_unavailable");
                if (stage == DiagnosticEvidence.FollowupStage)
                {
                    if (check != "none" && check != selected) throw new LabException(422, "report_second_check");
                    // A final 'none' means no further request, not cancellation of the completed check.
                    // Store the original selection as audit metadata; never dispatch another read/call.
                    report["follow_up"] = selected;
                }
                if (report.O("diagnosis").S("status") == "likely_cause" && report.O("diagnosis").A("evidence_refs").Count == 0)
                    throw new LabException(422, "report_cause_without_reference");
            }
        }
        catch (LabException ex) when (ex.Message is not ("report_unknown_reference" or "report_check_unavailable" or "report_second_check" or "report_cause_without_reference"))
        { throw new LabException(422, "Invalid structured report or evidence references"); }
        var usage = AiUsage.Safe(body.O("usage")); job.Usage[stage] = usage.Copy();
        var session = db.Sessions.SingleOrDefault(s => s.JobId == id && s.Role == stage) ?? throw new LabException(409, "Stage session missing");
        session.Status = "completed"; session.Ended = J.Now(); session.Report = report.Copy(); session.Usage = usage; session.ProviderSessionId = body["provider_session_id"]?.GetValue<string>();
        BedrockBudget.Settle(db, session);
        if (DiagnosticEvidence.Role(stage) == "investigator")
        {
            job.Investigation = report; job.Stage = "manager";
            Event(db, job, stage, "Investigation structure validated and saved. References checked; this is not proof that the diagnosis is correct.");
            if (stage == "investigator" && report.S("follow_up", "none") != "none")
            {
                job.Stage = DiagnosticEvidence.FollowupStage;
                Event(db, job, "diagnostic_check", "Read-only frozen check supplied: " + report.S("follow_up") + ". Original packet unchanged. One additional Investigator call, subject to budget; no second check.");
            }
            else Event(db, job, "manager", "Manager received the investigation and notification policy; assess actionable value beyond the rule notice.");
        }
        else if (stage == "manager")
        {
            job.Decision = report; Event(db, job, "manager", "Escalation proposal validated and saved.");
            if (job.Settings.B("judge_enabled")) { job.Stage = "judge"; Event(db, job, "judge", "Second judge requested. Notification proposal held until review completes."); }
            else Deliver(db, job);
        }
        else { Event(db, job, "judge", "Independent review validated: " + report.S("verdict")); Deliver(db, job, report); }
        return J.Obj(new { status = job.Status, stage = job.Stage, job });
    });
    public JsonObject Failed(string id, JsonObject body, string executionMode = "codex_local") => store.Write(db =>
    {
        var job = Owned(db, id, body, executionMode); job.Status = "failed"; job.Error = body.S("reason");
        var session = db.Sessions.SingleOrDefault(s => s.JobId == id && s.Role == job.Stage);
        if (session != null) { session.Usage = AiUsage.Safe(body.O("usage")); session.ProviderSessionId = body["provider_session_id"]?.GetValue<string>(); job.Usage[job.Stage] = session.Usage.Copy(); }
        if (session != null) BedrockBudget.Settle(db, session);
        AiUsage.Close(db, job, "failed", job.Error); Event(db, job, job.Stage, $"Execution failed: {job.Error}. No AI fallback or automatic retry.");
        return J.Obj(new { status = job.Status });
    });
    static void Deliver(LabDb db, AgentJob job, JsonObject? judgment = null)
    {
        var report = job.Decision!; var incident = db.Incidents.Find(job.IncidentId)!;
        var urgent = incident.Severity is "high" or "critical"; var disputed = judgment != null && judgment.S("verdict") != "agree";
        var action = urgent || disputed ? "escalate" : report.S("action");
        string[] ranks = ["low", "warning", "high", "critical"];
        var priority = ranks[Math.Max(Array.IndexOf(ranks, incident.Severity), Array.IndexOf(ranks, report.S("priority")))];
        var reason = disputed ? "Judge requires human review. Manager draft withheld; a review request is delivered instead." : urgent ? "High-severity rules require human escalation." : "Manager decision accepted within laboratory policy.";
        var channels = new JsonArray { J.Obj(new { channel = "lab-inbox", status = action != "observe" ? "delivered" : "suppressed" }) };
        foreach (var c in new[] { "slack", "telegram", "email" }) channels.Add(J.Obj(new { channel = c, status = "not-configured" }));
        var policy = J.Obj(new { proposed_action = report.S("action"), effective_action = action, priority, human_review_required = action == "escalate" || report.B("human_review_required"),
            reason, incident_status_at_decision = incident.Status, judge_verdict = judgment?.S("verdict") ?? "not_requested", channels, remediation = "No automated changes permitted" });
        Event(db, job, "policy", reason);
        if (action != "observe")
        {
            var notice = new Notice { IncidentId = job.IncidentId, Kind = action == "escalate" ? "ai-escalation" : "ai-notification",
                Body = J.Obj(new { title = disputed ? "AI review requires human attention" : report.S("notification_title"), severity = priority,
                    message = disputed ? $"Second judge flagged incident {job.IncidentId}. Review the evidence and both reports in Agents; the manager draft was not sent." : report.S("notification_message"),
                    ai = (BedrockBudget.IsBedrock(job.Settings) ? "Bedrock" : "Codex") + (judgment != null ? " manager + judge" : " manager"), job_id = job.Id, delivery_status = "delivered", human_review_required = policy.B("human_review_required") }) };
            db.Notices.Add(notice); policy["notice_id"] = notice.Id;
        }
        job.Policy = policy; job.Status = "completed"; job.Stage = "delivery";
        Event(db, job, "delivery", action != "observe" ? "Delivered to the laboratory inbox. External channels not configured." : "Observation only: AI notification suppressed. Existing rule notifications remain.");
    }
}
