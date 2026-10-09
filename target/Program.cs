using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 16_384);
builder.Services.AddSingleton<FaultState>();
builder.Services.AddSingleton<Telemetry>();
builder.Services.AddHostedService(s => s.GetRequiredService<Telemetry>());
builder.Services.AddHostedService<ResourceSampler>();
var app = builder.Build();
var telemetry = app.Services.GetRequiredService<Telemetry>();
var faults = app.Services.GetRequiredService<FaultState>();
var commands = new ConcurrentDictionary<string, object>();
var controlKey = Environment.GetEnvironmentVariable("LAB_CONTROL_KEY") ?? "";
if (controlKey.Length < 16) throw new InvalidOperationException("LAB_CONTROL_KEY requires at least 16 characters");

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal")) { await next(); return; }
    var watch = Stopwatch.StartNew();
    var requestId = Guid.NewGuid().ToString("N");
    context.Response.Headers["X-Request-Id"] = requestId;
    try
    {
        if (context.Request.Path != "/health")
        {
            var state = faults.PlanRequest();
            if (state.DelayMs > 0) await Task.Delay(state.DelayMs, context.RequestAborted);
            if (state.Fail)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new { error = "Processing failed", request_id = requestId });
                return;
            }
        }
        await next();
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        context.Response.StatusCode = 499;
    }
    catch (Exception ex)
    {
        context.Response.StatusCode = 500;
        app.Logger.LogError("Unhandled request error {ErrorType}", ex.GetType().Name);
    }
    finally
    {
        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var client = context.Request.Headers["X-Lab-Client"].ToString();
        var run = context.Request.Headers["X-Lab-Run"].ToString();
        if (run.Length != 32 || run.Any(c => !"0123456789abcdef".Contains(c))) run = "";
        var path = context.Request.Path.ToString();
        telemetry.Emit(new Dictionary<string, object?> {
            ["kind"] = "request", ["scope"] = "request", ["path"] = path[..Math.Min(path.Length, 512)],
            ["method"] = context.Request.Method, ["status"] = context.Response.StatusCode,
            ["duration_ms"] = watch.Elapsed.TotalMilliseconds, ["source_ip"] = source,
            ["client_id"] = client[..Math.Min(client.Length, 80)], ["request_id"] = requestId, ["run_id"] = run,
            ["command_signature"] = context.Items.TryGetValue("signature", out var signature) ? signature : ""
        });
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "target-api" }));
app.MapGet("/items", () => Results.Ok(new[] { new { id = "item-01", name = "Sample resource" }, new { id = "item-02", name = "Second resource" } }));
app.MapPost("/commands", (CommandInput input, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(input.Command) || string.IsNullOrWhiteSpace(input.Resource) || input.Command.Length > 80 || input.Resource.Length > 160)
        return Results.BadRequest(new { error = "Command and resource are required and must be bounded" });
    var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{input.Command}\0{input.Resource}")))[..16].ToLowerInvariant();
    context.Items["signature"] = signature;
    var id = Guid.NewGuid().ToString("N");
    var result = new { id, status = "completed", command = input.Command, resource = input.Resource };
    commands[id] = result;
    if (commands.Count > 2000) foreach (var key in commands.Keys.Take(500)) commands.TryRemove(key, out _);
    telemetry.Emit(new Dictionary<string, object?> { ["kind"] = "application", ["scope"] = "request",
        ["request_id"] = context.Response.Headers["X-Request-Id"].ToString(), ["command_signature"] = signature,
        ["message"] = "Command completed" });
    return Results.Ok(result);
});
app.MapGet("/commands/{id}", (string id) => commands.TryGetValue(id, out var value) ? Results.Ok(value) : Results.NotFound());
app.MapPost("/compute", async (ComputeInput input, HttpContext context) =>
{
    if (input.Milliseconds < 1 || input.Milliseconds > 200) return Results.BadRequest(new { error = "Allowed duration: 1–200 ms" });
    var result = await Task.Run(() => {
        var watch = Stopwatch.StartNew();
        double value = 0;
        while (watch.ElapsedMilliseconds < input.Milliseconds)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            for (var i = 1; i < 2000; i++) value += Math.Sqrt(i);
        }
        return value;
    }, context.RequestAborted);
    return Results.Ok(new { result });
});
app.MapPost("/internal/faults", (FaultInput input, HttpContext context) =>
{
    var supplied = context.Request.Headers["X-Lab-Control-Key"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(controlKey))) return Results.Unauthorized();
    if (input.ErrorRate is < 0 or > 1 || input.DelayMs is < 0 or > 2000 || input.MemoryMb is < 0 or > 128 || input.LeaseSeconds is < 0 or > 180)
        return Results.BadRequest(new { error = "Fault parameters exceed laboratory bounds" });
    faults.Set(input);
    return Results.Ok(new { applied = true, expires_in_seconds = input.LeaseSeconds });
});
app.Run();

