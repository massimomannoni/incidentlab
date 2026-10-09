using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileProviders;

namespace IncidentLab.Api;

public static class LabApplication
{
    public static WebApplication Build(string[] args, LabConfig? config = null, bool background = true, Action<WebApplicationBuilder>? configure = null)
    {
        config ??= LabConfig.FromEnvironment(); config.Validate();
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_000_000);
        builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new Store(config.DatabaseUrl));
        builder.Services.AddHttpClient("lab", c => c.Timeout = TimeSpan.FromSeconds(8)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        builder.Services.AddSingleton<ITelemetry>(sp => new Telemetry(config, sp.GetRequiredService<Store>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("lab")));
        builder.Services.AddSingleton<Agents>();
        builder.Services.AddSingleton<IBedrockInference, BedrockInference>();
        builder.Services.AddSingleton<BedrockExecutor>();
        builder.Services.AddSingleton<MonitorRuntime>();
        builder.Services.AddSingleton<ScenarioRunner>(sp => new(config, sp.GetRequiredService<Store>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("lab")));
        if (background)
        {
            builder.Services.AddHostedService<AnalyzerService>();
            if (config.BedrockEnabled) builder.Services.AddHostedService<BedrockService>();
        }
        configure?.Invoke(builder);
        var app = builder.Build();
        var store = app.Services.GetRequiredService<Store>(); store.Initialize();
        var runtime = app.Services.GetRequiredService<MonitorRuntime>();
        var agents = app.Services.GetRequiredService<Agents>();
        var runner = app.Services.GetRequiredService<ScenarioRunner>();

        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers.CacheControl = context.Request.Path.StartsWithSegments("/api") ? "no-store" : "no-cache";
            try
            {
                if (context.Request.ContentLength > 1_000_000) throw new LabException(413, "Request too large");
                var origin = context.Request.Headers.Origin.ToString();
                if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && origin.Length > 0 &&
                    (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
                    throw new LabException(403, "Cross-origin mutation rejected");
                await next(context);
            }
            catch (LabException ex) { context.Response.StatusCode = ex.Status; await context.Response.WriteAsJsonAsync(new { detail = ex.Message }); }
            catch (JsonException) { context.Response.StatusCode = 422; await context.Response.WriteAsJsonAsync(new { detail = "Invalid JSON body" }); }
            catch (BadHttpRequestException ex) { context.Response.StatusCode = ex.StatusCode; await context.Response.WriteAsJsonAsync(new { detail = ex.StatusCode == 413 ? "Request too large" : "Invalid request" }); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // Do not emit credentials, database URLs, prompts or raw provider output to logs.
                app.Logger.LogError("Request failed: {ErrorType}", ex.GetType().Name);
                context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { detail = "Laboratory service unavailable" });
            }
        });
        app.MapGet("/api/health", () => { using var db = store.Open(); return Results.Ok(new { status = "ok", ai_enabled = Agents.Settings(db).B("enabled"), telemetry_backend = config.TelemetryBackend }); });
        app.MapPost("/api/ingest", (Func<HttpContext, Task<IResult>>)(async (HttpContext ctx) =>
        {
            if (!J.SecretEquals(ctx.Request.Headers["X-Lab-Ingest-Key"].ToString(), config.IngestKey)) throw new LabException(401, "Invalid ingest credential");
            var node = await Read(ctx);
            if (node is not JsonArray batch || batch.Count is < 1 or > 200) throw new LabException(422, "Batch must contain between 1 and 200 events");
            var events = batch.Select(e => Contracts.Validate(e, Contracts.EventSchema).AsObject()).ToList(); var now = J.Now();
            if (events.Any(e => e.N("ts") > now + 5 || e.N("ts") < now - 86400)) throw new LabException(422, "Event timestamp outside the accepted window");
            return Results.Json(new { accepted = store.Ingest(events, now) }, statusCode: 202);
        }));
        app.MapGet("/api/overview", () => runtime.Overview());
        app.MapGet("/api/events", () => { var state = runtime.State; return J.Obj(new { events = state.Events.OrderByDescending(e => e.N("ts")).Take(150), source = config.TelemetryBackend, stale = state.Health != "healthy" }); });
        app.MapGet("/api/incidents/{id}", (string id) =>
        {
            using var db = store.Open(); var item = db.Incidents.Find(id) ?? throw new LabException(404, "Incident not found"); var data = J.Obj(item);
            data["events"] = J.Arr(item.Evidence.A("evidence_ids").Select(e => db.Telemetry.Find(e!.GetValue<string>())?.Payload).Where(e => e != null));
            data["notices"] = J.Arr(db.Notices.Where(n => n.IncidentId == id).OrderBy(n => n.Ts).ToList());
            var jobs = db.Jobs.Where(j => j.IncidentId == id).OrderByDescending(j => j.Ts).ToList();
            data["agent_runs"] = J.Arr(jobs.Select(j => Agents.PublicJob(db, j)));
            data["context"] = IncidentContext.Read(db, item, jobs.FirstOrDefault(), J.Now()); return data;
        });
        app.MapPost("/api/incidents/{id}/ack", (string id) =>
        {
            store.Write(db => { var item = db.Incidents.Find(id) ?? throw new LabException(404, "Incident not found"); item.Acknowledged = true; });
            store.Log("incident.acknowledged", "Acknowledged: " + J.Clip(id, 8)); return new { acknowledged = true };
        });
        app.MapGet("/api/rules", () => J.Obj(new { rules = Detection.Rules, window_seconds = config.Window, recovery_seconds = config.RecoverySeconds, source_stale_seconds = config.StaleSeconds }));
        app.MapGet("/api/scenarios", () => ScenarioRunner.Scenarios);
        bool MasterAdmission(HttpContext ctx) => SimulationQuota.IsMaster(ctx.Request.Headers[SimulationQuota.MasterHeader].ToString(), config.ControlKey);
        app.MapGet("/api/limits", (HttpContext ctx) => { using var db = store.Open(); return J.Obj(new { simulations = SimulationQuota.Status(db, J.Now(), MasterAdmission(ctx)) }); });
        app.MapGet("/api/contracts", () => new JsonObject { ["event"] = Contracts.EventSchema, ["run"] = Contracts.RunSchema,
            ["settings"] = Contracts.SettingsSchema, ["investigation"] = Contracts.ReportSchema("investigator"),
            ["decision"] = Contracts.ReportSchema("manager"), ["judgment"] = Contracts.ReportSchema("judge") });
        app.MapGet("/api/runs", () =>
        {
            using var db = store.Open(); var rows = db.Runs.OrderByDescending(r => r.Started).Take(30).ToList();
            var incidents = db.Incidents.OrderByDescending(i => i.Opened).Take(1000).ToList();
            var result = new JsonArray();
            foreach (var row in rows)
            {
                row.Result.Remove("ai_calls"); var data = J.Obj(row);
                data["incidents"] = J.Arr(incidents.Where(i => i.Evidence.A("run_ids").Any(r => r!.GetValue<string>() == row.Id))
                    .Select(i => new { id = i.Id, title = i.Title, severity = i.Severity, status = i.Status }));
                result.Add(data);
            }
            return result;
        });
        app.MapPost("/api/runs", (Func<HttpContext, Task<IResult>>)(async (HttpContext ctx) =>
        {
            var spec = Contracts.Validate(await Read(ctx), Contracts.RunSchema).AsObject();
            try { return Results.Json(new { id = await runner.Start(spec, ctx.RequestAborted, MasterAdmission(ctx)) }, statusCode: 201); }
            catch (LabException) { throw; }
            catch (Exception) { throw new LabException(503, "Target unreachable or fault controls unavailable"); }
        }));
        app.MapPost("/api/runs/stop", async () => { await runner.Stop(); return new { status = "stopped" }; });
        app.MapGet("/api/notices", () => { using var db = store.Open(); return J.Arr(db.Notices.OrderByDescending(n => n.Ts).Take(100).ToList()); });
        app.MapGet("/api/settings/ai", () => { using var db = store.Open(); return Agents.Settings(db); });
        app.MapPut("/api/settings/ai", (Func<HttpContext, Task<IResult>>)(async ctx => Results.Json(agents.SaveSettings(await ObjectBody(ctx)))));
        app.MapGet("/api/agents", () => { var data = agents.Summary(); data["scheduler_error"] = runtime.AiError; return data; });
        app.MapGet("/api/ai/catalog", (string? provider) => { using var db = store.Open(); return provider == "bedrock" || provider == null && BedrockBudget.IsBedrock(Agents.Settings(db)) ? BedrockBudget.Catalog : Agents.Catalog(db); });
        app.MapGet("/api/ai/usage", (HttpContext ctx) =>
        {
            string? Query(string key) => ctx.Request.Query.ContainsKey(key) ? ctx.Request.Query[key].ToString() : null;
            var role = Query("role"); var id = Query("incident_id"); var raw = Query("since"); double? since = null;
            if (role != null && !Contracts.Roles.Contains(role) || id?.Length > 100) throw new LabException(422, "Invalid usage filters");
            if (raw != null)
            {
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < 0) throw new LabException(422, "Invalid usage filters");
                since = n;
            }
            using var db = store.Open(); return AiUsage.Report(db, role, id, since);
        });
        app.MapPost("/api/incidents/{id}/analyze", (string id) => agents.Analyze(id));
        app.MapPost("/api/worker/catalog", (Func<HttpContext, Task<IResult>>)(async ctx => Results.Json(agents.PutCatalog(await WorkerBody(ctx, agents, "catalog")))));
        app.MapPost("/api/worker/claim", (Func<HttpContext, Task<IResult>>)(async ctx => Results.Json(agents.Claim(await WorkerBody(ctx, agents, "claim")))));
        app.MapPost("/api/worker/jobs/{id}/heartbeat", async (string id, HttpContext ctx) => agents.Heartbeat(id, await WorkerBody(ctx, agents, "heartbeat")));
        app.MapPost("/api/worker/jobs/{id}/started", async (string id, HttpContext ctx) => agents.Started(id, await WorkerBody(ctx, agents, "started")));
        app.MapPost("/api/worker/jobs/{id}/result", async (string id, HttpContext ctx) => agents.Result(id, await WorkerBody(ctx, agents, "result")));
        app.MapPost("/api/worker/jobs/{id}/failed", async (string id, HttpContext ctx) => agents.Failed(id, await WorkerBody(ctx, agents, "failed")));
        var frontend = Path.GetFullPath(config.FrontendDir);
        if (Directory.Exists(Path.Combine(frontend, "assets"))) app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(Path.Combine(frontend, "assets")), RequestPath = "/assets" });
        app.MapGet("/", () => File.Exists(Path.Combine(frontend, "index.html")) ? Results.File(Path.Combine(frontend, "index.html"), "text/html") : Results.Json(new { message = "Build frontend or use Vite dev server" }));
        return app;
    }
    static async Task<JsonNode> Read(HttpContext context)
    {
        // Count actual bytes too, including chunked bodies and in-memory test hosting.
        using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
        while ((read = await context.Request.Body.ReadAsync(bytes, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > 1_000_000) throw new LabException(413, "Request too large");
            await buffer.WriteAsync(bytes.AsMemory(0, read), context.RequestAborted);
        }
        return JsonNode.Parse(buffer.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) ?? throw new LabException(422, "JSON body required");
    }
    static async Task<JsonObject> ObjectBody(HttpContext ctx) => await Read(ctx) as JsonObject ?? throw new LabException(422, "JSON object required");
    static async Task<JsonObject> WorkerBody(HttpContext ctx, Agents agents, string action)
    {
        agents.Authorize(ctx.Request.Headers["X-Lab-Worker-Key"].ToString());
        return Contracts.Validate(await Read(ctx), Contracts.WorkerSchema(action)).AsObject();
    }
}

public sealed class AnalyzerService(MonitorRuntime runtime, LabConfig config, ScenarioRunner runner, ILogger<AnalyzerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await runtime.Tick(stoppingToken); runtime.Schedule(); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Analyzer cycle failed: {ErrorType}", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(config.Interval), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken) { await base.StopAsync(cancellationToken); await runner.Stop(); }
}
