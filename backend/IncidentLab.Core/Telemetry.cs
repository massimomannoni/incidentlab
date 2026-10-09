using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace IncidentLab;

public interface ITelemetry
{
    Task Sync(CancellationToken ct = default);
    Task<List<JsonObject>> Read(double since, int limit = 10000, CancellationToken ct = default);
}
public sealed class Telemetry(LabConfig config, Store store, HttpClient client) : ITelemetry
{
    public const string Index = "incidentlab-events-v1";
    bool ready;
    public async Task Sync(CancellationToken ct = default)
    {
        if (config.TelemetryBackend == "sqlite") { ready = true; return; }
        var url = config.OpenSearchUrl.TrimEnd('/');
        if (!ready)
        {
            var fields = new Dictionary<string, object>();
            foreach (var name in new[] { "ts", "duration_ms", "cpu_percent", "memory_mb" }) fields[name] = new { type = "double" };
            foreach (var name in new[] { "id", "kind", "service", "instance" }) fields[name] = new { type = "keyword" };
            fields["status"] = new { type = "integer" };
            using var response = await client.PutAsJsonAsync($"{url}/{Index}", new { settings = new { number_of_shards = 1, number_of_replicas = 0 }, mappings = new { properties = fields } }, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                if (response.StatusCode != HttpStatusCode.BadRequest || error.O("error").S("type") != "resource_already_exists_exception") response.EnsureSuccessStatusCode();
            }
            ready = true;
        }
        List<TelemetryRecord> rows;
        using (var db = store.Open()) rows = db.Telemetry.AsNoTracking().Where(r => !r.Sent).OrderBy(r => r.Received).Take(1000).ToList();
        if (rows.Count == 0) return;
        var body = string.Concat(rows.Select(r => J.Obj(new { index = new { _index = Index, _id = r.Id } }).ToJsonString() + "\n" + r.Payload.ToJsonString() + "\n"));
        using var bulk = await client.PostAsync($"{url}/_bulk?refresh=wait_for", new StringContent(body, Encoding.UTF8, "application/x-ndjson"), ct);
        bulk.EnsureSuccessStatusCode();
        var result = await bulk.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidDataException("Missing bulk result");
        var items = result.A("items"); var succeeded = new HashSet<string>();
        for (int i = 0; i < Math.Min(rows.Count, items.Count); i++)
            if (items[i].O("index").N("status") is >= 200 and < 300) succeeded.Add(rows[i].Id);
        store.Write(db => { foreach (var row in db.Telemetry.Where(r => succeeded.Contains(r.Id))) row.Sent = true; });
        if (succeeded.Count != rows.Count || items.Count != rows.Count) throw new InvalidDataException("Some OpenSearch documents could not be indexed; retained for retry");
    }
    public async Task<List<JsonObject>> Read(double since, int limit = 10000, CancellationToken ct = default)
    {
        if (config.TelemetryBackend == "sqlite")
        {
            var rows = store.Events(since, limit + 1);
            if (rows.Count > limit) throw new InvalidDataException("Telemetry window exceeds POC capacity");
            return rows;
        }
        using var response = await client.PostAsJsonAsync($"{config.OpenSearchUrl.TrimEnd('/')}/{Index}/_search", new {
            size = limit, track_total_hits = true, sort = new[] { new { ts = "desc" } }, query = new { range = new { ts = new { gte = since } } } }, ct);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<JsonObject>(ct) ?? throw new InvalidDataException("Empty archive response");
        var hits = data.O("hits");
        if (hits.O("total").N("value") > limit || data.B("timed_out") || data.O("_shards").N("failed") > 0)
            throw new InvalidDataException("Telemetry window incomplete; evaluation paused, no automatic closure");
        return hits.A("hits").Select(h => h.O("_source").Copy()).ToList();
    }
}
