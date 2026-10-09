import { spawn, type ChildProcess } from 'node:child_process';
import { access, mkdir } from 'node:fs/promises';
import { createServer } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { ROOT, LabError, only, type Options, requireThat, sleep } from './common.js';

export function ports(options: Options['values']): [number, number] {
  const monitor = Number(options['monitor-port'] ?? 8010); const target = Number(options['target-port'] ?? 8081);
  requireThat(monitor !== target && [monitor, target].every(p => Number.isInteger(p) && p >= 1024 && p <= 65535), 'Choose two distinct ports between 1024 and 65535.');
  return [monitor, target];
}
export async function available(port: number): Promise<void> {
  await new Promise<void>((done, reject) => {
    const server = createServer();
    server.once('error', () => reject(new LabError(`Port ${port} is already in use. No existing process stopped.`)));
    server.listen(port, '127.0.0.1', () => server.close(error => error ? reject(error) : done()));
  });
}
export async function stopOwned(child: ChildProcess, timeout = 20_000): Promise<void> {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null) return;
  const ended = new Promise<void>(done => child.once('exit', () => done()));
  const kill = (signal: NodeJS.Signals) => {
    try { process.kill(-child.pid!, signal); }
    catch (e) { if ((e as NodeJS.ErrnoException).code !== 'ESRCH') throw e; }
  };
  kill('SIGTERM');
  let timer: NodeJS.Timeout | undefined;
  try {
    await Promise.race([ended, new Promise<void>(done => { timer = setTimeout(() => { kill('SIGKILL'); done(); }, timeout); })]);
    await ended;
  } finally { clearTimeout(timer); }
}
export async function local(options: Options['values'], signal: AbortSignal): Promise<void> {
  only(options, ['with-codex', 'database', 'monitor-port', 'target-port']);
  requireThat(process.platform !== 'win32', 'The local launcher supports macOS/Linux. Use WSL2 on Windows.');
  const [monitorPort, targetPort] = ports(options);
  const target = join(ROOT, 'target/bin/Debug/net8.0/Target.dll');
  const monitor = join(ROOT, 'backend/IncidentLab.Api/bin/Debug/net8.0/IncidentLab.Api.dll');
  const worker = join(ROOT, 'backend/IncidentLab.Worker/bin/Debug/net8.0/IncidentLab.Worker.dll');
  try { for (const path of [target, monitor, join(ROOT, 'frontend/dist/index.html'), ...(options['with-codex'] ? [worker] : [])]) await access(path); }
  catch { throw new LabError('Build IncidentLab.sln (Debug) and the frontend first; see docs/ai-worker.md.'); }
  await available(monitorPort); await available(targetPort);
  const database = resolve(String(options.database ?? join(ROOT, '.local/lab.db')));
  await mkdir(dirname(database), { recursive: true });
  const env = { ...process.env, DATABASE_URL: 'sqlite:///' + database, TELEMETRY_BACKEND: 'sqlite',
    TARGET_URL: `http://127.0.0.1:${targetPort}`, MONITOR_URL: `http://127.0.0.1:${monitorPort}`, FRONTEND_DIR: join(ROOT, 'frontend/dist'),
    LAB_INGEST_KEY: randomBytes(32).toString('hex'), LAB_CONTROL_KEY: randomBytes(32).toString('hex'), LAB_WORKER_KEY: randomBytes(32).toString('hex') };
  const children: ChildProcess[] = [];
  let failed = false;
  const start = (dll: string, extra: NodeJS.ProcessEnv, quiet = false) => {
    const child = spawn('dotnet', [dll], { cwd: ROOT, env: { ...env, ...extra }, detached: true, stdio: quiet ? 'ignore' : ['ignore', 'inherit', 'inherit'] });
    child.once('error', () => { failed = true; }); children.push(child);
  };
  const running = () => !failed && children.every(p => p.exitCode === null && p.signalCode === null);
  try {
    start(monitor, { ASPNETCORE_URLS: `http://127.0.0.1:${monitorPort}`, Logging__LogLevel__Default: 'Warning' });
    start(target, { ASPNETCORE_URLS: `http://127.0.0.1:${targetPort}`, DOTNET_GCHeapHardLimit: '0x4000000' }, true);
    const deadline = performance.now() + 25_000;
    while (true) {
      requireThat(running(), 'A laboratory process failed to start.');
      try {
        for (const url of [`http://127.0.0.1:${monitorPort}/api/health`, `http://127.0.0.1:${targetPort}/health`]) {
          const result = await fetch(url, { redirect: 'error', signal: AbortSignal.any([signal, AbortSignal.timeout(1000)]) });
          await result.body?.cancel(); requireThat(result.ok, 'Waiting for startup.');
        }
        break;
      } catch { requireThat(performance.now() < deadline, 'Laboratory startup timed out.'); await sleep(500, signal); }
    }
    if (options['with-codex']) start(worker, {});
    console.log(`IncidentLab: http://127.0.0.1:${monitorPort} — C# services + real API + explicit SQLite development storage`);
    console.log('Existing AI settings retained. --with-codex starts the worker: enabled automatic AI may consume account usage.');
    console.log('Ctrl+C stops only owned processes; database and reports are preserved.');
    while (running()) await sleep(500, signal);
    throw new LabError('A laboratory process exited unexpectedly.');
  } finally { for (const child of children.reverse()) await stopOwned(child); }
}
