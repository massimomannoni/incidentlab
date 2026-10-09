using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace IncidentLab.Tests;

public sealed class AgentTests
{
    [Fact] public async Task DisabledAndCloudNeverStart()
    {
        await using var l = new LabFixture(); var id = l.Incident();
        using var analyze = await l.Send($"/api/incidents/{id}/analyze"); Assert.Equal(409, (int)analyze.StatusCode);
        using var claim = await l.Send("/api/worker/claim", J.Obj(new { worker_id = "test-worker" }), worker: true);
        Assert.Null((await claim.Content.ReadFromJsonAsync<JsonObject>())!["job"]);
        using var cloud = await l.Send("/api/settings/ai", J.Obj(new { enabled = true, execution_mode = "cloud_api" }), "PUT"); Assert.Equal(422, (int)cloud.StatusCode);
        Assert.Equal(0, (await l.Get("/api/agents")).N("calls"));
    }
    [Fact] public async Task WorkerAuthOneJobSingleClaimAndNoPublicLease()
    {
        await using var l = new LabFixture(); await l.Enable(); var id = l.Incident();
        using var denied = await l.Send("/api/worker/claim", J.Obj(new { worker_id = "test-worker" })); Assert.Equal(401, (int)denied.StatusCode);
        var job = await l.Claim(id);
        using var again = await l.Send($"/api/incidents/{id}/analyze"); Assert.Equal(job.S("id"), (await again.Content.ReadFromJsonAsync<JsonObject>())!.S("id"));
        using var second = await l.Send("/api/worker/claim", J.Obj(new { worker_id = "other" }), worker: true); Assert.Null((await second.Content.ReadFromJsonAsync<JsonObject>())!["job"]);
        Assert.False((await l.Get("/api/agents")).A("jobs")[0]!.AsObject().ContainsKey("lease"));
        Assert.False((await l.Get($"/api/incidents/{id}")).A("agent_runs")[0]!.AsObject().ContainsKey("lease"));
    }
    [Fact] public async Task TwoStagesOverrideObserveOnHighSeverityAndDeliverOnce()
    {
        await using var l = new LabFixture(); await l.Enable(); var job = await l.Claim(l.Incident());
        using var wrong = await l.Worker(job, "result", J.Obj(new { stage = "manager", report = LabFixture.Decision })); Assert.Equal(409, (int)wrong.StatusCode);
        await l.Complete(job); var state = await l.Get("/api/agents"); var done = state.A("jobs")[0]!;
        Assert.Equal(2, state.N("calls")); Assert.Equal(115, state.N("tokens")); Assert.Equal("completed", done.S("status"));
        Assert.Equal("observe", done.O("decision").S("action")); Assert.Equal("escalate", done.O("policy").S("effective_action"));
        Assert.True(done.O("policy").B("human_review_required")); Assert.Equal("delivered", done.O("policy").A("channels")[0].S("status"));
        Assert.All(done.O("policy").A("channels").Skip(1), c => Assert.Equal("not-configured", c.S("status")));
        using var duplicate = await l.Worker(job, "result", J.Obj(new { stage = "manager", report = LabFixture.Decision })); Assert.Equal(409, (int)duplicate.StatusCode);
        using var db = l.Store.Open(); Assert.Single(db.Notices.Where(n => n.Kind == "ai-escalation"));
    }
    [Fact] public async Task ObservePreservesRuleNotice()
    {
        await using var l = new LabFixture(); await l.Enable(); await l.Complete(await l.Claim(l.Incident("warning")));
        var job = (await l.Get("/api/agents")).A("jobs")[0]!; Assert.Equal("suppressed", job.O("policy").A("channels")[0].S("status"));
        using var db = l.Store.Open(); Assert.Single(db.Notices);
    }
    [Fact] public async Task DailyQuotaAndAutoGateAreTransactional()
    {
        await using var l = new LabFixture(); var old = l.Incident(now: J.Now() - 100); await l.Enable(auto: true, limit: 1);
        l.Agents.Reconcile(); Assert.Empty((await l.Get("/api/agents")).A("jobs"));
        var fresh = l.Incident(subject: "new"); l.Incident(subject: "second"); l.Agents.Reconcile();
        var jobs = (await l.Get("/api/agents")).A("jobs"); Assert.Single(jobs); Assert.Equal(fresh, jobs[0].S("incident_id"));
        using var over = await l.Send($"/api/incidents/{old}/analyze"); Assert.Equal(429, (int)over.StatusCode);
    }
    [Fact] public async Task CancelRejectsHeartbeatAndMarksUnknownUsage()
    {
        await using var l = new LabFixture(); var cfg = await l.Enable(); var job = await l.Claim(l.Incident());
        using var started = await l.Worker(job, "started"); Assert.Equal(200, (int)started.StatusCode);
        using var duplicate = await l.Worker(job, "started"); Assert.Equal(409, (int)duplicate.StatusCode);
        cfg["enabled"] = false; using var disabled = await l.Send("/api/settings/ai", cfg, "PUT"); Assert.Equal(200, (int)disabled.StatusCode);
        using var pulse = await l.Worker(job, "heartbeat"); Assert.Equal(409, (int)pulse.StatusCode);
        var totals = (await l.Get("/api/ai/usage")).O("totals"); Assert.Equal(1, totals.N("cancelled")); Assert.Null(totals["reported_tokens"]);
    }
    [Fact] public async Task ExpiredLeaseFailsWithoutRetry()
    {
        await using var l = new LabFixture(); await l.Enable(); var job = await l.Claim(l.Incident());
        l.Store.Write(db => { db.Jobs.Find(job.S("id"))!.Deadline = J.Now() - 1; }); l.Agents.Reconcile();
        Assert.Equal("failed", (await l.Get("/api/agents")).A("jobs")[0].S("status"));
        using var pulse = await l.Worker(job, "heartbeat"); Assert.Equal(409, (int)pulse.StatusCode);
    }
    [Fact] public async Task PacketRedactsSensitiveFieldsAndRejectsInventedEvidence()
    {
        await using var l = new LabFixture(); await l.Enable(); var e = LabFixture.Event("event1"); e["message"] = "secret"; e["path"] = "/x?token=secret"; e["source_ip"] = "private";
        await l.Ingest(e); var job = await l.Claim(l.Incident()); var sample = job.O("packet").A("events")[0]!.AsObject();
        foreach (var field in new[] { "message", "path", "source_ip", "command_signature", "client_id" }) Assert.False(sample.ContainsKey(field));
        using var start = await l.Worker(job, "started"); var report = LabFixture.Investigation;
        report["findings"] = J.Arr(new[] { new { observation = "invented", evidence_ids = new[] { "fabricated" } } });
        using var result = await l.Worker(job, "result", J.Obj(new { stage = "investigator", report })); Assert.Equal(422, (int)result.StatusCode);
    }
    [Fact] public async Task JudgeHoldsNotificationThenAccountsWithoutDoubleCounting()
    {
        await using var l = new LabFixture(); await l.Enable(judge: true); var job = await l.Claim(l.Incident()); await l.Complete(job);
        var pending = (await l.Get("/api/agents")).A("jobs")[0]!; Assert.Equal("judge", pending.S("stage")); Assert.Null(pending["policy"]);
        using (var db = l.Store.Open()) Assert.DoesNotContain(db.Notices, n => n.Kind.StartsWith("ai-"));
        using var start = await l.Worker(job, "started");
        using var result = await l.Worker(job, "result", J.Obj(new { stage = "judge", report = LabFixture.Judgment(), usage = new { input_tokens = 100, output_tokens = 20, cached_input_tokens = 80, reasoning_output_tokens = 5 }, provider_session_id = "test-thread" }));
        Assert.Equal(200, (int)result.StatusCode); var state = await l.Get("/api/agents"); Assert.Equal(3, state.N("calls")); Assert.Equal(235, state.N("tokens"));
        Assert.Equal("agree", state.A("jobs")[0].O("judge").S("verdict")); Assert.Equal(3, state.A("jobs")[0].A("sessions").Count);
    }
    [Theory][InlineData("disagree")][InlineData("needs_review")]
    public async Task JudgeDisagreementWithholdsManagerDraft(string verdict)
    {
        await using var l = new LabFixture(); await l.Enable(judge: true); var job = await l.Claim(l.Incident("warning")); await l.Complete(job);
        using var start = await l.Worker(job, "started"); using var result = await l.Worker(job, "result", J.Obj(new { stage = "judge", report = LabFixture.Judgment(verdict) })); Assert.Equal(200, (int)result.StatusCode);
        using var db = l.Store.Open(); var notice = db.Notices.Single(n => n.Kind == "ai-escalation"); Assert.True(notice.Body.B("human_review_required")); Assert.NotEqual(LabFixture.Decision.S("notification_message"), notice.Body.S("message"));
    }
    [Fact] public async Task FailedJudgeBlocksNotificationAndKeepsPartialUsage()
    {
        await using var l = new LabFixture(); await l.Enable(judge: true); var job = await l.Claim(l.Incident()); await l.Complete(job);
        using var start = await l.Worker(job, "started"); using var fail = await l.Worker(job, "failed", J.Obj(new { reason = "invalid_output", usage = new { input_tokens = 123 }, provider_session_id = "test-thread" }));
        Assert.Equal(200, (int)fail.StatusCode); var report = await l.Get("/api/ai/usage"); Assert.Equal(1, report.O("totals").N("usage_incomplete"));
        Assert.Equal(238, report.O("totals").N("reported_tokens")); Assert.Equal(1, report.O("totals").N("failed")); Assert.Null(report.A("sessions")[0]!["total_tokens"]);
        using var db = l.Store.Open(); Assert.DoesNotContain(db.Notices, n => n.Kind.StartsWith("ai-")); Assert.Null(db.Jobs.Single().Policy);
    }
    [Fact] public async Task UsageFilteringUnknownAndLegacyArePreserved()
    {
        await using var l = new LabFixture(); await l.Enable(); var id = l.Incident(); var job = await l.Claim(id); await l.Complete(job);
        var second = await l.Claim(l.Incident(subject: "another")); using var start = await l.Worker(second, "started"); using var fail = await l.Worker(second, "failed", J.Obj(new { reason = "timeout" }));
        var report = await l.Get("/api/ai/usage"); Assert.Equal(3, report.O("totals").N("sessions")); Assert.Equal(115, report.O("totals").N("reported_tokens"));
        var filtered = await l.Get($"/api/ai/usage?role=manager&incident_id={id}"); Assert.Equal(65, filtered.O("totals").N("reported_tokens"));
        Assert.Null((await l.Get($"/api/ai/usage?incident_id={second.S("incident_id")}")).O("totals")["reported_tokens"]);
        using var invalid = await l.Client.GetAsync("/api/ai/usage?role=invalid"); Assert.Equal(422, (int)invalid.StatusCode);
        var jobId = job.S("id"); l.Store.Write(db => db.Sessions.Where(s => s.JobId == jobId).ExecuteDelete());
        var legacy = await l.Get($"/api/ai/usage?incident_id={id}"); Assert.Equal(115, legacy.O("totals").N("reported_tokens")); Assert.All(legacy.A("sessions"), s => { Assert.True(s.B("legacy")); Assert.Null(s!["duration_seconds"]); });
    }
    static Task<HttpResponseMessage> Catalog(LabFixture l) => l.Send("/api/worker/catalog", J.Obj(new { worker_id = "test-worker", models = new[] { new { id = "test-model", name = "Test model", family = "Test", efforts = new[] { "low", "high" }, default_effort = "low", is_default = true } } }), worker: true);
    [Theory][InlineData("model_id", "missing")][InlineData("model_family", "Wrong")][InlineData("reasoning_effort", "ultra")]
    public async Task InvalidModelProfilesAreRejectedButEmergencyStopWorks(string field, string value)
    {
        await using var l = new LabFixture(); using var catalog = await Catalog(l); Assert.Equal(200, (int)catalog.StatusCode); var cfg = await l.Enable();
        cfg.O("agent_profiles").O("investigator")[field] = value; using var invalid = await l.Send("/api/settings/ai", cfg, "PUT"); Assert.Equal(422, (int)invalid.StatusCode);
        cfg["enabled"] = false; using var stop = await l.Send("/api/settings/ai", cfg, "PUT"); Assert.Equal(200, (int)stop.StatusCode);
    }
    [Fact] public async Task QueuedProfileSnapshotDoesNotChange()
    {
        await using var l = new LabFixture(); using var catalog = await Catalog(l); var cfg = await l.Enable(); var id = l.Incident();
        using var queue = await l.Send($"/api/incidents/{id}/analyze"); cfg.O("agent_profiles").O("investigator")["reasoning_effort"] = "high"; cfg["judge_enabled"] = true;
        using var save = await l.Send("/api/settings/ai", cfg, "PUT"); Assert.Equal(200, (int)save.StatusCode); var job = await l.Claim(id);
        Assert.False(job.O("settings").B("judge_enabled")); Assert.Equal("low", job.O("settings").O("resolved_profiles").O("investigator").S("reasoning_effort"));
        Assert.Equal("test-model", job.O("settings").O("resolved_profiles").O("investigator").S("model_id"));
    }
    [Fact] public void OldSettingsMigrateWithoutOverwritingOldModel()
    {
        var settings = Contracts.Settings(J.Obj(new { enabled = true, execution_mode = "codex_local", provider = "bedrock", model_id = "previous-model" }));
        Assert.Equal("codex", settings.S("provider")); Assert.Equal("previous-model", settings.O("agent_profiles").O("manager").S("model_id")); Assert.False(settings.B("judge_enabled"));
    }
    [Fact] public async Task BooleanUsageCannotBecomeATokenCount()
    {
        await using var l = new LabFixture(); await l.Enable(); var job = await l.Claim(l.Incident()); using var start = await l.Worker(job, "started");
        using var bad = await l.Worker(job, "result", J.Obj(new { stage = "investigator", report = LabFixture.Investigation, usage = new { input_tokens = true } })); Assert.Equal(422, (int)bad.StatusCode);
    }
    [Fact] public void InvalidCountersAndImpossibleSubsetsAreDiscarded()
    {
        var usage = AiUsage.Safe(J.Obj(new { input_tokens = 10, cached_input_tokens = 11, output_tokens = -1, reasoning_output_tokens = true, bill_usd = 100 }));
        Assert.Single(usage); Assert.Equal(10, usage.N("input_tokens"));
    }
}
