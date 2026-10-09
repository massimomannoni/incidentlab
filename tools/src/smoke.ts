import { type Obj, type Json, type Options, array, object, str, num, requireThat, only, sleep } from './common.js';

export type Call = (path: string, payload?: Obj, method?: string, cleanup?: boolean) => Promise<Json>;
export function api(url: string, signal: AbortSignal): Call {
  const base = new URL(url);
  requireThat(['http:', 'https:'].includes(base.protocol) && !base.username && !base.password && !base.search && !base.hash, 'Use an HTTP(S) monitor URL without embedded credentials or query parameters.');
  return async (path, payload, method, cleanup = false) => {
    const response = await fetch(base.href.replace(/\/$/, '') + '/api' + path, {
      method: method ?? (payload ? 'POST' : 'GET'), body: payload ? JSON.stringify(payload) : undefined,
      headers: { 'Content-Type': 'application/json' }, redirect: 'error',
      signal: cleanup ? AbortSignal.timeout(15_000) : AbortSignal.any([signal, AbortSignal.timeout(15_000)]),
    });
    requireThat(response.ok, `Monitor ${path.split('?')[0]} returned HTTP ${response.status}.`);
    return await response.json() as Json;
  };
}
const scenarios = ['normal', 'retry', 'probing', 'errors', 'latency', 'memory', 'telemetry_gap', 'cpu'];
const busy = (run: Obj) => ['running', 'settling'].includes(str(run.status));
const findRun = (rows: Json, id: string): Obj => {
  const run = array(rows).find(row => row.id === id); requireThat(run, 'Experiment is missing.'); return run;
};
export function verifySamples(run: Obj, events: Obj[]): void {
  const result = object(run.result);
  requireThat(num(result.sent) === num(result.responses) + num(result.transport_errors) + num(result.cancelled_requests), 'Request accounting does not reconcile.');
  const samples = array(result.response_samples);
  const observed = new Map(events.filter(e => e.kind === 'request').map(e => [e.request_id, e]));
  requireThat(samples.length, 'No real HTTP response samples captured.');
  for (const sample of samples) {
    const event = observed.get(sample.request_id);
    requireThat(event, 'Response has no matching server telemetry.');
    requireThat(['path', 'method', 'status'].every(k => event[k] === sample[k]), 'Response differs from server telemetry.');
    requireThat(num(sample.client_duration_ms) > 0 && num(event.duration_ms) >= 0, 'Invalid measured duration.');
  }
}
export function verifyNoAi(initial: Obj, current: Obj): void {
  requireThat(current.calls === initial.calls && current.tokens === initial.tokens, 'Unexpected additional AI calls or tokens.');
}
export async function smoke(options: Options['values'], signal: AbortSignal, request?: Call): Promise<void> {
  only(options, ['url', 'scenarios', 'manual-ai']);
  const call = request ?? api(String(options.url ?? 'http://localhost:8010'), signal);
  const settings = object(await call('/settings/ai'));
  requireThat(!settings.enabled || options['manual-ai'] === true && !settings.auto_analyze, 'Disable AI, or use --manual-ai with automatic mode off.');
  const agents = object(await call('/agents'));
  requireThat(!agents.queued && !agents.running, 'AI queue must be idle.');
  const initial = object(object(await call('/overview')).ai);
  const known = array(await call('/scenarios')).map(s => s.id);
  const selected = options.scenarios as string[] | undefined ?? scenarios;
  requireThat(selected.length && selected.every(s => known.includes(s)), 'Unknown scenario requested.');
  requireThat(!array(await call('/runs')).some(busy), 'Laboratory already busy.');
  const ready = performance.now() + 45_000;
  while (object(await call('/overview')).health !== 'healthy') { requireThat(performance.now() < ready, 'Telemetry did not become healthy.'); await sleep(1000, signal); }
  await sleep(10_000, signal); // Let any preceding outage recover.
  const failures: string[] = [];
  for (const scenario of selected) {
    const id = str(object(await call('/runs', { scenario, duration: 30, rps: 6, clients: 3, warmup: 5 })).id);
    let gap = false; let run: Obj;
    try {
      const deadline = performance.now() + 110_000;
      while (true) {
        run = findRun(await call('/runs'), id); if (!busy(run)) break;
        if (scenario === 'telemetry_gap') {
          const state = object(await call('/overview'));
          if (state.data_state === 'stale') {
            gap = true;
            requireThat(state.requests_per_second === null && state.error_rate === null && state.resources === null && array(state.histogram).length === 0, 'Missing telemetry is presented as live data.');
          }
        }
        requireThat(performance.now() < deadline, 'Experiment timeout.'); await sleep(1000, signal);
      }
    } catch (error) {
      // Check our own run before using the single-experiment stop endpoint.
      try { if (busy(findRun(await call('/runs', undefined, undefined, true), id))) await call('/runs/stop', {}, undefined, true); }
      catch { console.error('Could not confirm experiment cleanup; inspect the tester. Fault leases still expire on the target.'); }
      throw error;
    }
    const state = object(await call('/overview')); const result = object(run.result);
    verifyNoAi(initial, object(state.ai)); requireThat(num(state.total_events) > 0, 'No telemetry collected.');
    requireThat(scenario !== 'telemetry_gap' || gap, 'Dashboard never reported the real telemetry interruption.');
    verifySamples(run, array(object(await call('/events')).events));
    const passed = run.status === 'completed' && result.evaluation === 'matched' && result.recovery_confirmed === true;
    console.log(JSON.stringify({ scenario, passed, status: run.status, result })); if (!passed) failures.push(scenario);
  }
  requireThat(!failures.length, `Experiments to inspect: ${failures.join(', ')}`);
  console.log('All requested experiments matched; no additional AI calls or tokens.');
}
export async function smokeAi(options: Options['values'], signal: AbortSignal, request?: Call): Promise<void> {
  only(options, ['url', 'judge', 'model', 'effort', 'confirm-ai']);
  requireThat(options['confirm-ai'] === true, 'smoke-ai invokes real models and consumes account usage. Add --confirm-ai explicitly (2 calls, plus at most 1 diagnostic follow-up and 1 with --judge).');
  const call = request ?? api(String(options.url ?? 'http://localhost:8010'), signal);
  const previous = object(await call('/settings/ai')); let state = object(await call('/agents'));
  const initialCalls = num(state.calls);
  requireThat(state.worker_online && !state.running && !state.queued, 'Start the authenticated worker and wait for an idle AI queue.');
  requireThat(!array(await call('/runs')).some(busy), 'Tester must be idle.');
  const overview = object(await call('/overview')); requireThat(overview.health === 'healthy', 'Wait for healthy telemetry.');
  const previousIncidents = new Set(array(overview.incidents).map(i => i.id));
  const models = array(object(await call('/ai/catalog')).models);
  const model = options.model ? models.find(m => m.id === options.model) : models.find(m => m.is_default);
  const effort = String(options.effort ?? 'low');
  requireThat(model && Array.isArray(model.efforts) && model.efforts.includes(effort), 'Choose a catalog model and supported reasoning effort.');
  const profile: Obj = { model_family: model.family, model_id: model.id, reasoning_effort: effort };
  let id: string | undefined;
  try {
    await call('/settings/ai', { ...previous, enabled: true, execution_mode: 'codex_local', provider: 'codex', auto_analyze: false,
      judge_enabled: options.judge === true, agent_profiles: { investigator: profile, manager: profile, judge: profile }, timeout_seconds: 120 }, 'PUT');
    id = str(object(await call('/runs', { scenario: 'errors', duration: 30, rps: 6, clients: 3, warmup: 5 })).id);
    console.log('Real error scenario started; waiting for detector.');
    const detectionDeadline = performance.now() + 60_000; let incident: Obj | undefined;
    while (!incident) {
      incident = array(object(await call('/overview')).incidents).find(i => i.rule === 'server-errors' && !previousIncidents.has(i.id));
      if (!incident) { requireThat(performance.now() < detectionDeadline, 'No new server-errors incident.'); await sleep(2000, signal); }
    }
    const jobId = str(object(await call(`/incidents/${encodeURIComponent(str(incident.id))}/analyze`, {})).id);
    console.log(`Real incident queued for Codex: ${jobId}`);
    let last = ''; let job: Obj; const deadline = performance.now() + 600_000;
    while (true) {
      state = object(await call('/agents')); const found = array(state.jobs).find(j => j.id === jobId);
      requireThat(found, 'AI job is missing.'); job = found;
      const summary = JSON.stringify({ status: job.status, stage: job.stage, events: array(job.timeline).length, error: job.error });
      if (summary !== last) { console.log(summary); last = summary; }
      if (['completed', 'failed', 'cancelled'].includes(str(job.status))) break;
      requireThat(performance.now() < deadline, 'AI pipeline timed out.'); await sleep(2000, signal);
    }
    requireThat(job.status === 'completed' && job.investigation && job.decision && job.policy, 'AI pipeline did not produce complete reports.');
    const followup = array(job.sessions).filter(s=>s.role === 'investigator_followup');
    requireThat(followup.length <= 1, 'More than one diagnostic follow-up.');
    const expected = (options.judge ? 3 : 2) + followup.length; const policy = object(job.policy);
    requireThat(num(state.calls) === initialCalls + expected, 'Unexpected invocation count.');
    if (options.judge) requireThat(job.judge && ['agree', 'needs_review', 'disagree'].includes(str(policy.judge_verdict)), 'Missing judge report.');
    const usage = object(await call(`/ai/usage?incident_id=${encodeURIComponent(str(job.incident_id))}`)); const totals = object(usage.totals); const sessions = array(usage.sessions);
    requireThat(totals.sessions === expected && totals.usage_reported === expected, 'Incomplete AI usage.');
    requireThat(totals.reported_tokens === sessions.reduce((sum, s) => sum + num(object(s.usage).input_tokens) + num(object(s.usage).output_tokens), 0), 'Token ledger does not reconcile.');
    requireThat(sessions.every(s => s.provider_session_id && s.duration_seconds !== null && object(s.config).model_id === model.id && object(s.config).reasoning_effort === effort), 'Missing session metadata.');
    requireThat(policy.effective_action === 'escalate', 'Expected escalation was not produced; inspect the actual reports.');
    const channels = array(policy.channels);
    requireThat(channels[0]?.status === 'delivered' && channels.slice(1).every(c => c.status === 'not-configured'), 'Unexpected delivery state.');
    const notices = array(await call('/notices')).filter(n => object(n.body).job_id === jobId);
    requireThat(notices.length === 1 && notices[0].channel === 'lab-inbox', 'Expected one internal notification.');
    let run: Obj;
    while (true) { run = findRun(await call('/runs'), id); if (!busy(run)) break; requireThat(performance.now() < deadline, 'Experiment timeout.'); await sleep(2000, signal); }
    requireThat(run.status === 'completed' && num(object(run.result).http_errors) > 0, 'Real error scenario failed.');
    console.log(JSON.stringify({ passed: true, job_id: jobId, requests: object(run.result).sent, http_errors: object(run.result).http_errors,
      decision: object(job.decision).action, effective_action: policy.effective_action, usage: job.usage, notices: notices.length, judge: job.judge,
      sessions: totals, model: model.id, effort }));
  } finally {
    // Restoring settings must still run if the traffic-stop request fails or Ctrl+C aborts normal requests.
    try { if (id && busy(findRun(await call('/runs', undefined, undefined, true), id))) await call('/runs/stop', {}, undefined, true); }
    finally { await call('/settings/ai', previous, 'PUT', true); }
  }
}
