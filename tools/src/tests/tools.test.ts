import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createServer } from 'node:net';
import { createServer as httpServer } from 'node:http';
import { join } from 'node:path';
import { ROOT, parse, only, execute, type Obj, type Json, sleep } from '../common.js';
import { available, ports, stopOwned } from '../local.js';
import { api, smoke, smokeAi, verifySamples, verifyNoAi } from '../smoke.js';

test('CLI parser handles scenarios and rejects duplicate or incomplete options', () => {
  assert.deepEqual(parse(['smoke', '--scenarios', 'normal', 'retry', '--manual-ai']).values, { scenarios: ['normal', 'retry'], 'manual-ai': true });
  for (const args of [['local', '--database'], ['smoke', '--scenarios'], ['local', '--with-codex', '--with-codex'], ['local', 'oops']]) assert.throws(() => parse(args));
  assert.throws(() => only({ typo: true }, ['url']));
});
test('CLI help and write/AI gates run without any credentials or Python', async () => {
  const cli = join(ROOT, 'tools/dist/cli.js');
  const help = await execute(process.execPath, [cli, '--help']); assert.equal(help.code, 0); assert.match(help.stdout, /no Python required/);
  const aws = await execute(process.execPath, [cli, 'aws', 'create', '--config', '/not/a/config']); assert.notEqual(aws.code, 0); assert.match(aws.stderr, /requires --apply/);
  const ai = await execute(process.execPath, [cli, 'smoke-ai']); assert.notEqual(ai.code, 0); assert.match(ai.stderr, /--confirm-ai/);
});
test('local launcher validates ports', () => {
  assert.deepEqual(ports({}), [8010, 8081]);
  const invalid: Record<string, string>[] = [{ 'monitor-port': '8081' }, { 'target-port': '1' }, { 'target-port': 'nan' }, { 'target-port': '1234.5' }];
  for (const values of invalid) assert.throws(() => ports(values));
});
test('occupied port is refused without stopping its owner', async () => {
  const server = createServer(); server.listen(0, '127.0.0.1'); await once(server, 'listening');
  try {
    const address = server.address(); assert.ok(address && typeof address !== 'string');
    await assert.rejects(available(address.port), /already in use/); assert.equal(server.listening, true);
  } finally { await new Promise<void>(done => server.close(() => done())); }
});
test('owned child stops gracefully; unresponsive owned child is bounded', async () => {
  for (const ignores of [false, true]) {
    const code = `process.on('SIGTERM',()=>{${ignores ? '' : 'process.exit(0);'}}); console.log('ready'); setInterval(()=>{},1000);`;
    const child = spawn(process.execPath, ['-e', code], { detached: true, stdio: ['ignore', 'pipe', 'ignore'] });
    try {
      await once(child.stdout!, 'data'); await stopOwned(child, 100);
      if (ignores) assert.equal(child.signalCode, 'SIGKILL'); else assert.equal(child.exitCode, 0);
      await stopOwned(child, 100); // Idempotent; no unrelated pid is touched.
    } finally { await stopOwned(child, 100); }
  }
});
test('cancellation interrupts delays promptly', async () => {
  const controller = new AbortController(); const waiting = sleep(10_000, controller.signal); controller.abort();
  await assert.rejects(waiting, /cancelled/);
});
test('sample verification joins real response IDs and rejects fabricated/mismatched data', () => {
  const result: Obj = { sent: 2, responses: 1, transport_errors: 1, cancelled_requests: 0,
    response_samples: [{ request_id: 'r1', method: 'GET', path: '/items', status: 200, client_duration_ms: 3 }] };
  const events: Obj[] = [{ kind: 'request', request_id: 'r1', method: 'GET', path: '/items', status: 200, duration_ms: 2 }];
  verifySamples({ result }, events);
  assert.throws(() => verifySamples({ result }, []));
  assert.throws(() => verifySamples({ result }, [{ ...events[0], status: 500 }]));
  assert.throws(() => verifySamples({ result: { ...result, sent: 10 } }, events));
  assert.throws(() => verifySamples({ result: { ...result, response_samples: [] } }, events));
});
test('no-AI ledger verification rejects additional calls or tokens', () => {
  verifyNoAi({ calls: 2, tokens: 100 }, { calls: 2, tokens: 100 });
  assert.throws(() => verifyNoAi({ calls: 2, tokens: 100 }, { calls: 3, tokens: 100 }));
  assert.throws(() => verifyNoAi({ calls: 2, tokens: 100 }, { calls: 2, tokens: 101 }));
});
test('smoke refuses enabled automatic AI and active jobs before generating traffic', async () => {
  for (const mode of ['auto', 'enabled', 'busy']) {
    const calls: string[] = [];
    const call = async (path: string): Promise<Json> => {
      calls.push(path);
      if (path === '/settings/ai') return { enabled: mode !== 'busy', auto_analyze: mode === 'auto' };
      if (path === '/agents') return { running: 1, queued: 0 };
      throw new Error('Unexpected request');
    };
    await assert.rejects(smoke(mode === 'auto' ? { 'manual-ai': true } : {}, new AbortController().signal, call));
    assert.ok(!calls.includes('/runs'));
  }
});
test('AI smoke requires explicit cost consent before contacting the monitor', async () => {
  let contacted = false;
  await assert.rejects(smokeAi({}, new AbortController().signal, async () => { contacted = true; return {}; }), /--confirm-ai/);
  assert.equal(contacted, false);
});
test('AI smoke verifies investigator/manager and optional judge using offline responses, then restores settings', async t => {
  t.mock.method(console, 'log', () => {});
  for (const judge of [false, true]) for (const followup of [false, true]) {
    const previous = { enabled: false, auto_analyze: false }; const saved: Obj[] = []; const expected = (judge ? 3 : 2) + (followup ? 1 : 0);
    let settingsApplied = false; let scenarioStarted = false;
    const profile = { model_family: 'fixture', model_id: 'fixture-model', reasoning_effort: 'low' };
    const call = async (path: string, payload?: Obj, method?: string): Promise<Json> => {
      if (path === '/settings/ai' && method === 'PUT') { saved.push(payload!); settingsApplied = true; return payload!; }
      if (path === '/settings/ai') return previous;
      if (path === '/overview') return { health: 'healthy', incidents: scenarioStarted ? [{ id: 'incident', rule: 'server-errors' }] : [] };
      if (path === '/runs' && payload) { scenarioStarted = true; return { id: 'run' }; }
      if (path === '/runs') return scenarioStarted ? [{ id: 'run', status: 'completed', result: { sent: 10, http_errors: 6 } }] : [];
      if (path === '/ai/catalog') return { models: [{ id: 'fixture-model', family: 'fixture', is_default: true, efforts: ['low'] }] };
      if (path === '/incidents/incident/analyze') return { id: 'job' };
      if (path === '/agents') return settingsApplied ? { calls: expected, jobs: [{ id: 'job', status: 'completed', stage: 'done', timeline: [], error: null,
        investigation: { summary: 'Offline fixture' }, decision: { action: 'escalate' }, incident_id: 'incident', judge: judge ? { verdict: 'agree' } : null, sessions: followup ? [{role: 'investigator_followup'}] : [],
        policy: { effective_action: 'escalate', judge_verdict: judge ? 'agree' : null, channels: [{ status: 'delivered' }, { status: 'not-configured' }] }, usage: {} }] }
        : { calls: 0, worker_online: true, running: 0, queued: 0 };
      if (path === '/ai/usage?incident_id=incident') return { totals: { sessions: expected, usage_reported: expected, reported_tokens: expected * 12 },
        sessions: Array.from({ length: expected }, () => ({ usage: { input_tokens: 10, output_tokens: 2 }, provider_session_id: 'fixture', duration_seconds: 1, config: profile })) };
      if (path === '/notices') return [{ body: { job_id: 'job' }, channel: 'lab-inbox' }];
      throw new Error(`Unexpected fixture request ${path}`);
    };
    await smokeAi({ 'confirm-ai': true, judge }, new AbortController().signal, call);
    assert.equal(saved[0].enabled, true); assert.equal(saved[0].auto_analyze, false); assert.equal(saved[0].judge_enabled, judge);
    assert.deepEqual(saved.at(-1), previous);
  }
});
test('AI settings restoration is attempted even if stopping the experiment fails', async t => {
  t.mock.method(console, 'log', () => {});
  const previous = { enabled: false, auto_analyze: false }; const saved: Obj[] = []; let started = false;
  const call = async (path: string, payload?: Obj, method?: string, cleanup?: boolean): Promise<Json> => {
    if (path === '/settings/ai' && method === 'PUT') { saved.push(payload!); if (saved.length > 1) assert.equal(cleanup, true); return payload!; }
    if (path === '/settings/ai') return previous;
    if (path === '/agents') return { calls: 0, worker_online: true, running: 0, queued: 0 };
    if (path === '/runs' && payload) { started = true; return { id: 'run' }; }
    if (path === '/runs') return started ? [{ id: 'run', status: 'running' }] : [];
    if (path === '/overview') return { health: 'healthy', incidents: started ? [{ id: 'incident', rule: 'server-errors' }] : [] };
    if (path === '/ai/catalog') return { models: [{ id: 'fixture', family: 'fixture', is_default: true, efforts: ['low'] }] };
    if (path === '/incidents/incident/analyze') throw new Error('offline fixture queue failure');
    if (path === '/runs/stop') { assert.equal(cleanup, true); throw new Error('offline fixture stop failure'); }
    throw new Error('Unexpected fixture request');
  };
  await assert.rejects(smokeAi({ 'confirm-ai': true }, new AbortController().signal, call));
  assert.deepEqual(saved.at(-1), previous);
});
test('HTTP client rejects redirects and embedded credentials, cleanup survives cancellation', async () => {
  assert.throws(() => api('http://user:secret@localhost', new AbortController().signal));
  assert.throws(() => api('file:///tmp/test', new AbortController().signal));
  const server = httpServer((req, res) => {
    if (req.url === '/api/redirect') { res.writeHead(302, { Location: 'http://127.0.0.1:1' }); res.end(); }
    else { res.setHeader('Content-Type', 'application/json'); res.end('{"ok":true}'); }
  });
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  try {
    const address = server.address(); assert.ok(address && typeof address !== 'string');
    const controller = new AbortController(); const call = api(`http://127.0.0.1:${address.port}`, controller.signal);
    assert.deepEqual(await call('/ok'), { ok: true }); await assert.rejects(call('/redirect'));
    controller.abort(); await assert.rejects(call('/ok')); assert.deepEqual(await call('/ok', {}, undefined, true), { ok: true });
  } finally { server.closeAllConnections(); await new Promise<void>(done => server.close(() => done())); }
});
