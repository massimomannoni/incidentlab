using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace IncidentLab.Api;

// Read-only entry point used by the AWS installer inside the monitor image.
public static class PipelineVerification
{
    public static async Task<int> Run()
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(100); JsonObject? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await client.GetFromJsonAsync<JsonObject>("http://127.0.0.1:8010/api/overview");
                if (last?.S("backend") == "opensearch" && last.S("data_state") == "live" && last.N("total_events") > 0 && last["resources"] != null) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            last = null; await Task.Delay(5000);
        }
        if (last == null) { Console.Error.WriteLine("Pipeline not live: check target telemetry, SGs, matching ingest keys and OpenSearch. Details withheld."); return 1; }
        try
        {
            var url = (Environment.GetEnvironmentVariable("OPENSEARCH_URL") ?? "http://opensearch:9200").TrimEnd('/');
            var result = await client.GetFromJsonAsync<JsonObject>($"{url}/{Telemetry.Index}/_count");
            if (result.N("count") <= 0) throw new InvalidDataException();
            Console.WriteLine(J.Obj(new { backend = last.S("backend"), data_state = last.S("data_state"), source_lag_seconds = last["source_lag"],
                indexed_events = result.N("count"), pending_outbox_events = last.N("pending_events") }).ToJsonString());
            Console.WriteLine("PASS: .NET telemetry -> authenticated ASP.NET Core ingest -> PostgreSQL outbox -> OpenSearch -> analyzer.");
            Console.WriteLine("This check did not inject faults or invoke AI. Run the explicit smoke suite separately."); return 0;
        }
        catch (Exception) { Console.Error.WriteLine("OpenSearch index unavailable or contains no real events. Details withheld."); return 1; }
    }
}
