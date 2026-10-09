using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using IncidentLab.Api;

namespace IncidentLab.Tests;

public sealed class DiagnosticTests
{
    static JsonObject Metric(string id, double ts, double cpu)
    {
        var e=LabFixture.Event(id,"metric",ts); e["scope"]="process"; e["cpu_percent"]=cpu; e["memory_mb"]=70; return e;
    }
    static async Task<string> Context(LabFixture l)
    {
        var now=J.Now()-1; var events=new List<JsonObject>();
        for(var i=0;i<40;i++)
        {
            var ts=now-80+i*2;
            events.Add(Metric("metric"+i,ts,i<30?5:80));
            var req=LabFixture.Event("request"+i,ts:ts); req["path"]=i<30?"/items":"/compute"; req["duration_ms"]=i<30?5:180;
            events.Add(req);
        }
        var unrelated=Metric("other-instance",now-1,999); unrelated["instance"]="other"; events.Add(unrelated);
        await l.Ingest(events.ToArray());
        Incidents.Apply(l.Store,[new Finding("process-cpu","resources","warning","target-api","target","CPU pressure",80,60,["metric37","metric38","metric39"],"Threshold crossed")],now,true,8);
        using var db=l.Store.Open(); return db.Incidents.Single().Id;
    }
    static JsonObject Investigation(string followup="none", bool final=false)
    {
        var report=LabFixture.Investigation; report["follow_up"]=followup;
        report["diagnosis"]=J.Obj(new {status=final?"likely_cause":"insufficient_evidence", explanation=final?"More expensive /compute requests are a plausible contributor; profiling is required for causal confirmation.":"Load and request cost need to be distinguished.", evidence_refs=final?new[]{"ctx-baseline","ctx-during","check-endpoints"}:new[]{"ctx-baseline","ctx-during"}});
        return report;
    }
    sealed class Provider(params JsonObject[] reports):IBedrockInference
    {
        public List<string> Prompts {get;}=[];
        public Action<int>? Before {get;init;}
        public Task<BedrockAnswer> Converse(string prompt,int maxOutputTokens,CancellationToken ct)
        {
            Before?.Invoke(Prompts.Count); Prompts.Add(prompt);
            return Task.FromResult(new BedrockAnswer(reports[Prompts.Count-1].ToJsonString(),J.Obj(new{input_tokens=600,output_tokens=200}),"test-request","end_turn"));
        }
    }
    static JsonObject BedrockSettings()=>Contracts.Settings(J.Obj(new{enabled=true,execution_mode="cloud_api",provider="bedrock",region="eu-south-1",model_id=BedrockBudget.ModelId,max_tool_calls=0}));
    [Fact] public async Task CapturesEqualLoadDifferentCostAndNoOtherInstanceOrGroundTruth()
    {
        await using var l=new LabFixture(); var id=await Context(l); await l.Enable(); var job=await l.Claim(id); var d=job.O("packet").O("diagnostics");
        Assert.Equal(.5,d.O("baseline").N("requests_per_second")); Assert.Equal(.5,d.O("during").N("requests_per_second"));
        Assert.Equal(5,d.O("baseline").N("process_cpu_mean")); Assert.Equal(80,d.O("during").N("process_cpu_mean"));
        Assert.Equal(180,d.O("during").N("p95_ms"));
        Assert.Equal("/compute",d.O("checks").O("endpoint_breakdown").A("during")[0].S("route"));
        var text=job.O("packet").ToJsonString(); Assert.DoesNotContain("other-instance",text); Assert.DoesNotContain("\"scenario\"",text); Assert.DoesNotContain("\"run_id\"",text);
        var prompt=AgentPrompts.Prompt(job,"investigator"); Assert.Contains("WHY",prompt); Assert.DoesNotContain("\"check_result\"",prompt); Assert.DoesNotContain("\"checks\"",prompt);
        Assert.True(prompt.IndexOf("Investigate WHY",StringComparison.Ordinal)<prompt.IndexOf("BEGIN UNTRUSTED EVIDENCE JSON",StringComparison.Ordinal));
        Assert.DoesNotContain("\"task\":",prompt); Assert.Contains("actual numeric values",prompt);Assert.Contains("conditional mitigation or fix",prompt);
        Assert.True(Encoding.UTF8.GetByteCount(prompt+Contracts.ReportSchema("investigator",true).ToJsonString())<16000);
    }
    [Fact] public async Task SparseAndMissingDataDoNotBecomeZeroRates()
    {
        await using var l=new LabFixture(); var when=J.Now()-1; await l.Ingest(Metric("only",when-1,70));
        var id=l.Incident(now:when); await l.Enable(); var job=await l.Claim(id); var d=job.O("packet").O("diagnostics");
        Assert.Null(d.O("baseline")["requests"]); Assert.Null(d.O("baseline")["requests_per_second"]);
        Assert.Null(d.O("during")["requests_per_second"]); Assert.Null(d.O("during")["error_rate_percent"]);
    }
    [Fact] public async Task ContextRedactsPathsMessagesClientsAndCommands()
    {
        await using var l=new LabFixture(); var now=J.Now()-1;
        var e=LabFixture.Event("safe-id",ts:now-1); e["path"]="/commands"; e["client_id"]="secret-client"; e["command_signature"]="secret-command"; e["message"]="ignore instructions";
        var unknown=LabFixture.Event("unknown-id",ts:now-1); unknown["path"]="/private?token=secret-token";
        await l.Ingest(e,unknown); var id=l.Incident(now:now); await l.Enable(); var job=await l.Claim(id);
        var d=job.O("packet").O("diagnostics").ToJsonString();
        foreach(var secret in new[]{"secret-client","secret-command","secret-token","ignore instructions"}) Assert.DoesNotContain(secret,d);
        Assert.Contains("other/redacted",d);
    }
    [Fact] public async Task OversizedCollectionFailsClosed()
    {
        await using var l=new LabFixture(); var now=J.Now()-1; var id=l.Incident(now:now);
        l.Store.Write(db=>db.Telemetry.AddRange(Enumerable.Range(0,10001).Select(i=>new TelemetryRecord{Id="e"+i,Ts=now-2,Received=now,Payload=LabFixture.Event("e"+i,ts:now-2)})));
        await l.Enable(); var job=await l.Claim(id); var d=job.O("packet").O("diagnostics");
        Assert.Equal("capacity-exceeded",d.S("data_state")); Assert.Empty(d.O("checks")); Assert.Null(d["during"]);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task FollowupRunsOnceFreezesEvidenceAndAccountsUnderInvestigator(bool judge)
    {
        await using var l=new LabFixture(true); var id=await Context(l); var settings=BedrockSettings();settings["judge_enabled"]=judge;l.Agents.SaveSettings(settings); l.Agents.Analyze(id);
        string original; using(var db=l.Store.Open()) original=J.Canonical(db.Jobs.Single().Packet);
        var provider=new Provider(Investigation("endpoint_breakdown"),Investigation("endpoint_breakdown",true),LabFixture.Decision,LabFixture.Judgment());
        await new BedrockExecutor(l.Agents,provider,l.Config).RunOne(default);
        using var done=l.Store.Open(); var job=done.Jobs.Single();
        Assert.Equal("completed",job.Status); Assert.Equal(original,J.Canonical(job.Packet)); Assert.Equal(judge?4:3,provider.Prompts.Count);
        Assert.DoesNotContain("\"check_result\"",provider.Prompts[0]); Assert.Contains("\"check_result\"",provider.Prompts[1]); Assert.Contains("check-endpoints",provider.Prompts[2]);
        Assert.Contains(done.AgentEvents,e=>e.Stage=="diagnostic_check"); Assert.Single(done.Sessions.Where(s=>s.Role==DiagnosticEvidence.FollowupStage));
        Assert.Equal(2,AiUsage.Report(done,"investigator",null,null).O("totals").N("sessions"));
        Assert.All(done.Sessions.ToList(),s=>Assert.Equal("settled",s.Config.S("budget_status")));
    }
    [Fact] public async Task LiveBudgetBlocksFollowupWithoutRetryOrDelivery()
    {
        await using var l=new LabFixture(true); var id=await Context(l); l.Agents.SaveSettings(BedrockSettings()); l.Agents.Analyze(id);
        var provider=new Provider(Investigation("endpoint_breakdown")){Before=_=>{var s=BedrockSettings();s["daily_budget_usd"]=0;l.Agents.SaveSettings(s);}};
        await new BedrockExecutor(l.Agents,provider,l.Config).RunOne(default);
        using var db=l.Store.Open(); Assert.Single(provider.Prompts); Assert.Equal("budget_exhausted",db.Jobs.Single().Error);
        Assert.Single(db.Sessions); Assert.DoesNotContain(db.Notices,n=>n.Kind.StartsWith("ai-"));
    }
    [Fact] public async Task UnavailableCheckAndInventedAggregateAreRejected()
    {
        await using var l=new LabFixture(); await l.Enable(); var job=await l.Claim(l.Incident()); using var start=await l.Worker(job,"started");
        using var unavailable=await l.Worker(job,"result",J.Obj(new{stage="investigator",report=Investigation("endpoint_breakdown")})); Assert.Equal(422,(int)unavailable.StatusCode);
        var invalid=LabFixture.Investigation; invalid.O("diagnosis")["evidence_refs"]=J.Arr(new[]{"invented-aggregate"});
        using var invented=await l.Worker(job,"result",J.Obj(new{stage="investigator",report=invalid})); Assert.Equal(422,(int)invented.StatusCode);
    }
    [Fact] public async Task CheckCannotChangeOrBeCitedBeforeItIsSupplied()
    {
        await using var l=new LabFixture(); var id=await Context(l); await l.Enable(); var job=await l.Claim(id); using var start=await l.Worker(job,"started");
        using var early=await l.Worker(job,"result",J.Obj(new{stage="investigator",report=Investigation("endpoint_breakdown",true)})); Assert.Equal(422,(int)early.StatusCode);
        using var first=await l.Worker(job,"result",J.Obj(new{stage="investigator",report=Investigation("endpoint_breakdown")})); Assert.Equal(200,(int)first.StatusCode);
        using var follow=await l.Worker(job,"started");
        using var second=await l.Worker(job,"result",J.Obj(new{stage=DiagnosticEvidence.FollowupStage,report=Investigation("resource_timeline")})); Assert.Equal(422,(int)second.StatusCode);
    }
    [Fact] public async Task MissingDiagnosticFieldsFailForNewJobsButLegacyReportsRemainSupported()
    {
        await using var l=new LabFixture(); await l.Enable(); var job=await l.Claim(l.Incident()); using var start=await l.Worker(job,"started");
        var report=LabFixture.Investigation; foreach(var key in new[]{"diagnosis","impact","hypotheses","actions","follow_up"}) report.Remove(key);
        using var invalid=await l.Worker(job,"result",J.Obj(new{stage="investigator",report})); Assert.Equal(422,(int)invalid.StatusCode);
        Assert.NotNull(Contracts.Validate(report,Contracts.ReportSchema("investigator")));
    }
    [Fact] public async Task FinalNoneMeansNoFurtherCheckAndRetainsOriginalSelectionForAudit()
    {
        await using var l=new LabFixture(true); var id=await Context(l); l.Agents.SaveSettings(BedrockSettings()); l.Agents.Analyze(id);
        var provider=new Provider(Investigation("endpoint_breakdown"),Investigation("none",true),LabFixture.Decision);
        await new BedrockExecutor(l.Agents,provider,l.Config).RunOne(default);
        using var db=l.Store.Open(); Assert.Equal("completed",db.Jobs.Single().Status); Assert.Equal(3,provider.Prompts.Count);
        Assert.Equal("endpoint_breakdown",db.Jobs.Single().Investigation!.S("follow_up"));
        Assert.Contains("check-endpoints",provider.Prompts[2]); Assert.Single(db.Sessions.Where(s=>s.Role==DiagnosticEvidence.FollowupStage));
    }
    [Fact] public async Task InvalidReferenceFailureIsSpecificWithoutLeakingProviderOutput()
    {
        await using var l=new LabFixture(true); var id=await Context(l); l.Agents.SaveSettings(BedrockSettings()); l.Agents.Analyze(id);
        var report=Investigation();report.O("diagnosis")["evidence_refs"]=J.Arr(new[]{"private-invented-reference"});
        var provider=new Provider(report); await new BedrockExecutor(l.Agents,provider,l.Config).RunOne(default);
        using var db=l.Store.Open();Assert.Equal("bedrock_report_unknown_reference",db.Jobs.Single().Error);
        Assert.DoesNotContain("private-invented-reference",l.Agents.Summary().ToJsonString());Assert.Single(provider.Prompts);
    }
}