record CommandInput(string Command, string Resource);
record ComputeInput(int Milliseconds);
record FaultInput(
    [property: JsonPropertyName("error_rate")] double ErrorRate = 0,
    [property: JsonPropertyName("delay_ms")] int DelayMs = 0,
    [property: JsonPropertyName("memory_mb")] int MemoryMb = 0,
    [property: JsonPropertyName("drop_telemetry")] bool DropTelemetry = false,
    [property: JsonPropertyName("lease_seconds")] int LeaseSeconds = 60);

sealed class FaultState : IDisposable
{
    readonly object gate = new();
    FaultInput value = new();
    DateTimeOffset expires;
    MemoryMappedFile? memory;
    MemoryMappedViewAccessor? memoryView;
    decimal failureRemainder;
    public FaultInput Read()
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow > expires) Reset();
            return value;
        }
    }
    public void Set(FaultInput input)
    {
        lock (gate)
        {
            Reset();
            value = input;
            expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, input.LeaseSeconds));
            if (input.MemoryMb > 0)
            {
                var size = input.MemoryMb * 1024 * 1024;
                // A dedicated mapping can be unmapped on reset; malloc/GC may retain RSS.
                memory = MemoryMappedFile.CreateNew(null, size);
                memoryView = memory.CreateViewAccessor();
                for (var i = 0; i < size; i += 4096) memoryView.Write(i, (byte)1);
            }
        }
    }
    public (int DelayMs, bool Fail) PlanRequest()
    {
        lock (gate)
        {
            if (DateTimeOffset.UtcNow > expires) Reset();
            // Deterministic fault scheduling: 0.6 produces 3 real HTTP failures per 5 requests.
            failureRemainder += (decimal)value.ErrorRate;
            var fail = failureRemainder >= 1;
            if (fail) failureRemainder -= 1;
            return (value.DelayMs, fail);
        }
    }
    void Reset()
    {
        memoryView?.Dispose();
        memory?.Dispose();
        memoryView = null;
        memory = null;
        value = new();
        failureRemainder = 0;
    }
    public void Dispose() { lock (gate) Reset(); }
}

sealed class Telemetry(FaultState faults, ILogger<Telemetry> logger) : BackgroundService
{
    readonly Channel<Dictionary<string, object?>> queue = Channel.CreateBounded<Dictionary<string, object?>>(5000);
    readonly string instance = Environment.GetEnvironmentVariable("INSTANCE_ID") ?? Environment.MachineName;
    long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public void Emit(Dictionary<string, object?> item)
    {
        item["id"] = Guid.NewGuid().ToString("N");
        item["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        item["service"] = "target-api";
        item["instance"] = instance;
        logger.LogInformation("Telemetry {Event}", JsonSerializer.Serialize(item));
        if (faults.Read().DropTelemetry) return;
        if (!queue.Writer.TryWrite(item)) Interlocked.Increment(ref dropped);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = Environment.GetEnvironmentVariable("MONITOR_URL") ?? "http://monitor:8010";
        var key = Environment.GetEnvironmentVariable("LAB_INGEST_KEY") ?? "";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("X-Lab-Ingest-Key", key);
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(250, stoppingToken);
                var batch = new List<Dictionary<string, object?>>();
                while (batch.Count < 200 && queue.Reader.TryRead(out var item)) batch.Add(item);
                var sent = false;
                for (var attempt = 0; attempt < 3 && !sent; attempt++)
                {
                    try
                    {
                        using var response = await http.PostAsJsonAsync(endpoint + "/api/ingest", batch, stoppingToken);
                        sent = response.IsSuccessStatusCode;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        if (stoppingToken.IsCancellationRequested) throw;
                        logger.LogWarning("Telemetry delivery failed {ErrorType}", ex.GetType().Name);
                    }
                    if (!sent) await Task.Delay(1000, stoppingToken);
                }
                if (!sent) Interlocked.Add(ref dropped, batch.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

sealed class ResourceSampler(Telemetry telemetry) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(2000, stoppingToken);
                process.Refresh();
                var next = process.TotalProcessorTime;
                var usage = (next - cpu).TotalSeconds / wall.Elapsed.TotalSeconds * 100;
                cpu = next;
                wall.Restart();
                telemetry.Emit(new Dictionary<string, object?> { ["kind"] = "metric", ["scope"] = "process",
                    ["cpu_percent"] = Math.Round(usage, 2), ["memory_mb"] = Math.Round(process.WorkingSet64 / 1048576.0, 2),
                    ["dropped_events"] = telemetry.Dropped });
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
