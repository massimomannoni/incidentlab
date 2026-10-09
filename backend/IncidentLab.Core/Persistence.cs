using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;

namespace IncidentLab;

public abstract class Entity { [Key] public string Id { get; set; } = J.Id(); }
public sealed class TelemetryRecord : Entity
{
    public double Ts { get; set; }
    public double Received { get; set; }
    public bool Sent { get; set; }
    public JsonObject Payload { get; set; } = new();
}
public sealed class Incident : Entity
{
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = "active";
    public string Severity { get; set; } = "warning";
    public string Category { get; set; } = "";
    public string Rule { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Title { get; set; } = "";
    public double Opened { get; set; }
    public double Updated { get; set; }
    public double LastSignal { get; set; }
    public double? HealthySince { get; set; }
    public double? Closed { get; set; }
    public bool Acknowledged { get; set; }
    public JsonObject Evidence { get; set; } = new();
    public JsonObject Report { get; set; } = new();
}
public sealed class Notice : Entity
{
    public string IncidentId { get; set; } = "";
    public double Ts { get; set; } = J.Now();
    public string Kind { get; set; } = "";
    public string Channel { get; set; } = "lab-inbox";
    public JsonObject Body { get; set; } = new();
}
public sealed class Experiment : Entity
{
    public string Scenario { get; set; } = "";
    public string Status { get; set; } = "running";
    public double Started { get; set; } = J.Now();
    public double? Ended { get; set; }
    public JsonObject Config { get; set; } = new();
    public JsonObject Result { get; set; } = new();
}
public sealed class Setting
{
    [Key] public string Key { get; set; } = "";
    public JsonObject Value { get; set; } = new();
}
public sealed class AgentJob : Entity
{
    public string IncidentId { get; set; } = "";
    public double Ts { get; set; } = J.Now();
    public double Updated { get; set; } = J.Now();
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "queue";
    public string? WorkerId { get; set; }
    public string? Lease { get; set; }
    public double? Deadline { get; set; }
    public JsonObject Packet { get; set; } = new();
    public JsonObject Settings { get; set; } = new();
    public JsonObject? Investigation { get; set; }
    public JsonObject? Decision { get; set; }
    public JsonObject? Policy { get; set; }
    public JsonObject Usage { get; set; } = new();
    public string? Error { get; set; }
}
public sealed class AgentSession : Entity
{
    public string JobId { get; set; } = "";
    public string IncidentId { get; set; } = "";
    public string Role { get; set; } = "";
    public double Started { get; set; } = J.Now();
    public double? Ended { get; set; }
    public string Status { get; set; } = "running";
    public JsonObject Config { get; set; } = new();
    public string? ProviderSessionId { get; set; }
    public JsonObject Usage { get; set; } = new();
    public JsonObject? Report { get; set; }
    public string? Error { get; set; }
}
public sealed class AgentEvent : Entity
{
    public string JobId { get; set; } = "";
    public double Ts { get; set; } = J.Now();
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
}
public sealed class Activity : Entity
{
    public double Ts { get; set; } = J.Now();
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class LabDb(DbContextOptions<LabDb> options) : DbContext(options)
{
    public DbSet<TelemetryRecord> Telemetry => Set<TelemetryRecord>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Notice> Notices => Set<Notice>();
    public DbSet<Experiment> Runs => Set<Experiment>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AgentJob> Jobs => Set<AgentJob>();
    public DbSet<AgentSession> Sessions => Set<AgentSession>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();
    public DbSet<Activity> Activities => Set<Activity>();
    public static string Encode(JsonObject value) => value.ToJsonString();
    public static JsonObject Decode(string value) => JsonNode.Parse(value)?.AsObject()!;

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Keep explicit table/column names and JSON, not JSONB, on PostgreSQL.
        // Opening an existing database must not rewrite rows or reset volumes.
        var tables = new (Type type, string table)[] { (typeof(TelemetryRecord), "telemetry"), (typeof(Incident), "incidents"),
            (typeof(Notice), "notices"), (typeof(Experiment), "runs"), (typeof(Setting), "settings"), (typeof(AgentJob), "agent_jobs"),
            (typeof(AgentSession), "agent_sessions"), (typeof(AgentEvent), "agent_events"), (typeof(Activity), "activity") };
        var json = new ValueConverter<JsonObject, string>(v => Encode(v), v => Decode(v));
        var comparer = new ValueComparer<JsonObject>((a, b) => Encode(a!) == Encode(b!), v => Encode(v).GetHashCode(), v => Decode(Encode(v)));
        foreach (var (type, table) in tables)
        {
            var entity = model.Entity(type); entity.ToTable(table);
            // No inheritance hierarchy in the original relational model.
            if (type != typeof(Setting)) entity.HasBaseType((Type?)null);
            foreach (var property in type.GetProperties())
            {
                var p = entity.Property(property.Name).HasColumnName(JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name));
                if (property.PropertyType == typeof(JsonObject))
                {
                    p.HasConversion(json).HasColumnType(Database.IsNpgsql() ? "json" : "JSON");
                    p.Metadata.SetValueComparer(comparer);
                }
            }
        }
        model.Entity<TelemetryRecord>().HasIndex(x => x.Ts);
        model.Entity<TelemetryRecord>().HasIndex(x => x.Sent);
        model.Entity<Incident>().HasIndex(x => x.Fingerprint);
        model.Entity<Notice>().HasIndex(x => x.IncidentId);
        model.Entity<AgentJob>().HasIndex(x => x.IncidentId).IsUnique();
        model.Entity<AgentSession>().HasIndex(x => new { x.JobId, x.Role }).IsUnique();
        model.Entity<AgentSession>().HasIndex(x => x.IncidentId);
        model.Entity<AgentEvent>().HasIndex(x => x.JobId);
    }
    public JsonObject? SettingValue(string key) => Settings.Find(key)?.Value;
    public void PutSetting(string key, JsonObject value)
    {
        var row = Settings.Find(key);
        if (row == null) Settings.Add(new() { Key = key, Value = value }); else row.Value = value;
    }
}

