using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace IncidentLab.Worker;

public interface ICliProcess : IAsyncDisposable
{
    TextWriter Input { get; }
    TextReader Output { get; }
    Task<int> Wait(CancellationToken ct);
    Task Stop();
}
public interface ICliLauncher { ICliProcess Start(IReadOnlyList<string> arguments, string directory); }

public sealed class CliLauncher(string executable) : ICliLauncher
{
    public static string FindExecutable()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(dir, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(path)) return path;
        }
        throw new WorkerFailure("codex_failed");
    }
    public static Dictionary<string, string> ChildEnvironment()
    {
        var result = new Dictionary<string, string>();
        foreach (var key in new[] { "HOME", "PATH", "USER", "LOGNAME", "TMPDIR", "LANG", "LC_ALL", "CODEX_HOME", "SystemRoot" })
            if (Environment.GetEnvironmentVariable(key) is { } value) result[key] = value;
        return result;
    }
    public ICliProcess Start(IReadOnlyList<string> arguments, string directory)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment.Clear(); foreach (var (key, value) in ChildEnvironment()) info.Environment[key] = value;
        return new Child(Process.Start(info) ?? throw new WorkerFailure("codex_failed"));
    }
    sealed class Child(Process process) : ICliProcess
    {
        readonly Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        public TextWriter Input => process.StandardInput;
        public TextReader Output => process.StandardOutput;
        public async Task<int> Wait(CancellationToken ct) { await process.WaitForExitAsync(ct); return process.ExitCode; }
        public async Task Stop()
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
            }
        }
        public async ValueTask DisposeAsync() { await Stop(); await stderr; process.Dispose(); }
    }
}

public sealed class WorkerFailure(string reason) : Exception(reason);

public static class BoundedOutput
{
    public static async IAsyncEnumerable<string> Lines(TextReader reader, int maxBytes, [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new char[4096]; var line = new StringBuilder(); int total = 0, read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            total += Encoding.UTF8.GetByteCount(chunk.AsSpan(0, read));
            if (total > maxBytes) throw new WorkerFailure("output_limit");
            for (int i = 0; i < read; i++)
                if (chunk[i] == '\n') { yield return line.ToString(); line.Clear(); }
                else line.Append(chunk[i]);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}
