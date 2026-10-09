using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace IncidentLab.Tests;

public sealed class ApiTests
{
    [Fact] public async Task EmptyDashboardIsNotHealthyOrMocked()
    {
        await using var lab = new LabFixture(); var overview = await lab.Get("/api/overview");
        Assert.Equal("waiting", overview.S("data_state"));
        foreach (var field in new[] { "request_count", "requests_per_second", "error_rate", "resources" }) Assert.Null(overview[field]);
        Assert.Empty(overview.A("histogram")); Assert.Equal(0, overview.N("total_events")); Assert.Equal(0, overview.O("ai").N("calls"));
    }
    [Fact] public async Task RealIngestIsAuthenticatedAndDeduplicated()
    {
        await using var lab = new LabFixture(); var e = LabFixture.Event("a", "metric");
        using var unauthorized = await lab.Send("/api/ingest", J.Arr(new[] { e })); Assert.Equal(401, (int)unauthorized.StatusCode);
        await lab.Ingest(e, e); await lab.Ingest(e); Assert.Equal(1, (await lab.Get("/api/overview")).N("total_events"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/ingest") { Content = JsonContent.Create(J.Arr(new[] { LabFixture.Event("future", ts: J.Now() + 100) })) };
        request.Headers.Add("X-Lab-Ingest-Key", lab.Config.IngestKey);
        using var invalid = await lab.Client.SendAsync(request); Assert.Equal(422, (int)invalid.StatusCode);
    }
    [Fact] public async Task DashboardUsesIngestedCountsAndProcessMetrics()
    {
        await using var lab = new LabFixture(); var metric = LabFixture.Event("m", "metric"); metric["cpu_percent"] = 12.5; metric["memory_mb"] = 64.7;
        await lab.Ingest(LabFixture.Event("r1"), LabFixture.Event("r2", status: 500), LabFixture.Event("r3"), metric);
        await lab.Runtime.Tick(); var o = await lab.Get("/api/overview");
        Assert.Equal("live", o.S("data_state")); Assert.Equal(3, o.N("request_count")); Assert.Equal(33.3, o.N("error_rate"));
        Assert.Equal(12.5, o.O("resources").N("cpu_percent")); Assert.Equal(64.7, o.O("resources").N("memory_mb"));
        Assert.Equal(3, o.A("histogram").Sum(b => b.N("requests")));
    }
    [Theory][InlineData("source")][InlineData("analyzer")][InlineData("store")]
    public async Task UnavailablePipelineNeverShowsLiveValues(string failure)
    {
        await using var lab = new LabFixture(); var now = J.Now();
        lab.Runtime.State = new([LabFixture.Event("metric", "metric")], now - 60, failure == "analyzer" ? now - 60 : now,
            failure == "source" ? now - 60 : now, "healthy", failure == "store" ? "Archive unavailable" : null);
        var data = await lab.Get("/api/overview"); Assert.Equal(failure == "store" ? "unavailable" : "stale", data.S("data_state"));
        Assert.Null(data["resources"]); Assert.Null(data["requests_per_second"]); Assert.Empty(data.A("histogram"));
    }
    [Fact] public async Task ObservedIdleIsZeroTrafficNotZeroErrorRate()
    {
        await using var lab = new LabFixture(); await lab.Ingest(LabFixture.Event("metric", "metric")); await lab.Runtime.Tick();
        var data = await lab.Get("/api/overview"); Assert.Equal("live", data.S("data_state")); Assert.Equal(0, data.N("request_count")); Assert.Null(data["error_rate"]);
    }
    [Fact] public async Task RecoveryRequiresFreshSustainedTelemetryAndDeduplicates()
    {
        await using var lab = new LabFixture(); var finding = new Finding("repeat-command", "behavior", "warning", "target-api", "client/same", "Repeated command", 20, 12, [], "Check retries");
        foreach (var now in new[] { 100, 102, 104 }) Incidents.Apply(lab.Store, [finding], now, true, 8);
        using (var db = lab.Store.Open()) { Assert.Single(db.Incidents); Assert.Single(db.Notices); }
        Incidents.Apply(lab.Store, [], 105, true, 8); Incidents.Apply(lab.Store, [], 108, false, 8); Incidents.Apply(lab.Store, [], 200, true, 8);
        using (var db = lab.Store.Open()) Assert.Equal("recovering", db.Incidents.Single().Status);
        Incidents.Apply(lab.Store, [], 209, true, 8);
        using (var db = lab.Store.Open()) { Assert.Equal("resolved", db.Incidents.Single().Status); Assert.Equal(2, db.Notices.Count()); }
        Incidents.Apply(lab.Store, [finding], 250, true, 8); using (var db = lab.Store.Open()) Assert.Equal(2, db.Incidents.Count());
    }
    [Fact] public async Task SourceGapDoesNotResolveExistingIncident()
    {
        await using var lab = new LabFixture(); var id = lab.Incident(); lab.Runtime.State = new([], J.Now() - 30);
        await lab.Runtime.Tick(); using var db = lab.Store.Open();
        Assert.Equal("active", db.Incidents.Find(id)!.Status); Assert.Contains(db.Incidents, i => i.Rule == "source-stale");
    }
    [Fact] public async Task ControlsRejectCrossOriginMutation()
    {
        await using var lab = new LabFixture(); using var response = await lab.Send("/api/settings/ai", new JsonObject(), "PUT", origin: "https://unrelated.example");
        Assert.Equal(403, (int)response.StatusCode);
    }
    [Theory][InlineData("target_url", "https://unrelated.example")][InlineData("scenario", "arbitrary")]
    public async Task ScenarioDoesNotAcceptArbitraryTargetsOrUnknownFields(string key, string value)
    {
        await using var lab = new LabFixture(); var body = J.Obj(new { scenario = "normal" }); body[key] = value;
        using var response = await lab.Send("/api/runs", body); Assert.Equal(422, (int)response.StatusCode);
    }
    [Fact] public async Task ScenarioRejectsExcessiveLoad()
    {
        await using var lab = new LabFixture(); using var response = await lab.Send("/api/runs", J.Obj(new { scenario = "normal", rps = 10000 })); Assert.Equal(422, (int)response.StatusCode);
    }
    [Fact] public async Task PayloadLimitCountsBytesAndBadJsonIs422()
    {
        await using var lab = new LabFixture(); using var large = new StringContent(new string('x', 1_000_001), Encoding.UTF8, "application/json");
        using var response = await lab.Client.PutAsync("/api/settings/ai", large); Assert.Equal(413, (int)response.StatusCode);
        using var bad = await lab.Client.PutAsync("/api/settings/ai", new StringContent("{", Encoding.UTF8, "application/json")); Assert.Equal(422, (int)bad.StatusCode);
    }
    [Fact] public async Task CatalogsAndIncidentReportsStayEnglish()
    {
        await using var lab = new LabFixture(); var scenarios = (await lab.Client.GetFromJsonAsync<JsonArray>("/api/scenarios"))!;
        Assert.Equal(new[] { "Normal traffic", "Retry loop", "HTTP probing", "Application errors", "Slow responses", "CPU pressure", "Memory pressure", "Telemetry interruption" }, scenarios.Select(s => s.S("name")));
        var events = Enumerable.Range(0, 5).Select(i => LabFixture.Event(i.ToString(), status: 500)).ToArray();
        await lab.Ingest(events); await lab.Ingest(LabFixture.Event("metric", "metric")); await lab.Runtime.Tick();
        var incident = (await lab.Get("/api/overview")).A("incidents").Single()!;
        Assert.Equal("Increased service error rate", incident.S("title")); Assert.Equal("5/5 requests returned HTTP 5xx within the window.", incident.O("report").S("summary"));
        using var ack = await lab.Send($"/api/incidents/{incident.S("id")}/ack"); Assert.Equal(200, (int)ack.StatusCode);
        Assert.True((await lab.Get($"/api/incidents/{incident.S("id")}")).B("acknowledged"));
    }
}
