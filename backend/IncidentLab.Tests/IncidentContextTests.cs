using System.Text.Json.Nodes;

namespace IncidentLab.Tests;

public sealed class IncidentContextTests
{
    [Fact] public async Task RecordedWindowStaysFrozenAfterIncidentChangesAndReadsDoNotInvokeAi()
    {
        await using var lab = new LabFixture(); var when = J.Now() - 80;
        await lab.Ingest(LabFixture.Event("event1", ts:when-2, status:500));
        var id = lab.Incident(now:when); await lab.Enable();
        using var response = await lab.Send($"/api/incidents/{id}/analyze"); Assert.Equal(200, (int)response.StatusCode);
        var first = await lab.Get($"/api/incidents/{id}"); var job = first.A("agent_runs").Single()!;
        Assert.Equal(when - 20, first.O("context").N("window_start")); Assert.Equal(when, first.O("context").N("window_end"));
        Assert.Equal("recorded", first.O("context").S("window_precision"));
        var hash = job.S("packet_sha256");
        lab.Incident(now:when+40);
        var updated = await lab.Get($"/api/incidents/{id}");
        Assert.Equal(when, updated.O("context").N("window_end"));
        Assert.Equal(hash, updated.A("agent_runs").Single()!.S("packet_sha256"));
        using var db = lab.Store.Open(); Assert.Empty(db.Sessions); Assert.Single(db.Jobs);
        Assert.Equal(when, db.Jobs.Single().Packet.N("window_end"));
    }

    [Fact] public async Task ChartUsesMeasuredServiceTrafficAndKeepsUnknownBinsNull()
    {
        await using var lab = new LabFixture(); var when = J.Now()-40;
        var id = lab.Incident(now:when);
        var other = LabFixture.Event("other", ts:when-2, status:500); other["service"] = "unrelated-service";
        var health = LabFixture.Event("health", ts:when-2); health["path"] = "/health";
        await lab.Ingest(LabFixture.Event("ok", ts:when-2), LabFixture.Event("missing", ts:when-3, status:404),
            LabFixture.Event("failure", ts:when-4, status:500), health, other, LabFixture.Event("idle", "metric", when-20));
        var context = (await lab.Get($"/api/incidents/{id}")).O("context"); var bins = context.A("histogram");
        Assert.Equal("persisted-ingest-telemetry", context.S("source")); Assert.Equal("available", context.S("data_state"));
        Assert.Equal(3, bins.Sum(b=>b.N("requests"))); Assert.Equal(2, bins.Sum(b=>b.N("errors"))); Assert.Equal(1, bins.Sum(b=>b.N("http_404")));
        Assert.Contains(bins, b => !b.B("observed") && b!["requests"] == null);
        Assert.Contains(bins, b => b.B("observed") && b.N("requests") == 0);
        Assert.Empty(context.A("runs"));
    }

    [Fact] public async Task ResourceChartDoesNotMixInstances()
    {
        await using var lab = new LabFixture(); var when=J.Now()-10;
        var affected=LabFixture.Event("affected", "metric", when-1); affected["instance"]="affected"; affected["cpu_percent"]=70;
        var other=LabFixture.Event("other", "metric", when-1); other["instance"]="other"; other["cpu_percent"]=99;
        await lab.Ingest(affected,other);
        Incidents.Apply(lab.Store,[new Finding("process-cpu","resources","warning","target-api","affected","High CPU",70,60,["affected"],"Measured CPU")],when,true,8);
        using var db=lab.Store.Open(); var incident=db.Incidents.Single();
        var context=IncidentContext.Read(db,incident,null,J.Now());
        Assert.Equal(70,context.A("histogram").Max(b=>b.N("cpu_percent")));
        Assert.Equal("affected",context.A("instances").Single()!.GetValue<string>());
    }

