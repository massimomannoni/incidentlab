using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IncidentLab.Worker;

public sealed class ModelCatalog(ICliLauncher launcher)
{
    public static string Family(string model)
    {
        var match = Regex.Match(model, @"^gpt-(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return match.Success ? "GPT-" + match.Groups[1].Value : "Other";
    }
    public async Task<JsonArray> Discover(CancellationToken ct = default)
    {
        var temp = Directory.CreateTempSubdirectory("incidentlab-catalog-");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(25));
            await using var process = launcher.Start(["app-server", "--listen", "stdio://"], temp.FullName);
            await using var lines = BoundedOutput.Lines(process.Output, 1_000_000, deadline.Token).GetAsyncEnumerator(deadline.Token);
            async Task<JsonObject> Rpc(int id, string method, object parameters)
            {
                // JSON-RPC uses camelCase parameters; do not apply the public API's snake_case policy.
                await process.Input.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters })); await process.Input.FlushAsync(deadline.Token);
                for (int i = 0; i < 100 && await lines.MoveNextAsync(); i++)
                {
                    var message = JsonNode.Parse(lines.Current)!.AsObject();
                    if (message["id"] == null || message.N("id", -1) != id) continue;
                    if (message.ContainsKey("error")) throw new WorkerFailure("codex_failed"); return message.O("result");
                }
                throw new WorkerFailure("codex_failed");
            }
            try
            {
                await Rpc(1, "initialize", new { clientInfo = new { name = "incidentlab", title = "IncidentLab", version = "0.2" } });
                await process.Input.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}"); await process.Input.FlushAsync(deadline.Token);
                var models = new JsonArray(); string? cursor = null;
                for (int page = 0; page < 10; page++)
                {
                    var result = await Rpc(page + 2, "model/list", new { limit = 100, includeHidden = false, cursor });
                    foreach (var m in result.A("data").Where(m => !m.B("hidden")))
                        models.Add(J.Obj(new { id = m.S("model"), name = m.S("displayName"), family = Family(m.S("model")),
                            efforts = m.A("supportedReasoningEfforts").Select(e => e.S("reasoningEffort")), default_effort = m.S("defaultReasoningEffort"), is_default = m.B("isDefault") }));
                    cursor = result["nextCursor"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(cursor)) return models;
                }
                throw new WorkerFailure("output_limit");
            }
            finally { await process.Stop(); }
        }
        finally { temp.Delete(recursive: true); }
    }
}
