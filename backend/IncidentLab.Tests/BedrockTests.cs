using System.Text.Json.Nodes;
using Amazon.BedrockRuntime;
using IncidentLab.Api;

namespace IncidentLab.Tests;

public sealed class BedrockTests
{
    static JsonObject Settings(bool judge = false, decimal daily = .50m, decimal monthly = 5) => Contracts.Settings(J.Obj(new
    {
        enabled = true, execution_mode = "cloud_api", provider = "bedrock", region = "eu-south-1", model_id = BedrockBudget.ModelId,
        max_tool_calls = 0, judge_enabled = judge, daily_budget_usd = daily, monthly_budget_usd = monthly
    }));
    sealed class FakeInference(params BedrockAnswer[] answers) : IBedrockInference
    {
        public int Calls { get; private set; }
        public List<string> Prompts { get; } = [];
        public Action? BeforeCall { get; init; }
        public Task<BedrockAnswer> Converse(string prompt, int maxOutputTokens, CancellationToken ct)
        {
            BeforeCall?.Invoke(); Prompts.Add(prompt); Calls++;
            if (Calls > answers.Length) throw new TimeoutException("test provider timeout");
            return Task.FromResult(answers[Calls - 1]);
        }
    }
    static BedrockAnswer Answer(JsonObject report, string reason = "end_turn") => new(report.ToJsonString(), J.Obj(new { input_tokens = 600, output_tokens = 100 }), "aws-test-request", reason);
    static BedrockExecutor Executor(LabFixture lab, IBedrockInference inference) => new(lab.Agents, inference, lab.Config);
    static string Queue(LabFixture lab, JsonObject settings)
    {
        lab.Agents.SaveSettings(settings); var id = lab.Incident(); return lab.Agents.Analyze(id).S("id");
    }
    [Fact] public async Task ApprovedMonthlyCeilingKeepsSafeDefaultsAndDailyLimit()
    {
        await using var lab = new LabFixture(true, 100);
        Assert.Equal(5, new LabConfig().BedrockMonthlyCapUsd);
        lab.Config.Validate();
        Assert.Throws<ArgumentException>(() => (lab.Config with { BedrockMonthlyCapUsd = 100.01m }).Validate());
        Assert.Throws<ArgumentException>(() => (lab.Config with { BedrockMonthlyCapUsd = -1 }).Validate());
        Assert.Throws<ArgumentException>(() => (lab.Config with { BedrockDailyCapUsd = .51m }).Validate());
        lab.Agents.SaveSettings(Settings(monthly: 100));
        Assert.Throws<LabException>(() => lab.Agents.SaveSettings(Settings(daily: .51m, monthly: 100)));
        Assert.Throws<LabException>(() => lab.Agents.SaveSettings(Settings(monthly: 100.01m)));
        using var db = lab.Store.Open(); var state = BedrockBudget.Snapshot(db, lab.Config, J.Now());
        Assert.Equal(100m, BedrockBudget.Amount(state, "monthly_limit"));
        Assert.Equal(.50m, BedrockBudget.Amount(state, "daily_limit"));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HundredDollarMonthlyBudgetStillBlocksAtEitherBoundary(bool monthlyBoundary)
    {
        await using var lab = new LabFixture(true, 100); var settings = Settings(monthly: 100); lab.Agents.SaveSettings(settings);
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        lab.Store.Write(db => db.Sessions.Add(new AgentSession { JobId = "previous", Role = "investigator", Started = monthlyBoundary ? now - 86400 : now,
            Config = J.Obj(new { provider = "bedrock", budget_status = "settled", budget_cost_usd = monthlyBoundary ? 99.98m : .48m }) }));
        lab.Store.Initialize(); // Reopening must not reset settled usage.
        var ex = Assert.Throws<LabException>(() => lab.Store.Write(db => BedrockBudget.Reserve(db,
            new AgentSession { JobId = "next", Role = "investigator", Config = BedrockBudget.Profile }, settings, lab.Config, now)));
        Assert.Equal(429, ex.Status);
        using var db = lab.Store.Open(); Assert.Single(db.Sessions);
        var state = BedrockBudget.Snapshot(db, lab.Config, now);
        Assert.Equal(.02m, BedrockBudget.Amount(state, monthlyBoundary ? "monthly_remaining" : "daily_remaining"));
    }
    [Fact] public async Task RaisingMonthlySettingPreservesUnresolvedReservations()
    {
        await using var lab = new LabFixture(true, 100); Queue(lab, Settings());
        var fake = new FakeInference(); await Executor(lab, fake).RunOne(default);
        using var beforeDb = lab.Store.Open(); var before = BedrockBudget.Snapshot(beforeDb, lab.Config, J.Now());
        lab.Agents.SaveSettings(Settings(monthly: 100)); lab.Store.Initialize();
        using var afterDb = lab.Store.Open(); var after = BedrockBudget.Snapshot(afterDb, lab.Config, J.Now());
        Assert.Equal(before.N("monthly_committed"), after.N("monthly_committed"));
        Assert.Equal(before.N("daily_remaining"), after.N("daily_remaining"));
        Assert.Equal(1, after.N("unresolved_reservations")); Assert.Equal(1, fake.Calls);
    }
    [Fact] public async Task DeploymentGateAndModelRegionToolsCapsFailClosed()
    {
        await using var disabled = new LabFixture(); Assert.Throws<LabException>(() => disabled.Agents.SaveSettings(Settings()));
        await using var lab = new LabFixture(true);
        foreach (var (key, value) in new (string, object)[] { ("region", "us-east-1"), ("model_id", "global.amazon.nova-lite-v1:0"), ("max_tool_calls", 1), ("daily_job_limit", 11), ("max_output_tokens", 2001), ("monthly_budget_usd", 6), ("daily_budget_usd", .51) })
        {
            var s = Settings(); s[key] = System.Text.Json.JsonSerializer.SerializeToNode(value); Assert.Throws<LabException>(() => lab.Agents.SaveSettings(s));
        }
        var invalid = Settings(); invalid.O("agent_profiles").O("investigator")["reasoning_effort"] = "high";
        Assert.Throws<LabException>(() => lab.Agents.SaveSettings(invalid));
    }
    [Fact] public async Task TwoRealLifecycleStagesReserveBeforeDispatchAndRecordTokensAndCost()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings());
        var fake = new FakeInference(Answer(LabFixture.Investigation), Answer(LabFixture.Decision)) { BeforeCall = () =>
        {
            using var db = lab.Store.Open(); Assert.Contains(db.Sessions.ToList(), s => s.Status == "running" && s.Config.N("reserved_usd") > 0);
        } };
        Assert.True(await Executor(lab, fake).RunOne(default)); Assert.Equal(2, fake.Calls);
        using var db = lab.Store.Open(); var job = db.Jobs.Single(); Assert.Equal("completed", job.Status);
        Assert.Single(db.Notices.Where(n => n.Kind == "ai-escalation"));
        Assert.All(db.Sessions.ToList(), s => { Assert.Equal("settled", s.Config.S("budget_status")); Assert.Equal("bedrock", s.Config.S("provider")); });
        var usage = AiUsage.Report(db, null, null, null); Assert.Equal(1400, usage.O("totals").N("reported_tokens"));
        Assert.Equal(.0002m, BedrockBudget.Amount(usage.O("totals"), "budget_cost_usd"));
        Assert.DoesNotContain("\"scenario\":", fake.Prompts[0]); Assert.Contains("notification", fake.Prompts[1]);
        Assert.False(await Executor(lab, fake).RunOne(default)); Assert.Equal(2, fake.Calls);
    }
    [Fact] public async Task CodexWorkerCannotClaimOrReportBedrockJob()
    {
        await using var lab = new LabFixture(true); var id = Queue(lab, Settings());
        using var claim = await lab.Send("/api/worker/claim", J.Obj(new { worker_id = "test-worker" }), worker: true);
        Assert.Null(System.Text.Json.Nodes.JsonNode.Parse(await claim.Content.ReadAsStringAsync())!["job"]);
        var body = J.Obj(new { worker_id = "internal" }); var job = lab.Agents.Claim(body, "cloud_api").O("job"); body["lease"] = job.S("lease");
        Assert.Throws<LabException>(() => lab.Agents.Started(id, body));
    }
    [Fact] public async Task ZeroBudgetStartsNoInference()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings(monthly: 0)); var fake = new FakeInference();
        await Executor(lab, fake).RunOne(default); Assert.Equal(0, fake.Calls);
        using var db = lab.Store.Open(); Assert.Empty(db.Sessions); Assert.Equal("budget_exhausted", db.Jobs.Single().Error);
    }
    [Fact] public async Task FailedUnknownUsageKeepsReservationAfterRestartAndMonthRollover()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings()); var fake = new FakeInference();
        await Executor(lab, fake).RunOne(default); Assert.Equal(1, fake.Calls); lab.Store.Initialize();
        using var db = lab.Store.Open(); var session = db.Sessions.Single(); Assert.Empty(session.Usage); Assert.Equal("reserved", session.Config.S("budget_status"));
        var next = BedrockBudget.Snapshot(db, lab.Config, J.Now() + 40 * 86400);
        Assert.Equal(1, next.N("unresolved_reservations")); Assert.True(next.N("monthly_committed") > 0);
        Assert.Null(AiUsage.Report(db, null, null, null).O("totals")["budget_cost_usd"]);
    }
    [Theory]
    [InlineData("AccessDeniedException", "bedrock_access_denied")]
    [InlineData("ValidationException", "bedrock_validation_error")]
    [InlineData("ThrottlingException", "bedrock_throttled")]
    [InlineData("private-unrecognized-error", "bedrock_request_failed")]
    public async Task ProviderErrorsExposeOnlyAllowlistedCategoryAndCorrelationId(string code, string expected)
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings());
        var requestId = Guid.NewGuid().ToString();
        var fake = new FakeInference { BeforeCall = () => throw new AmazonBedrockRuntimeException("private provider response") { ErrorCode = code, RequestId = requestId } };
        await Executor(lab, fake).RunOne(default);
        using var db = lab.Store.Open();
        Assert.Equal(expected, db.Jobs.Single().Error);
        Assert.Equal(requestId, db.Sessions.Single().ProviderSessionId);
        Assert.Equal("reserved", db.Sessions.Single().Config.S("budget_status"));
        Assert.Empty(db.Sessions.Single().Usage);
        Assert.DoesNotContain("private", AiUsage.Report(db, null, null, null).ToJsonString());
        Assert.False(await Executor(lab, fake).RunOne(default));
    }
    [Fact] public async Task InvalidOutputIsChargedButNeverDeliveredOrRetried()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings());
        var report = LabFixture.Investigation; report["unexpected"] = true;
        var fake = new FakeInference(Answer(report)); await Executor(lab, fake).RunOne(default);
        using var db = lab.Store.Open(); Assert.Equal("failed", db.Jobs.Single().Status); Assert.Equal("settled", db.Sessions.Single().Config.S("budget_status"));
        Assert.DoesNotContain(db.Notices, n => n.Kind.StartsWith("ai-")); Assert.Equal(1, fake.Calls);
    }
    [Fact] public async Task SingleJsonFenceStillUsesStrictSchemaAndEvidenceValidation()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings());
        var report = Answer(LabFixture.Investigation);
        var fake = new FakeInference(report with { Text = "```json\n" + report.Text + "\n```" }, Answer(LabFixture.Decision));
        await Executor(lab, fake).RunOne(default);
        using var db = lab.Store.Open(); Assert.Equal("completed", db.Jobs.Single().Status); Assert.Equal(2, fake.Calls);
    }
    [Theory]
    [InlineData("Here is the result: {\"summary\":\"unsafe extraction\"}")]
    [InlineData("```json\n{}\n```\n```json\n{}\n```")]
    [InlineData("{} {}")]
    public void ParserDoesNotExtractJsonFromProseOrMultipleReports(string text) => Assert.ThrowsAny<System.Text.Json.JsonException>(() => BedrockExecutor.ParseReport(text));
    [Fact] public async Task FencedInvalidSchemaIsChargedAndRejected()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings());
        var report = LabFixture.Investigation; report["unexpected"] = true;
        var fake = new FakeInference(Answer(report) with { Text = "```json\r\n" + report.ToJsonString() + "\r\n```" });
        await Executor(lab, fake).RunOne(default);
        using var db = lab.Store.Open(); Assert.Equal("bedrock_invalid_report", db.Jobs.Single().Error);
        Assert.Equal("settled", db.Sessions.Single().Config.S("budget_status")); Assert.DoesNotContain(db.Notices, n => n.Kind.StartsWith("ai-"));
    }
    [Fact] public async Task JudgeAddsOneCallAndUsesExistingReviewPolicy()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings(judge: true));
        var fake = new FakeInference(Answer(LabFixture.Investigation), Answer(LabFixture.Decision), Answer(LabFixture.Judgment("disagree")));
        await Executor(lab, fake).RunOne(default); Assert.Equal(3, fake.Calls);
        using var db = lab.Store.Open(); Assert.Equal("disagree", db.Jobs.Single().Policy!.S("judge_verdict"));
        Assert.Equal("AI review requires human attention", db.Notices.Single(n => n.Kind == "ai-escalation").Body.S("title"));
    }
    [Fact] public async Task LiveLowerBudgetStopsNextStageEvenWithFrozenSettings()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings());
        var fake = new FakeInference(Answer(LabFixture.Investigation)) { BeforeCall = () => lab.Agents.SaveSettings(Settings(monthly: 0)) };
        await Executor(lab, fake).RunOne(default); Assert.Equal(1, fake.Calls);
        using var db = lab.Store.Open(); Assert.Equal("budget_exhausted", db.Jobs.Single().Error); Assert.Single(db.Sessions);
    }
    [Fact] public async Task ConcurrentBudgetReservationsAreAtomicAndPersistAcrossReopen()
    {
        await using var lab = new LabFixture(true); var settings = Settings(daily: .04m); lab.Agents.SaveSettings(settings);
        var successes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            try { lab.Store.Write(db => { var s = new AgentSession { JobId = "job" + i, Role = "investigator", Config = BedrockBudget.Profile }; BedrockBudget.Reserve(db, s, settings, lab.Config, J.Now()); db.Sessions.Add(s); }); return 1; }
            catch (LabException ex) when (ex.Status == 429) { return 0; }
        })));
        Assert.Equal(1, successes.Sum());
        var reopened = new Store(lab.Config.DatabaseUrl); using var db = reopened.Open(); Assert.Single(db.Sessions);
    }
    [Fact] public async Task SwitchingProviderCancelsOldJobsAndNoModelRunsWhenDisabled()
    {
        await using var lab = new LabFixture(true); Queue(lab, Settings()); await lab.Enable();
        var fake = new FakeInference(); Assert.False(await Executor(lab, fake).RunOne(default)); Assert.Equal(0, fake.Calls);
        using var db = lab.Store.Open(); Assert.Equal("cancelled", db.Jobs.Single().Status);
    }
}
