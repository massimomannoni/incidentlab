using IncidentLab.Worker;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
using var signal = OperatingSystem.IsWindows() ? null : System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
try { await new WorkerHost().Run(shutdown.Token); }
catch (OperationCanceledException) { Console.WriteLine("Worker stopped; unfinished jobs expire without automatic retry."); }
catch (Exception ex) { Console.Error.WriteLine(ex is ArgumentException ? ex.Message : "Worker could not start. Check the Codex CLI and monitor configuration."); Environment.ExitCode = 1; }
