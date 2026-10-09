using System.Net;

namespace IncidentLab.Tests;

public sealed class SimulationQuotaTests
{
    [Fact] public void MasterCredentialIsDomainSeparatedAndMatchesInstaller()
    {
        Assert.Equal("dca78f03502a729fd146ba57e6880a141e5f27cd319c2f141b2a2747afde3695", SimulationQuota.MasterKey(new('a',64)));
        Assert.False(SimulationQuota.IsMaster(new('a',64),new('a',64)));
        Assert.False(SimulationQuota.IsMaster("master",new('a',64)));
        Assert.False(SimulationQuota.IsMaster(SimulationQuota.MasterKey(""),""));
    }
    [Fact] public async Task OnlyAuthenticatedMasterCredentialGetsUnlimitedAdmission()
    {
        await using var lab=new LabFixture();
        lab.Store.Write(db=> { for(var i=0;i<10;i++) db.Runs.Add(new() {Status="stopped"}); });
        lab.Client.DefaultRequestHeaders.Add(SimulationQuota.MasterHeader,"master");
        Assert.False((await lab.Get("/api/limits")).O("simulations").B("unlimited"));
        using(var blocked=await lab.Send("/api/runs",J.Obj(new {scenario="normal"}))) Assert.Equal(HttpStatusCode.TooManyRequests,blocked.StatusCode);
        lab.Client.DefaultRequestHeaders.Remove(SimulationQuota.MasterHeader);
        lab.Client.DefaultRequestHeaders.Add(SimulationQuota.MasterHeader,SimulationQuota.MasterKey(lab.Config.ControlKey));
        var status=(await lab.Get("/api/limits")).O("simulations");
        Assert.True(status.B("unlimited")); Assert.Null(status["daily_limit"]); Assert.Null(status["remaining"]);
        Assert.Equal("personal-master",status.S("scope"));
        // No target is configured: passing admission reaches the target rather than returning quota 429.
        using var admitted=await lab.Send("/api/runs",J.Obj(new {scenario="normal"})); Assert.Equal(HttpStatusCode.ServiceUnavailable,admitted.StatusCode);
    }
    [Fact] public async Task MasterStartsBeyondQuotaButKeepsLoadBoundsConcurrencyAndHistory()
    {
        await using var lab=new LabFixture();
        lab.Store.Write(db=> { for(var i=0;i<10;i++) db.Runs.Add(new() {Status="completed"}); });
        using var client=new HttpClient(new Target()); await using var runner=new ScenarioRunner(lab.Config,lab.Store,client);
        var id=await runner.Start(J.Obj(new {scenario="normal"}),master:true);
        var busy=await Assert.ThrowsAsync<LabException>(()=>runner.Start(J.Obj(new {scenario="normal"}),master:true)); Assert.Equal(409,busy.Status);
        await runner.Stop();
        await runner.Start(J.Obj(new {scenario="normal"}),master:true); await runner.Stop();
        var invalid=await Assert.ThrowsAsync<LabException>(()=>runner.Start(J.Obj(new {scenario="normal",rps=100000}),master:true)); Assert.Equal(422,invalid.Status);
        var forged=await Assert.ThrowsAsync<LabException>(()=>runner.Start(J.Obj(new {scenario="normal",master_quota_exempt=true}))); Assert.Equal(422,forged.Status);
        var reopened=new Store(lab.Config.DatabaseUrl); reopened.Initialize(); using var db=reopened.Open();
        Assert.True(db.Runs.Find(id)!.Config.B("master_quota_exempt")); Assert.Equal(12,db.Runs.Count());
        Assert.Equal(10,SimulationQuota.Status(db,J.Now()).N("used")); Assert.Equal(2,SimulationQuota.Status(db,J.Now(),true).N("used"));
        Assert.Equal(0,SimulationQuota.Status(db,J.Now()).N("remaining"));
    }
    [Fact] public async Task LimitSurvivesRestartAndCountsEveryStartedStatus()
    {
        await using var lab = new LabFixture(); var now = J.Now();
        lab.Store.Write(db => {
            foreach (var status in new[] { "completed", "stopped", "failed", "interrupted", "running", "settling" })
                db.Runs.Add(new() { Started = now, Status = status });
            db.Runs.Add(new() { Started = now - 86400 });
        });
        var reopened = new Store(lab.Config.DatabaseUrl); reopened.Initialize();
        using var db = reopened.Open(); var status = SimulationQuota.Status(db, now);
        Assert.Equal(6, status.N("used")); Assert.Equal(4, status.N("remaining"));
        Assert.Equal(10, SimulationQuota.Status(db, now + 86400).N("remaining"));
        Assert.Equal("UTC", status.S("timezone"));
        Assert.Equal(4, (await lab.Get("/api/limits")).O("simulations").N("remaining"));
    }
    [Fact] public async Task ExhaustedQuotaIsRejectedBeforeAnyTargetRequest()
    {
        await using var lab = new LabFixture();
        lab.Store.Write(db => { for (var i = 0; i < SimulationQuota.DailyLimit; i++) db.Runs.Add(new() { Status = "stopped" }); });
        // The default fixture has no target server; a quota check AFTER HTTP would return 503.
        using var response = await lab.Send("/api/runs", J.Obj(new { scenario = "normal" }));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(0, (await lab.Get("/api/limits")).O("simulations").N("remaining"));
    }
    sealed class Target : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
    [Fact] public async Task ConcurrentStartsCannotOverbookLastSlotAndStoppingDoesNotRefund()
    {
        await using var lab = new LabFixture();
        lab.Store.Write(db => { for (var i = 0; i < 9; i++) db.Runs.Add(new() { Status = "completed" }); });
        using var client = new HttpClient(new Target()); await using var runner = new ScenarioRunner(lab.Config, lab.Store, client);
        async Task<bool> Start() { try { await runner.Start(J.Obj(new { scenario = "normal" })); return true; }
            catch (LabException e) when (e.Status is 409 or 429) { return false; } }
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Start()));
        Assert.Single(results, x => x); await runner.Stop();
        var exception = await Assert.ThrowsAsync<LabException>(() => runner.Start(J.Obj(new { scenario = "normal" })));
        Assert.Equal(429, exception.Status);
        using var db = lab.Store.Open(); Assert.Equal(10, db.Runs.Count()); Assert.Equal(0, SimulationQuota.Status(db, J.Now()).N("remaining"));
    }
}