    [Fact] public async Task LegacyPacketIsNotRewrittenAndEstimatedWindowIsLabeled()
    {
        await using var lab = new LabFixture(); var id = lab.Incident(); await lab.Enable();
        using var queued = await lab.Send($"/api/incidents/{id}/analyze");
        lab.Store.Write(db => { var job=db.Jobs.Single(); job.Packet.Remove("window_start"); job.Packet.Remove("window_end"); });
        string before; using(var db=lab.Store.Open()) before=J.Canonical(db.Jobs.Single().Packet);
        var context=(await lab.Get($"/api/incidents/{id}")).O("context");
        Assert.Equal("estimated-legacy", context.S("window_precision"));
        using(var db=lab.Store.Open()) Assert.Equal(before,J.Canonical(db.Jobs.Single().Packet));
    }

    [Fact] public async Task RunLinksRequireTaggedEvidenceNotTemporalOverlapAndSurviveLaterSignals()
    {
        await using var lab = new LabFixture(); var when=J.Now();
        var linked=new Experiment { Started=when-5, Scenario="errors" }; var overlap=new Experiment { Started=when-5, Scenario="probing" };
        lab.Store.Write(db => db.Runs.AddRange(linked,overlap));
        var e=LabFixture.Event("tagged", ts:when-1, status:500); e["run_id"]=linked.Id; await lab.Ingest(e);
        var finding=new Finding("server-errors","application","high","target-api","api","Server errors",10,5,["tagged"],"Measured errors");
        Incidents.Apply(lab.Store,[finding],when,true,8);
        string id; using(var db=lab.Store.Open()) id=db.Incidents.Single().Id;
        Incidents.Apply(lab.Store,[finding with { EvidenceIds=[] }],when+1,true,8);
        var context=(await lab.Get($"/api/incidents/{id}")).O("context");
        Assert.Equal(linked.Id,context.A("runs").Single()!.S("id"));
        var runs=JsonNode.Parse(await lab.Client.GetStringAsync("/api/runs"))!.AsArray();
        Assert.Single(runs.Single(r=>r.S("id")==linked.Id)!.A("incidents"));
        Assert.Empty(runs.Single(r=>r.S("id")==overlap.Id)!.A("incidents"));
    }

    [Fact] public async Task EmptyAndAvailabilityIncidentsNeverInventChartsOrFixedWindows()
    {
        await using var lab = new LabFixture(); var id=lab.Incident();
        var context=(await lab.Get($"/api/incidents/{id}")).O("context");
        Assert.Equal("unavailable",context.S("data_state")); Assert.All(context.A("histogram"),b=>Assert.Null(b!["requests"]));
        Incidents.Apply(lab.Store,[new Finding("source-stale","monitoring","high","target-api","telemetry","Telemetry gap",30,10,[],"Missing samples")],J.Now(),false,8);
        using var db=lab.Store.Open(); var availability=db.Incidents.Single(i=>i.Rule=="source-stale");
        var gap=IncidentContext.Read(db,availability,null,J.Now()); Assert.Equal("unavailable",gap.S("window_precision")); Assert.Null(gap["window_start"]);
    }

    [Fact] public async Task OversizedQueryFailsClosedWithoutPartialCounts()
    {
        await using var lab=new LabFixture(); var when=J.Now(); var id=lab.Incident(now:when);
        lab.Store.Write(db=>db.Telemetry.AddRange(Enumerable.Range(0,10001).Select(i=>new TelemetryRecord { Id="capacity-"+i, Ts=when-1, Received=when, Payload=LabFixture.Event("capacity-"+i,ts:when-1) })));
        var context=(await lab.Get($"/api/incidents/{id}")).O("context");
        Assert.Equal("capacity-exceeded",context.S("data_state")); Assert.Empty(context.A("histogram"));
    }

    [Theory][InlineData("not-a-run")][InlineData("../secrets")]
    public void RunCorrelationFieldRejectsMalformedIds(string value)
    {
        var e=LabFixture.Event("id"); e["run_id"]=value;
        Assert.Throws<LabException>(()=>Contracts.Validate(e,Contracts.EventSchema));
    }
}
