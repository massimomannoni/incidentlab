using System.Text.Json.Nodes;

namespace IncidentLab.Tests;

public sealed class DetectionTests
{
    static List<JsonObject> Requests(int count, int status = 200) => Enumerable.Range(0, count).Select(i => LabFixture.Event(i.ToString(), ts: 100, status: status)).ToList();
    [Fact] public void BaselineAndOldOrFutureEventsDoNotAlert()
    {
        var events = Requests(20); Assert.Empty(Detection.Evaluate(events, 100));
        foreach (var e in events) { e["status"] = 500; e["ts"] = 50; }
        Assert.Empty(Detection.Evaluate(events, 100)); foreach (var e in events) e["ts"] = 101;
        Assert.Empty(Detection.Evaluate(events, 100));
    }
    [Theory][InlineData(11, false)][InlineData(12, true)] public void RepetitionHasBoundary(int count, bool expected)
    {
        var events = Requests(count); foreach (var e in events) { e["command_signature"] = "same"; e["client_id"] = "client"; }
        Assert.Equal(expected, Detection.Evaluate(events, 100).Any(f => f.Rule == "repeat-command"));
        foreach (var e in events) e["client_id"] = e.S("id"); Assert.Empty(Detection.Evaluate(events, 100));
    }
    [Theory][InlineData(11, 11, false)][InlineData(12, 7, false)][InlineData(12, 8, true)] public void ProbeRequiresCountAndDistinctPaths(int count, int paths, bool expected)
    {
        var events = Requests(count, 404); for (int i = 0; i < count; i++) events[i]["path"] = "/missing/" + i % paths;
        Assert.Equal(expected, Detection.Evaluate(events, 100).Any(f => f.Rule == "http-probing"));
    }
    [Theory][InlineData(4, 10, false)][InlineData(5, 25, true)][InlineData(5, 26, false)] public void ErrorRuleNeedsMinimumAndRatio(int errors, int count, bool expected)
    {
        var events = Requests(count); for (int i = 0; i < errors; i++) events[i]["status"] = 500;
        Assert.Equal(expected, Detection.Evaluate(events, 100).Any(f => f.Rule == "server-errors"));
    }
    [Fact] public void HealthRequestsAreExcluded()
    {
        var events = Requests(12, 500); foreach (var e in events) e["path"] = "/health"; Assert.Empty(Detection.Evaluate(events, 100));
    }
    [Theory][InlineData(7, false)][InlineData(8, true)] public void SlowThreshold(int count, bool expected)
    {
        var events = Requests(count); foreach (var e in events) e["duration_ms"] = 500;
        Assert.Equal(expected, Detection.Evaluate(events, 100).Any(f => f.Rule == "slow-responses"));
    }
    [Theory][InlineData("cpu_percent", 60, "process-cpu")][InlineData("memory_mb", 160, "process-memory")]
    public void ResourcesRequireThreeConsecutiveSamples(string field, int threshold, string rule)
    {
        var events = Enumerable.Range(0, 3).Select(i => LabFixture.Event(i.ToString(), "metric", 100 - i)).ToList();
        foreach (var e in events) e[field] = threshold;
        Assert.Contains(Detection.Evaluate(events, 100), f => f.Rule == rule);
        events[0][field] = null; Assert.Empty(Detection.Evaluate(events, 100));
    }
    [Fact] public void ServicesAndInstancesRemainSeparate()
    {
        var events = Requests(6, 500); for (int i = 0; i < 6; i++) events[i]["service"] = i % 2 == 0 ? "a" : "b";
        Assert.Empty(Detection.Evaluate(events, 100));
        foreach (var e in events) { e["kind"] = "metric"; e["memory_mb"] = 200; e["instance"] = e.S("id"); }
        Assert.Empty(Detection.Evaluate(events, 100));
    }
    [Fact] public void HistogramDistinguishesMissingFromObservedIdle()
    {
        var histogram = Detection.Histogram([LabFixture.Event("metric", "metric", 99)], 100);
        Assert.Null(histogram[0]!["requests"]); Assert.Equal(0, histogram[^1].N("requests")); Assert.True(histogram[^1].B("observed"));
    }
}
