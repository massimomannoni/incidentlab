using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IncidentLab.Tests;

public sealed class PersistenceTests
{
    sealed class FakeHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
    static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    [Theory][InlineData(false)][InlineData(true)] public async Task BulkOnlyAcknowledgesSuccessfulDocuments(bool truncated)
    {
        await using var l = new LabFixture(); await l.Ingest(LabFixture.Event("a"), LabFixture.Event("b")); string? sent = null;
        using var client = new HttpClient(new FakeHttp(async req =>
        {
            if (req.Method == HttpMethod.Put) return Response(new { acknowledged = true });
            sent = await req.Content!.ReadAsStringAsync(); return Response(new { items = truncated ? new[] { new { index = new { status = 201 } } } : new[] { new { index = new { status = 201 } }, new { index = new { status = 429 } } } });
        }));
        var telemetry = new Telemetry(l.Config with { TelemetryBackend = "opensearch" }, l.Store, client);
        await Assert.ThrowsAsync<InvalidDataException>(() => telemetry.Sync());
        using var db = l.Store.Open(); Assert.True(db.Telemetry.Find("a")!.Sent); Assert.False(db.Telemetry.Find("b")!.Sent);
        Assert.Contains("\"_id\":\"a\"", sent!);
    }
    [Theory][InlineData("overflow")][InlineData("timeout")][InlineData("shards")]
    public async Task IncompleteOpenSearchWindowsAreRejected(string kind)
    {
        await using var l = new LabFixture();
        using var client = new HttpClient(new FakeHttp(_ => Task.FromResult(Response(new { timed_out = kind == "timeout", _shards = new { failed = kind == "shards" ? 1 : 0 }, hits = new { total = new { value = kind == "overflow" ? 3 : 0 }, hits = Array.Empty<object>() } }))));
        var telemetry = new Telemetry(l.Config with { TelemetryBackend = "opensearch" }, l.Store, client);
        await Assert.ThrowsAsync<InvalidDataException>(() => telemetry.Read(0, 2));
    }
    [Fact] public async Task SQLiteWindowOverflowAlsoFailsClosed()
    {
        await using var l = new LabFixture(); await l.Ingest(LabFixture.Event("a"), LabFixture.Event("b"), LabFixture.Event("c"));
        using var client = new HttpClient(); var telemetry = new Telemetry(l.Config, l.Store, client);
        await Assert.ThrowsAsync<InvalidDataException>(() => telemetry.Read(J.Now() - 10, 2));
    }
    [Fact] public async Task ArchiveFailureKeepsApplicationIncidentsActive()
    {
        await using var l = new LabFixture(); var id = l.Incident();
        using var client = new HttpClient(new FakeHttp(_ => throw new HttpRequestException("Test failure")));
        var runtime = new MonitorRuntime(l.Config, l.Store, new Telemetry(l.Config with { TelemetryBackend = "opensearch" }, l.Store, client), l.Agents);
        await runtime.Tick(); Assert.Equal("degraded", runtime.State.Health);
        using var db = l.Store.Open(); Assert.Equal("active", db.Incidents.Find(id)!.Status); Assert.Contains(db.Incidents, i => i.Rule == "storage-unavailable");
    }
    [Fact] public async Task ConcurrentExperimentStartAcceptsOnlyOneAndStopRestoresFaults()
    {
        await using var l = new LabFixture(); var resets = 0;
        using var client = new HttpClient(new FakeHttp(async request =>
        { await Task.Delay(10); if (request.RequestUri!.AbsolutePath == "/internal/faults") Interlocked.Increment(ref resets); return Response(new { ok = true }); }));
        await using var runner = new ScenarioRunner(l.Config, l.Store, client);
        async Task<bool> Start() { try { await runner.Start(J.Obj(new { scenario = "normal" })); return true; } catch (LabException e) when (e.Status == 409) { return false; } }
        var results = await Task.WhenAll(Start(), Start()); Assert.Single(results, r => r);
        await runner.Stop(); Assert.True(resets >= 2);
        using var db = l.Store.Open(); var run = Assert.Single(db.Runs); Assert.Equal("stopped", run.Status); Assert.Equal("not-complete", run.Result.S("evaluation"));
        Assert.Equal(run.Result.N("sent"), run.Result.N("responses") + run.Result.N("transport_errors") + run.Result.N("cancelled_requests"));
    }
    [Fact] public void ExistingRelationalSchemaAndHistoryOpenWithoutRecreation()
    {
        var directory = Directory.CreateTempSubdirectory("incidentlab-compat-"); var path = Path.Combine(directory.FullName, "legacy.db");
        try
        {
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open(); using var schema = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("IncidentLab.Tests.Fixtures.legacy-schema.sql")!);
                using var create = connection.CreateCommand(); create.CommandText = schema.ReadToEnd(); create.ExecuteNonQuery();
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO settings VALUES ('ai', '{"enabled":true,"execution_mode":"codex_local","provider":"bedrock","model_id":"old-model"}');
                    INSERT INTO telemetry VALUES ('original-event', 100, 100, 0, '{"id":"original-event","kind":"metric","ts":100}');
                    INSERT INTO runs VALUES ('old-run', 'normal', 'running', 100, NULL, '{}', '{}');
                    INSERT INTO incidents VALUES ('original-incident', 'target-api:server-errors:api', 'resolved', 'high', 'application', 'server-errors', 'api', 'Service errors', 100, 200, 100, 190, 200, 1, '{"evidence_ids":["original-event"]}', '{"summary":"Historical evidence"}');
                    INSERT INTO agent_jobs VALUES ('old-job', 'original-incident', 101, 102, 'completed', 'delivery', 'old-worker', 'old-lease', 1000, '{"rule":"server-errors"}', '{"model_id":"old-model"}', '{"summary":"Historical report"}', NULL, NULL, '{"investigator":{"input_tokens":40,"output_tokens":10}}', NULL);
                    INSERT INTO agent_events VALUES ('old-start', 'old-job', 101, 'investigator', 'Codex invocation started.');
                    """;
                seed.ExecuteNonQuery();
            }
            var store = new Store("sqlite:///" + path); store.Initialize(); store.Initialize();
            using var db = store.Open(); Assert.Single(db.Telemetry); Assert.True(db.Incidents.Single().Acknowledged);
            Assert.Equal("Historical evidence", db.Incidents.Single().Report.S("summary")); Assert.Equal("interrupted", db.Runs.Single().Status);
            Assert.Equal("old-model", Agents.Settings(db).O("agent_profiles").O("manager").S("model_id"));
            Assert.Equal("Historical report", db.Jobs.Single().Investigation!.S("summary"));
            var sessions = AiUsage.Sessions(db); Assert.Single(sessions); Assert.True(sessions[0].B("legacy")); Assert.Equal(50, AiUsage.Totals(sessions).N("reported_tokens"));
            Assert.Null(db.Jobs.Single().Decision); Assert.Empty(db.Sessions);
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(true); }
    }
    [Fact] public void PostgresModelUsesPublishedJsonAndSnakeCaseColumns()
    {
        var store = new Store("postgresql://test:placeholder@localhost:5432/test"); using var db = store.Open();
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE agent_sessions", script); Assert.Contains("packet json", script); Assert.Contains("last_signal", script);
        Assert.DoesNotContain("jsonb", script); // Schema generation only: not a PostgreSQL integration test.
    }
}