public sealed class Store
{
    readonly DbContextOptions<LabDb> options;
    // The POC intentionally has one monitor process. Serialize write transactions, never HTTP I/O.
    public object Gate { get; } = new();
    public Store(string url)
    {
        var builder = new DbContextOptionsBuilder<LabDb>();
        if (url.StartsWith("sqlite:///", StringComparison.Ordinal))
        {
            var path = url[10..];
            if (path == ":memory:") throw new ArgumentException("Use a temporary SQLite file; per-context in-memory databases are unsupported");
            builder.UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15 }.ToString());
        }
        else
        {
            var uri = new Uri(url.Replace("postgresql+psycopg://", "postgresql://", StringComparison.Ordinal));
            if (uri.Scheme is not ("postgresql" or "postgres")) throw new ArgumentException("DATABASE_URL must use SQLite or PostgreSQL");
            var user = uri.UserInfo.Split(':', 2);
            builder.UseNpgsql(new NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.IsDefaultPort ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')), Username = Uri.UnescapeDataString(user[0]),
                Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : "", Timeout = 15 }.ToString());
        }
        options = builder.Options;
    }
    public LabDb Open() => new(options);
    public void Initialize()
    {
        lock (Gate)
        {
            using var db = Open(); db.Database.EnsureCreated();
            // Existing databases must have the published schema; do not silently recreate or erase them.
            _ = db.Sessions.Take(1).ToList();
            foreach (var run in db.Runs.Where(r => r.Status == "running" || r.Status == "settling"))
            { run.Status = "interrupted"; run.Ended = J.Now(); run.Result["phase"] = "interrupted"; run.Result["error"] = "Monitor restarted"; }
            if (db.SettingValue("ai") == null) db.PutSetting("ai", Contracts.Settings(new()));
            db.SaveChanges();
        }
    }
    public T Write<T>(Func<LabDb, T> action)
    {
        lock (Gate)
        {
            using var db = Open(); using var tx = db.Database.BeginTransaction();
            var result = action(db); db.SaveChanges(); tx.Commit(); return result;
        }
    }
    public void Write(Action<LabDb> action) => Write(db => { action(db); return 0; });
    public void Log(string type, string message) => Write(db => db.Activities.Add(new() { Type = type, Message = J.Clip(message, 500) }));
    public List<JsonObject> Events(double since, int limit)
    {
        using var db = Open(); return db.Telemetry.AsNoTracking().Where(x => x.Ts >= since).OrderByDescending(x => x.Ts).Take(limit).Select(x => x.Payload).ToList();
    }
    public int Ingest(IEnumerable<JsonObject> events, double now) => Write(db =>
    {
        int count = 0;
        foreach (var e in events.DistinctBy(e => e.S("id")))
        {
            if (db.Telemetry.Find(e.S("id")) != null) continue;
            db.Telemetry.Add(new() { Id = e.S("id"), Ts = e.N("ts"), Received = now, Payload = e.Copy() }); count++;
        }
        return count;
    });
}
