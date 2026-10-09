using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;

namespace IncidentLab;

public static class SimulationQuota
{
    public const int DailyLimit = 10;
    public const string MasterHeader = "X-IncidentLab-Master-Admission";
    public static string MasterKey(string controlKey) => Convert.ToHexString(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(controlKey), Encoding.UTF8.GetBytes("incidentlab:master-simulation-admission:v1"))).ToLowerInvariant();
    public static bool IsMaster(string supplied, string controlKey) => controlKey.Length >= 24 && supplied.Length == 64 && J.SecretEquals(supplied, MasterKey(controlKey));
    public static JsonObject Status(LabDb db, double now, bool master = false)
    {
        var start = Math.Floor(now / 86400) * 86400;
        // Interrupted, stopped and failed starts consume the allowance too.
        var used = db.Runs.Where(r => r.Started >= start && r.Started < start + 86400).Select(r => r.Config).ToList()
            .Count(c => c.B("master_quota_exempt") == master);
        return J.Obj(new { daily_limit = master ? (int?)null : DailyLimit, used, remaining = master ? (int?)null : Math.Max(0, DailyLimit - used),
            unlimited = master, resets_at = master ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds((long)start + 86400),
            scope = master ? "personal-master" : "non-master-laboratory", timezone = "UTC" });
    }
    public static void Check(LabDb db, double now)
    {
        if (Status(db, now).N("remaining") == 0)
            throw new LabException(429, "Non-master daily limit reached (10 simulations). Resets at 00:00 UTC.");
    }
}
