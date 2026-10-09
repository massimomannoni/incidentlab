using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IncidentLab.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IncidentLab.Tests;

public sealed class LabFixture : IAsyncDisposable
{
    public const string WorkerKey = "test-worker-credential-only-not-real";
    readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("incidentlab-test-");
    public LabConfig Config { get; }
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public Store Store => App.Services.GetRequiredService<Store>();
    public MonitorRuntime Runtime => App.Services.GetRequiredService<MonitorRuntime>();
    public Agents Agents => App.Services.GetRequiredService<Agents>();
    public LabFixture(bool bedrock = false, decimal monthlyCapUsd = 5)
    {
        Config = new() { DatabaseUrl = "sqlite:///" + Path.Combine(directory.FullName, "lab.db"), TelemetryBackend = "sqlite",
            IngestKey = "test-ingest-key-not-real", ControlKey = "test-control-key-not-real", WorkerKey = WorkerKey, BedrockEnabled = bedrock, BedrockMonthlyCapUsd = monthlyCapUsd };
        App = LabApplication.Build([], Config, false, b => { b.WebHost.UseTestServer(); b.Logging.ClearProviders(); });
        Task.Run(() => App.StartAsync()).GetAwaiter().GetResult(); Client = App.GetTestClient();
    }
    public async Task<HttpResponseMessage> Send(string path, JsonNode? value = null, string method = "POST", bool worker = false, string? origin = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (value != null) request.Content = JsonContent.Create(value);
        if (worker) request.Headers.Add("X-Lab-Worker-Key", WorkerKey);
        if (origin != null) request.Headers.Add("Origin", origin);
        return await Client.SendAsync(request);
    }
    public async Task<JsonObject> Get(string path) => (await Client.GetFromJsonAsync<JsonObject>(path))!;
    public async Task<JsonObject> Enable(bool judge = false, bool auto = false, int limit = 10)
    {
        var value = Contracts.Settings(J.Obj(new { enabled = true, execution_mode = "codex_local", judge_enabled = judge, auto_analyze = auto, daily_job_limit = limit }));
        using var response = await Send("/api/settings/ai", value, "PUT"); Assert.Equal(200, (int)response.StatusCode); return value;
    }
    public string Incident(string severity = "high", string subject = "server", double? now = null)
    {
        Incidents.Apply(Store, [new("server-errors", "application", severity, "target-api", subject, "Service errors", 40, 25, ["event1"], "40% errors")], now ?? J.Now(), true, 8);
        using var db = Store.Open(); return db.Incidents.Single(i => i.Subject == subject).Id;
    }
    public async Task<JsonObject> Claim(string id)
    {
        using var queued = await Send($"/api/incidents/{id}/analyze"); Assert.Equal(200, (int)queued.StatusCode);
        using var claimed = await Send("/api/worker/claim", J.Obj(new { worker_id = "test-worker" }), worker: true);
        Assert.Equal(200, (int)claimed.StatusCode); return (await claimed.Content.ReadFromJsonAsync<JsonObject>())!.O("job");
    }
    public Task<HttpResponseMessage> Worker(JsonObject job, string action, JsonObject? extra = null)
    {
        var body = J.Obj(new { worker_id = "test-worker", lease = job.S("lease") });
        foreach (var (key, value) in extra ?? new()) body[key] = value?.DeepClone();
        return Send($"/api/worker/jobs/{job.S("id")}/{action}", body, worker: true);
    }
    public static JsonObject Investigation => J.Obj(new { summary = "Rule threshold exceeded; cause unknown.", classification = "reliability", confidence = "low",
        findings = new[] { new { observation = "Aggregate is above threshold.", evidence_ids = Array.Empty<string>() } }, limitations = new[] { "No request samples available" }, recommended_checks = new[] { "Inspect application logs" },
        diagnosis = new { status = "insufficient_evidence", explanation = "No causal context was observed.", evidence_refs = Array.Empty<string>() }, impact = "User impact unknown.",
        hypotheses = new[] { new { explanation = "An application failure is possible, but no cause is established.", supporting_refs = Array.Empty<string>(), contradicting_refs = Array.Empty<string>(), test = "Inspect correlated request traces." } },
        actions = new[] { new { kind = "verify", action = "Collect a bounded request trace.", rationale = "Distinguish handler errors from dependency failures.", verification = "Match a failure to a trace and error source." } }, follow_up = "none" });
    public static JsonObject Decision => J.Obj(new { action = "observe", priority = "low", rationale = "Limited evidence.", notification_title = "Service error rate",
        notification_message = "Investigate the service error rate.", suggested_channels = new[] { "slack" }, human_review_required = false });
    public static JsonObject Judgment(string verdict = "agree") => J.Obj(new { verdict, rationale = "Evidence and policy support the proposal.", concerns = Array.Empty<string>(), evidence_ids = Array.Empty<string>() });
    public async Task Complete(JsonObject job)
    {
        using var start = await Worker(job, "started"); Assert.Equal(200, (int)start.StatusCode);
        using var investigation = await Worker(job, "result", J.Obj(new { stage = "investigator", report = Investigation, usage = new { input_tokens = 40, output_tokens = 10 } })); Assert.Equal(200, (int)investigation.StatusCode);
        using var start2 = await Worker(job, "started"); Assert.Equal(200, (int)start2.StatusCode);
        using var manager = await Worker(job, "result", J.Obj(new { stage = "manager", report = Decision, usage = new { input_tokens = 50, output_tokens = 15 } })); Assert.Equal(200, (int)manager.StatusCode);
    }
    public static JsonObject Event(string id, string kind = "request", double? ts = null, int status = 200) => Contracts.Validate(J.Obj(new { id, kind, ts = ts ?? J.Now(), path = "/items", status }), Contracts.EventSchema).AsObject();
    public async Task Ingest(params JsonObject[] events)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/ingest") { Content = JsonContent.Create(J.Arr(events)) };
        req.Headers.Add("X-Lab-Ingest-Key", Config.IngestKey);
        using var response = await Client.SendAsync(req); Assert.Equal(202, (int)response.StatusCode);
    }
    public async ValueTask DisposeAsync()
    {
        Client.Dispose(); await App.StopAsync(); await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); directory.Delete(true);
    }
}
