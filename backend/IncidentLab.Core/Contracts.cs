using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IncidentLab;

// A deliberately small schema vocabulary shared by HTTP validation and CLI output schemas.
// Unknown fields, non-finite numbers, booleans-as-token-counts and oversized reports fail closed.
public static class Contracts
{
    static JsonObject Str(int max = 1000, int min = 0, string? pattern = null)
    {
        var s = new JsonObject { ["type"] = "string", ["maxLength"] = max, ["minLength"] = min };
        if (pattern != null) s["pattern"] = pattern;
        return s;
    }
    static JsonObject Enum(params string[] values) => J.Obj(new { type = "string", @enum = values });
    static JsonObject Number(double min, double max, bool integer = false) => J.Obj(new { type = integer ? "integer" : "number", minimum = min, maximum = max });
    static JsonObject Bool() => J.Obj(new { type = "boolean" });
    static JsonObject Default(JsonObject s, object? value) { s["default"] = System.Text.Json.JsonSerializer.SerializeToNode(value, J.Options); return s; }
    static JsonObject Nullable(JsonObject s) { s["nullable"] = true; return Default(s, null); }
    static JsonObject Array(JsonObject item, int max, int min = 0) => new() { ["type"] = "array", ["items"] = item, ["maxItems"] = max, ["minItems"] = min };
    static JsonObject Object(params (string name, JsonObject schema)[] fields)
    {
        var props = new JsonObject(); var required = new JsonArray();
        foreach (var (name, schema) in fields) { props[name] = schema; if (!schema.ContainsKey("default")) required.Add(name); }
        return new() { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false };
    }
    public static readonly string[] Efforts = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
    public static readonly string[] Roles = ["investigator", "manager", "judge"];
    public static readonly string[] Stages = ["investigator", DiagnosticEvidence.FollowupStage, "manager", "judge"];
    public static readonly string[] Reasons = ["timeout", "codex_failed", "invalid_output", "worker_stopped", "unexpected_tool", "output_limit"];
    public static readonly string[] ScenarioIds = ["normal", "retry", "probing", "errors", "latency", "cpu", "memory", "telemetry_gap"];
    const string ModelPattern = "^[a-zA-Z0-9._/:-]*$";
    const string IdentityPattern = "^[a-zA-Z0-9_.-]+$";
    public static JsonObject EventSchema => Object(
        ("id", Str(100, 1)), ("ts", Number(double.Epsilon, double.MaxValue)), ("kind", Enum("request", "metric", "application")),
        ("service", Default(Str(80), "target-api")), ("instance", Default(Str(100), "target")),
        ("scope", Default(Enum("process", "container", "host", "request"), "request")),
        ("path", Default(Str(512), "")), ("method", Default(Str(12), "")), ("status", Default(Number(0, 599, true), 0)),
        ("duration_ms", Default(Number(0, 600000), 0)), ("source_ip", Default(Str(80), "")), ("client_id", Default(Str(80), "")),
        ("request_id", Default(Str(100), "")), ("run_id", Default(Str(32, 0, "^([a-f0-9]{32})?$"), "")), ("command_signature", Default(Str(160), "")), ("message", Default(Str(1000), "")),
        ("cpu_percent", Nullable(Number(0, 100000))), ("memory_mb", Nullable(Number(0, 1000000))), ("dropped_events", Nullable(Number(0, int.MaxValue, true))));
    public static JsonObject RunSchema => Object(("scenario", Enum(ScenarioIds)), ("duration", Default(Number(30, 180, true), 50)),
        ("rps", Default(Number(1, 30, true), 6)), ("clients", Default(Number(1, 10, true), 3)), ("warmup", Default(Number(3, 15, true), 5)));
    static JsonObject ProfileSchema => Default(Object(("model_family", Default(Str(80), "auto")), ("model_id", Default(Str(200, 0, ModelPattern), "")),
        ("reasoning_effort", Default(Enum(["default", ..Efforts]), "default"))), new { });
    public static JsonObject SettingsSchema => Object(
        ("enabled", Default(Bool(), false)), ("execution_mode", Default(Enum("disabled", "codex_local", "cloud_api"), "disabled")),
        ("auto_analyze", Default(Bool(), false)), ("daily_job_limit", Default(Number(1, 100, true), 10)), ("max_evidence_events", Default(Number(1, 30, true), 12)),
        ("provider", Default(Enum("codex", "openai", "bedrock", "unselected"), "codex")), ("judge_enabled", Default(Bool(), false)),
        ("agent_profiles", Default(Object(Roles.Select(r => (r, ProfileSchema)).ToArray()), new { })),
        ("region", Default(Str(50, 0, "^[a-z0-9-]+$"), "eu-central-1")), ("model_id", Default(Str(200), "")),
        ("max_input_tokens", Default(Number(100, 32000, true), 6000)), ("max_output_tokens", Default(Number(100, 8000, true), 1000)),
        ("max_tool_calls", Default(Number(0, 12, true), 4)), ("timeout_seconds", Default(Number(5, 180, true), 45)), ("daily_budget_usd", Default(Number(0, 20), .50)),
        ("monthly_budget_usd", Default(Number(0, 100), 5)), ("max_prompt_bytes", Default(Number(4000, 24000, true), 16000)));
    public static JsonObject ReportSchema(string role, bool diagnostic = false)
    {
        JsonObject Optional(JsonObject schema, object? fallback) => diagnostic ? schema : Default(schema, fallback);
        return DiagnosticEvidence.Role(role) switch
        {
        "investigator" => Object(("summary", Str(1500)), ("classification", Enum("reliability", "suspicious_behavior", "resource_pressure", "monitoring_gap", "inconclusive")),
            ("confidence", Enum("low", "medium", "high")), ("findings", Array(Object(("observation", Str(900)), ("evidence_ids", Array(Str(), 30))), 6, 1)),
            ("limitations", Array(Str(14000), 6)), ("recommended_checks", Array(Str(14000), 6)),
            ("diagnosis", Optional(Object(("status", Enum("likely_cause", "insufficient_evidence")), ("explanation", Str(700, 1)), ("evidence_refs", Array(Str(100), 6))), new { status = "insufficient_evidence", explanation = "Legacy report; diagnostic context was not available.", evidence_refs = System.Array.Empty<string>() })),
            ("impact", Optional(Str(600, 1), "Not assessed in this legacy report.")),
            ("hypotheses", Optional(Array(Object(("explanation", Str(500, 1)), ("supporting_refs", Array(Str(100), 6)),
                ("contradicting_refs", Array(Str(100), 6)), ("test", Str(500, 1))), 2, diagnostic ? 1 : 0), System.Array.Empty<object>())),
            ("actions", Optional(Array(Object(("kind", Enum("verify", "mitigate", "fix")), ("action", Str(500, 1)),
                ("rationale", Str(500, 1)), ("verification", Str(500, 1))), 3, diagnostic ? 1 : 0), System.Array.Empty<object>())),
            ("follow_up", Optional(Enum(["none", ..DiagnosticEvidence.Checks]), "none"))),
        "manager" => Object(("action", Enum("observe", "notify", "escalate")), ("priority", Enum("low", "warning", "high", "critical")),
            ("rationale", Str(1500)), ("notification_title", Str(180)), ("notification_message", Str(1500)),
            ("suggested_channels", Array(Enum("lab-inbox", "slack", "telegram", "email"), 4)), ("human_review_required", Bool())),
        "judge" => Object(("verdict", Enum("agree", "needs_review", "disagree")), ("rationale", Str(1500)), ("concerns", Array(Str(14000), 6)), ("evidence_ids", Array(Str(), 30))),
        _ => throw new LabException(422, "Invalid agent role")
        };
    }
    public static JsonObject WorkerSchema(string action)
    {
        var fields = new List<(string, JsonObject)> { ("worker_id", Str(80, 1, IdentityPattern)) };
        if (action is not ("claim" or "catalog")) fields.Add(("lease", Str(80, 20)));
        if (action == "result") { fields.Add(("stage", Enum(Stages))); fields.Add(("report", new() { ["type"] = "object" })); }
        if (action == "failed") fields.Add(("reason", Enum(Reasons)));
        if (action is "result" or "failed")
        {
            fields.Add(("usage", Default(new() { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "integer", ["strictInteger"] = true } }, new { })));
            fields.Add(("provider_session_id", Nullable(Str(100, 1, IdentityPattern))));
        }
        if (action == "catalog") fields.Add(("models", Array(Object(("id", Str(200, 1, ModelPattern)), ("name", Str(200)), ("family", Str(80)),
            ("efforts", Array(Enum(Efforts), 9)), ("default_effort", Enum(Efforts)), ("is_default", Bool())), 100)));
        return Object(fields.ToArray());
    }

    public static JsonObject Settings(JsonObject input)
    {
        var value = input.Copy();
        if (!value.ContainsKey("agent_profiles"))
        {
            var profile = J.Obj(new { model_id = value.S("model_id") });
            value["agent_profiles"] = new JsonObject { ["investigator"] = profile, ["manager"] = profile.DeepClone() };
            if (value.S("execution_mode") == "codex_local") value["provider"] = "codex";
        }
        return Validate(value, SettingsSchema).AsObject();
    }

    public static JsonNode Validate(JsonNode? node, JsonObject schema, string path = "body")
    {
        void Fail() => throw new LabException(422, $"Invalid field: {path}");
        if (node == null) { if (schema.B("nullable")) return null!; Fail(); }
        var value = node!.DeepClone();
        switch (schema.S("type"))
        {
            case "object":
                if (value is not JsonObject) Fail();
                var obj = value.AsObject(); var properties = schema.O("properties");
                foreach (var required in schema.A("required")) if (!obj.ContainsKey(required!.GetValue<string>())) Fail();
                foreach (var key in obj.Select(p => p.Key).ToArray())
                {
                    if (properties[key] is JsonObject rule) obj[key] = Validate(obj[key], rule, path + "." + key);
                    else if (schema["additionalProperties"] is JsonObject additional) obj[key] = Validate(obj[key], additional, path + "." + key);
                    else if (schema["additionalProperties"] is JsonValue b && b.TryGetValue<bool>(out var allow) && !allow) Fail();
                }
                foreach (var (key, rule) in properties)
                    if (!obj.ContainsKey(key) && rule!.AsObject().ContainsKey("default")) obj[key] = Validate(rule["default"], rule.AsObject(), path + "." + key);
                break;
            case "array":
                if (value is not JsonArray a || a.Count < schema.N("minItems") || a.Count > schema.N("maxItems", int.MaxValue)) Fail();
                var array = value.AsArray();
                for (int i = 0; i < array.Count; i++) array[i] = Validate(array[i], schema.O("items"), $"{path}[{i}]");
                break;
            case "string":
                if (value is not JsonValue s || !s.TryGetValue<string>(out _)) Fail();
                var str = value.GetValue<string>();
                if (str.Length < schema.N("minLength") || str.Length > schema.N("maxLength", int.MaxValue)) Fail();
                if (schema["enum"] is JsonArray values && !values.Any(v => v!.GetValue<string>() == str)) Fail();
                if (schema["pattern"] != null && !Regex.IsMatch(str, schema.S("pattern"), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) Fail();
                break;
            case "integer":
            case "number":
                if (value is not JsonValue number || !number.TryGetValue<double>(out var n) || !double.IsFinite(n))
                {
                    // JsonValue<int/long> created in code also represents a JSON number.
                    if (!double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n) || !double.IsFinite(n)) Fail();
                }
                if (schema.S("type") == "integer" && (n != Math.Truncate(n) || n < long.MinValue || n > long.MaxValue)) Fail();
                if (schema.B("strictInteger") && !long.TryParse(value.ToJsonString(), out _)) Fail();
                if (n < schema.N("minimum", -double.MaxValue) || n > schema.N("maximum", double.MaxValue)) Fail();
                break;
            case "boolean":
                if (value is not JsonValue boolean || !boolean.TryGetValue<bool>(out _)) Fail();
                break;
        }
        return value;
    }
}
