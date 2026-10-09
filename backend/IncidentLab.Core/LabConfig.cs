using System.Globalization;

namespace IncidentLab;

public sealed record LabConfig
{
    public string DatabaseUrl { get; init; } = "sqlite:///./incidentlab.db";
    public string TelemetryBackend { get; init; } = "opensearch";
    public string OpenSearchUrl { get; init; } = "http://opensearch:9200";
    public string TargetUrl { get; init; } = "http://target:8080";
    public string IngestKey { get; init; } = "";
    public string ControlKey { get; init; } = "";
    public string WorkerKey { get; init; } = "";
    public double Interval { get; init; } = 2;
    public int Window { get; init; } = 20;
    public int StaleSeconds { get; init; } = 12;
    public int RecoverySeconds { get; init; } = 8;
    public string FrontendDir { get; init; } = "frontend/dist";
    public bool BedrockEnabled { get; init; }
    public string BedrockRegion { get; init; } = "eu-south-1";
    public decimal BedrockMonthlyCapUsd { get; init; } = 5;
    public decimal BedrockDailyCapUsd { get; init; } = .50m;

    public static LabConfig FromEnvironment()
    {
        string Get(string key, string fallback = "") => Environment.GetEnvironmentVariable(key) ?? fallback;
        return new()
        {
            DatabaseUrl = Get("DATABASE_URL", "sqlite:///./incidentlab.db"), TelemetryBackend = Get("TELEMETRY_BACKEND", "opensearch"),
            OpenSearchUrl = Get("OPENSEARCH_URL", "http://opensearch:9200"), TargetUrl = Get("TARGET_URL", "http://target:8080"),
            IngestKey = Get("LAB_INGEST_KEY"), ControlKey = Get("LAB_CONTROL_KEY"), WorkerKey = Get("LAB_WORKER_KEY"),
            Interval = double.Parse(Get("ANALYZER_INTERVAL_SECONDS", "2"), CultureInfo.InvariantCulture),
            Window = int.Parse(Get("ANALYZER_WINDOW_SECONDS", "20")), StaleSeconds = int.Parse(Get("SOURCE_STALE_SECONDS", "12")),
            RecoverySeconds = int.Parse(Get("RECOVERY_SECONDS", "8")), FrontendDir = Get("FRONTEND_DIR", "frontend/dist"),
            BedrockEnabled = Get("LAB_BEDROCK_ENABLED") == "true", BedrockRegion = Get("LAB_BEDROCK_REGION", "eu-south-1"),
            BedrockMonthlyCapUsd = decimal.Parse(Get("LAB_BEDROCK_MONTHLY_CAP_USD", "5"), CultureInfo.InvariantCulture),
            BedrockDailyCapUsd = decimal.Parse(Get("LAB_BEDROCK_DAILY_CAP_USD", "0.50"), CultureInfo.InvariantCulture)
        };
    }

    public void Validate()
    {
        if (BedrockRegion != "eu-south-1" || BedrockMonthlyCapUsd is < 0 or > 100 || BedrockDailyCapUsd is < 0 or > .50m)
            throw new ArgumentException("Bedrock demo allows the Milan endpoint, at most USD 100/month and USD 0.50/day");
        if (IngestKey.Length < 16 || ControlKey.Length < 16) throw new ArgumentException("LAB_INGEST_KEY and LAB_CONTROL_KEY must each contain at least 16 characters");
        if (TelemetryBackend is not ("sqlite" or "opensearch")) throw new ArgumentException("TELEMETRY_BACKEND must be opensearch or sqlite (development only)");
        if (!double.IsFinite(Interval) || Interval is < .5 or > 30 || Window is < 10 or > 120) throw new ArgumentException("Analyzer interval must be 0.5–30 seconds and window 10–120 seconds");
        if (StaleSeconds is < 6 or > 120 || RecoverySeconds is < 2 or > 120) throw new ArgumentException("Source stale/recovery intervals exceed laboratory bounds");
        foreach (var url in new[] { TargetUrl, OpenSearchUrl })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                throw new ArgumentException("Target and archive endpoints must be HTTP(S) URLs without embedded credentials");
    }
}
