using System.Text.Json.Nodes;

namespace IncidentLab;

// A durable, single-monitor admission ledger, not a substitute for the AWS invoice.
// A call reserves the FULL model context, not a heuristic estimate of prompt tokens.
// Unknown outcomes retain their reservation across restarts and calendar boundaries.
public static class BedrockBudget
{
    public const string ModelId = "eu.amazon.nova-lite-v1:0";
    public const decimal InputUsdPerMillion = .10m;
    public const decimal OutputUsdPerMillion = .40m;
    public const int ContextTokens = 300_000;
    public const string StartMessage = "Bedrock invocation started.";
    public const string Billing = "Bedrock budget accounting uses conservative USD 0.10 / 0.40 per million input / output tokens. Not an AWS invoice; excludes taxes and infrastructure. Codex monetary usage is unavailable.";
    public static bool IsBedrock(JsonObject settings) => settings.S("execution_mode") == "cloud_api" && settings.S("provider") == "bedrock";
    public static decimal Amount(JsonNode? node, string key) => (decimal)(node?.N(key) ?? 0);
    public static decimal Cost(long input, long output) => (input * InputUsdPerMillion + output * OutputUsdPerMillion) / 1_000_000m;
    public static string Day(double now) => DateTimeOffset.FromUnixTimeSeconds((long)now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    public static string Month(double now) => Day(now)[..7];
    public static JsonObject Profile => J.Obj(new { provider = "bedrock", model_id = ModelId, model_family = "Amazon Nova", reasoning_effort = "default", selection_source = "server_allowlist" });
    public static JsonObject Catalog => J.Obj(new { models = new[] { new { id = ModelId, name = "Amazon Nova Lite · EU inference", family = "Amazon Nova", efforts = new[] { "default" }, default_effort = "default", is_default = true } }, source = "Server allowlist; AWS access must be verified separately", updated = (double?)null });
    public static void Validate(JsonObject settings, LabConfig config)
    {
        if (!config.BedrockEnabled) throw new LabException(422, "Bedrock is not enabled by the server operator");
        if (settings.S("region") != config.BedrockRegion || settings.S("model_id") != ModelId)
            throw new LabException(422, "This demo allows only Nova Lite EU inference from Milan");
        if (settings.N("max_output_tokens") > 2000 || settings.N("daily_job_limit") > 10 || settings.N("max_tool_calls") != 0)
            throw new LabException(422, "Bedrock demo: at most 2,000 output tokens, 10 jobs/day and no tools");
        if (Amount(settings, "daily_budget_usd") > config.BedrockDailyCapUsd || Amount(settings, "monthly_budget_usd") > config.BedrockMonthlyCapUsd)
            throw new LabException(422, "Budget exceeds the operator's deployment cap");
        foreach (var (_, p) in settings.O("agent_profiles"))
            if (p.S("model_id") is { Length: > 0 } id && id != ModelId || p.S("model_family") is not ("auto" or "Amazon Nova") || p.S("reasoning_effort") != "default")
                throw new LabException(422, "Nova Lite uses the server-approved model and default inference; reasoning effort is unsupported");
    }
    public static JsonObject Snapshot(LabDb db, LabConfig config, double now)
    {
        var rows = db.Sessions.ToList().Where(s => s.Config.S("provider") == "bedrock").ToList();
        decimal Spent(Func<double, bool> period) => rows.Sum(s => s.Config.S("budget_status") == "settled"
            ? period(s.Started) ? Amount(s.Config, "budget_cost_usd") : 0m
            : Amount(s.Config, "reserved_usd")); // Carry unresolved reservations into later periods.
        var settings = Agents.Settings(db);
        var daily = Math.Min(config.BedrockDailyCapUsd, Amount(settings, "daily_budget_usd"));
        var monthly = Math.Min(config.BedrockMonthlyCapUsd, Amount(settings, "monthly_budget_usd"));
        var usedDay = Spent(t => Day(t) == Day(now)); var usedMonth = Spent(t => Month(t) == Month(now));
        return J.Obj(new { currency = "USD", timezone = "UTC", day = Day(now), month = Month(now),
            daily_limit = daily, monthly_limit = monthly, daily_committed = usedDay, monthly_committed = usedMonth,
            daily_remaining = Math.Max(0, daily - usedDay), monthly_remaining = Math.Max(0, monthly - usedMonth),
            unresolved_reservations = rows.Count(s => s.Config.S("budget_status") != "settled"), billing = Billing });
    }
    public static void Reserve(LabDb db, AgentSession session, JsonObject settings, LabConfig config, double now)
    {
        Validate(settings, config);
        if (db.SettingValue("bedrock_budget_violation") != null) throw new LabException(409, "Bedrock accounting needs operator review; inference is stopped");
        var reservation = Cost(ContextTokens, (long)settings.N("max_output_tokens"));
        var state = Snapshot(db, config, now);
        if (reservation > Amount(state, "daily_remaining") || reservation > Amount(state, "monthly_remaining"))
            throw new LabException(429, "Bedrock budget exhausted or reserved. No model was invoked.");
        session.Config["reserved_usd"] = reservation; session.Config["budget_status"] = "reserved";
        session.Config["input_usd_per_million"] = InputUsdPerMillion; session.Config["output_usd_per_million"] = OutputUsdPerMillion;
        session.Config["region"] = config.BedrockRegion;
    }
    public static void Settle(LabDb db, AgentSession session)
    {
        if (session.Config.S("provider") != "bedrock" || session.Config.S("budget_status") == "settled") return;
        if (!session.Usage.ContainsKey("input_tokens") || !session.Usage.ContainsKey("output_tokens")) return;
        var cost = Cost((long)session.Usage.N("input_tokens"), (long)session.Usage.N("output_tokens"));
        session.Config["budget_cost_usd"] = cost; session.Config["budget_status"] = "settled";
        if (cost > Amount(session.Config, "reserved_usd")) db.PutSetting("bedrock_budget_violation", J.Obj(new { ts = J.Now(), session_id = session.Id }));
    }
}
