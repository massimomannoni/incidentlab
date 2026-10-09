using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentLab.Worker;

namespace IncidentLab.Tests;

public sealed class WorkerTests
{
    sealed class FakeProcess(string output, bool block = false) : ICliProcess
    {
        public StringWriter Written { get; } = new();
        public TextWriter Input => Written;
        public TextReader Output { get; } = block ? new BlockingReader() : new StringReader(output);
        public bool Stopped { get; private set; }
        public Task<int> Wait(CancellationToken ct) => Task.FromResult(0);
        public Task Stop() { Stopped = true; return Task.CompletedTask; }
        public async ValueTask DisposeAsync() { await Stop(); }
    }
    sealed class BlockingReader : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }
    sealed class FakeLauncher(FakeProcess process) : ICliLauncher
    {
        public IReadOnlyList<string>? Arguments { get; private set; }
        public ICliProcess Start(IReadOnlyList<string> arguments, string directory) { Arguments = arguments; return process; }
    }
    static JsonObject Job(double timeout = 5) => J.Obj(new { packet = new { }, settings = new { model_id = "", timeout_seconds = timeout } });
    static string Lines(params object[] rows) => string.Join('\n', rows.Select(r => JsonSerializer.Serialize(r))) + "\n";
    static Task Noop() => Task.CompletedTask;

    [Fact] public async Task WorkerParsesReportUsageAndNeverReturnsReasoning()
    {
        var output = Lines(new { type = "thread.started", thread_id = "test-provider-session" },
            new { type = "item.completed", item = new { type = "reasoning", text = "private reasoning not report" } },
            new { type = "item.completed", item = new { type = "agent_message", text = LabFixture.Investigation.ToJsonString() } },
            new { type = "turn.completed", usage = new { input_tokens = 40, output_tokens = 10 } });
        var process = new FakeProcess(output); var metadata = new JsonObject();
        var result = await new CodexInvocation(new FakeLauncher(process)).Invoke(Job(), "investigator", Noop, Noop, metadata);
        Assert.True(JsonNode.DeepEquals(LabFixture.Investigation, result.report)); Assert.Equal(40, result.usage.N("input_tokens"));
        Assert.Equal("test-provider-session", metadata.S("provider_session_id")); Assert.DoesNotContain("private reasoning", metadata.ToJsonString()); Assert.True(process.Stopped);
    }
    [Theory]
    [InlineData("{\"type\":\"turn.failed\"}", "codex_failed")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\"}}", "unexpected_tool")]
    [InlineData("{\"type\":\"item.started\",\"item\":{\"type\":\"mcp_tool_call\"}}", "unexpected_tool")]
    [InlineData("{\"type\":\"turn.completed\"}", "codex_failed")]
    public async Task FailuresAndToolUseAreRejected(string line, string reason)
    {
        var process = new FakeProcess(line + "\n"); var worker = new CodexInvocation(new FakeLauncher(process));
        var error = await Assert.ThrowsAsync<WorkerFailure>(() => worker.Invoke(Job(), "investigator", Noop, Noop, new())); Assert.Equal(reason, error.Message); Assert.True(process.Stopped);
    }
    [Fact] public async Task InvalidReportIsRejectedAfterUsageCaptured()
    {
        var process = new FakeProcess(Lines(new { type = "item.completed", item = new { type = "agent_message", text = "{}" } }, new { type = "turn.completed", usage = new { input_tokens = 12 } }));
        var metadata = new JsonObject();
        var ex = await Assert.ThrowsAsync<WorkerFailure>(() => new CodexInvocation(new FakeLauncher(process)).Invoke(Job(), "investigator", Noop, Noop, metadata));
        Assert.Equal("invalid_output", ex.Message); Assert.Equal(12, metadata.O("usage").N("input_tokens"));
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task TimeoutAndCancellationStopChild(bool cancel)
    {
        var process = new FakeProcess("", true); using var ct = new CancellationTokenSource(); var worker = new CodexInvocation(new FakeLauncher(process));
        if (cancel) ct.CancelAfter(30);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Invoke(Job(), "investigator", Noop, Noop, new(), ct.Token));
        else Assert.Equal("timeout", (await Assert.ThrowsAsync<WorkerFailure>(() => worker.Invoke(Job(.03), "investigator", Noop, Noop, new()))).Message);
        Assert.True(process.Stopped);
    }
    [Fact] public async Task HugeSingleOutputLineIsBounded()
    {
        var process = new FakeProcess(new string('x', 256001));
        var error = await Assert.ThrowsAsync<WorkerFailure>(() => new CodexInvocation(new FakeLauncher(process)).Invoke(Job(), "investigator", Noop, Noop, new())); Assert.Equal("output_limit", error.Message);
    }
    [Fact] public void HugePromptDoesNotStartInvocation()
    {
        var job = Job(); job["packet"] = J.Obj(new { summary = new string('x', 25000) });
        Assert.Equal("output_limit", Assert.Throws<WorkerFailure>(() => CodexInvocation.Prompt(job, "investigator")).Message);
    }
    [Fact] public void ArgumentsKeepReadonlyNoToolsAndChosenModel()
    {
        var args = CodexInvocation.Arguments("/tmp/lab", "/tmp/schema", "test-model", "low");
        Assert.Contains("--ignore-user-config", args); Assert.Equal("read-only", args[args.IndexOf("--sandbox") + 1]); Assert.Contains("web_search=\"disabled\"", args);
        foreach (var feature in new[] { "shell_tool", "apps", "plugins", "multi_agent", "computer_use", "browser_use" }) Assert.Equal("--disable", args[args.IndexOf(feature) - 1]);
        Assert.Equal("test-model", args[args.IndexOf("--model") + 1]); Assert.Contains("model_reasoning_effort=\"low\"", args);
        Assert.DoesNotContain("--model", CodexInvocation.Arguments("/tmp/lab", "/tmp/schema", ""));
        Assert.All(CliLauncher.ChildEnvironment().Keys, key => Assert.Contains(key, new[] { "HOME", "PATH", "USER", "LOGNAME", "TMPDIR", "LANG", "LC_ALL", "CODEX_HOME", "SystemRoot" }));
    }
    [Theory][InlineData("http://remote.example")][InlineData("https://user:secret@remote.example")][InlineData("https://remote.example/x")][InlineData("http://localhost:8010?secret=value")]
    public void WorkerRefusesUnsafeMonitorUrls(string url) => Assert.Throws<ArgumentException>(() => WorkerHost.ValidateUrl(url));
    [Theory][InlineData("http://localhost:8010")][InlineData("http://127.0.0.1:8010")][InlineData("https://monitor.example")]
    public void WorkerAllowsLoopbackOrHttps(string url) => Assert.NotNull(WorkerHost.ValidateUrl(url));
    [Fact] public async Task CatalogOnlyListsModelsAndHandlesPagination()
    {
        var model = new { model = "gpt-6-luna", displayName = "Test catalog entry", hidden = false, supportedReasoningEfforts = new[] { new { reasoningEffort = "low" } }, defaultReasoningEffort = "low", isDefault = true };
        var process = new FakeProcess(Lines(new { id = 1, result = new { } }, new { id = 2, result = new { data = new[] { model }, nextCursor = "page2" } },
            new { id = 3, result = new { data = Array.Empty<object>(), nextCursor = (string?)null } }));
        var launcher = new FakeLauncher(process); var models = await new ModelCatalog(launcher).Discover();
        Assert.Equal("GPT-6", models[0].S("family")); Assert.Equal("low", models[0].A("efforts")[0]!.GetValue<string>());
        var requests = process.Written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonNode.Parse(s)!).ToArray();
        Assert.Equal(new[] { "initialize", "initialized", "model/list", "model/list" }, requests.Select(r => r.S("method")));
        Assert.Equal("page2", requests[^1].O("params").S("cursor")); Assert.True(process.Stopped); Assert.Contains("app-server", launcher.Arguments!);
    }
    [Theory][InlineData("gpt-6.1-sol", "GPT-6.1")][InlineData("other", "Other")]
    public void FamilyIsNameGroupingOnly(string model, string expected) => Assert.Equal(expected, ModelCatalog.Family(model));
}
