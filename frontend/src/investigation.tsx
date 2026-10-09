import { useEffect, useId, useRef, useState } from "react";
import { ArrowLeft, Bot, Check, Clock3, FileText, ShieldCheck, ScanLine, Hand, RefreshCw } from "lucide-react";
import { type AgentJob, type AgentState } from "./agents";
import { SessionTable } from "./usage";
import { DiagnosticContext, DiagnosticReport } from "./diagnostics";
import "./investigation.css";

export type IncidentRecord = {
  id: string; title: string; severity: string; category: string; rule: string; status: string;
  subject: string; opened: number; updated: number; closed: number | null; acknowledged: boolean;
  evidence: { value: number; threshold: number; detail: string; evidence_ids: string[] };
  report: { summary: string; origin: string };
  notices?: { id: string; ts: number; kind: string; channel: string; body: { title: string; message: string } }[];
  agent_runs?: AgentJob[];
  context?: IncidentContext;
};
export type IncidentContext = {
  service: string | null; instances: string[]; source: string; scope: string; data_state: string;
  runs: { id: string; scenario: string; started: number; status: string; correlation: string }[];
  from: number; until: number; bin_seconds: number; captured_at: number;
  window_start: number | null; window_end: number | null; window_precision: string;
  histogram: { ts: number; observed: boolean; requests: number | null; errors: number | null; http_404: number | null; cpu_percent: number | null; memory_mb: number | null }[];
};
export const eventDate = (ts?: number | null) => ts == null || !Number.isFinite(ts) ? "Not recorded" :
  new Date(ts * 1000).toLocaleString("en-GB", { year: "numeric", month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", timeZoneName: "short" });
const eventTime = (ts: number) => new Date(ts * 1000).toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit", second: "2-digit" });
const shortId = (id: string) => id.slice(0, 8).toUpperCase();
const words = (s: string) => s.replaceAll("_", " ").replaceAll("-", " ");
const ruleNames: Record<string, string> = { "http-probing":"HTTP probing", "repeat-command":"Repeated command", "server-errors":"Server errors", "slow-responses":"Slow responses", "process-cpu":"Process CPU pressure", "process-memory":"Process memory pressure", "source-stale":"Telemetry interruption", "storage-unavailable":"Telemetry storage unavailable" };
type Request = <T>(path: string, options?: RequestInit) => Promise<T>;
type Tab = "result" | "activity" | "evidence" | "notifications" | "usage";

export function IncidentTraffic({ context, rule, hasAI }: { context: IncidentContext; rule: string; hasAI: boolean }) {
  const host = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(600);
  useEffect(() => {
    const observer = new ResizeObserver(entries => setWidth(Math.max(200, entries[0].contentRect.width)));
    if (host.current) observer.observe(host.current);
    return () => observer.disconnect();
  }, []);
  const metric = rule === "process-cpu" ? "cpu_percent" : rule === "process-memory" ? "memory_mb" : "requests";
  const resource = metric !== "requests";
  const errorField = rule === "http-probing" ? "http_404" : "errors";
  const unit = metric === "cpu_percent" ? "Process CPU % · peak / 5 s" : metric === "memory_mb" ? "Process RSS MiB · peak / 5 s" : "Requests / 5 s";
  const bins = context.histogram;
  const max = Math.max(1, ...bins.map(b => b[metric] ?? 0));
  const left = 46, right = 12, top = 26, bottom = 164, plot = Math.max(1, width - left - right);
  const x = (ts: number) => left + plot * (ts - context.from) / (context.until - context.from);
  const y = (value: number) => bottom - (bottom - top) * value / max;
  const start = context.window_start, end = context.window_end;
  const precision = context.window_precision === "recorded";
  const ticks = width < 480 ? [context.from, (context.from + context.until) / 2, context.until] : [0, 1, 2, 3, 4].map(i => context.from + (context.until - context.from) * i / 4);
  return <section className="panel investigation-traffic">
    <div className="panel-heading"><h2>{resource ? "Measured resources for this incident" : "Traffic for this incident"}</h2><span className="tiny quiet">{unit}</span></div>
    <div className="investigation-chart-body" ref={host}>
      {context.data_state === "available" && bins.some(b => b[metric] != null) ? <>
        <svg width="100%" height="216" viewBox={`0 0 ${width} 216`} role="img" aria-label={`${unit}. Measured telemetry from ${eventDate(context.from)} to ${eventDate(context.until)}. Gaps mean missing observations, not zero.`}>
          {start != null && end != null && <rect x={x(start)} y={top - 12} width={Math.max(0, x(end)-x(start))} height={bottom-top+12} className={precision ? "investigation-window" : "investigation-window estimated"}/>}
          {[0, max/2, max].map((v,i) => <g key={i}><line x1={left} y1={y(v)} x2={width-right} y2={y(v)} className="investigation-grid"/><text x={left-7} y={y(v)+4} textAnchor="end">{v.toLocaleString("en-GB", { maximumFractionDigits:1 })}</text></g>)}
          {bins.map(b => {
            const value = b[metric], bw = Math.max(1, plot / bins.length - 3);
            const inWindow = start != null && end != null && b.ts < end && b.ts + context.bin_seconds > start;
            const errors = resource ? 0 : Math.min(value ?? 0, b[errorField] ?? 0);
            return <g key={b.ts} opacity={inWindow || !hasAI ? 1 : .6}>
              <title>{eventDate(b.ts)} · {value == null ? "No observations" : `${value.toLocaleString()} ${unit}${resource ? "" : `; ${errors} ${rule === "http-probing" ? "HTTP 404" : "HTTP 4xx/5xx"}`}`}</title>
              {value == null ? <text x={x(b.ts)+bw/2} y={bottom-3} textAnchor="middle" className="investigation-gap">·</text> : <>
                <rect x={x(b.ts)+1} y={y(value-errors)} width={bw} height={bottom-y(value-errors)} className={resource ? "investigation-resource" : "investigation-request"}/>
                {errors > 0 && <rect x={x(b.ts)+1} y={y(value)} width={bw} height={y(value-errors)-y(value)} className="investigation-error"/>}
                {value === 0 && <line x1={x(b.ts)+2} y1={bottom} x2={x(b.ts)+bw} y2={bottom} className="investigation-zero"/>}
              </>}
            </g>;
          })}
          {end != null && <><line x1={x(end)} y1={top-13} x2={x(end)} y2={bottom+2} className="investigation-boundary"/><text x={x(end)-5} y={top-17} textAnchor="end">{precision ? "Window end" : "Estimated end"}</text></>}
          {ticks.map((ts,i) => <text key={ts} x={x(ts)} y={186} textAnchor={i===0 ? "start" : i===ticks.length-1 ? "end" : "middle"}>{eventTime(ts)}</text>)}
          <text x={width-right} y={210} textAnchor="end">{new Date(context.from * 1000).toLocaleDateString("en-GB")} · {Intl.DateTimeFormat().resolvedOptions().timeZone}</text>
        </svg>
        <div className="investigation-legend"><span><i className={resource ? "resource" : "requests"}/>{resource ? "Measured process samples" : "Other responses"}</span>{!resource && <span><i className="errors"/>{rule === "http-probing" ? "HTTP 404" : "HTTP 4xx/5xx"}</span>}<span><i className="window"/>{hasAI ? "AI evidence window" : "Detector window"}{!precision && " · estimated"}</span><span>· No observation</span></div>
      </> : <p className="investigation-no-data">{context.data_state === "capacity-exceeded" ? "Telemetry window exceeds the safe query limit. No partial chart is shown." : "No measured samples available for this service and period. Missing data is not zero traffic."}</p>}
      <div className="investigation-snapshot"><ScanLine size={18}/><div><strong>{hasAI ? "AI evidence window" : "Detector evidence window"}</strong><p>{start != null && end != null ? `${eventDate(start)} → ${eventDate(end)}` : "A fixed detector window was not recorded for this availability event."}</p>
        <small>{context.window_precision === "estimated-legacy" ? "Legacy report: boundaries are estimated from capture time and configured window; exact detector times were not stored. " : ""}{hasAI ? "The report uses the frozen packet, not subsequent traffic." : "No AI report exists yet."}</small></div></div>
      <p className="tiny quiet">Source: persisted ingest telemetry (the local mirror of the OpenSearch deployment pipeline). {context.scope}</p>
    </div>
  </section>;
}

export function IncidentWorkspace({ id, request, agents, canOperate, onBack, onRun, onUsage }: {
  id: string; request: Request; agents: AgentState | null; canOperate: boolean;
  onBack: () => void; onRun: (id: string) => void; onUsage: (id: string) => void;
}) {
  const [incident, setIncident] = useState<IncidentRecord | null>(null);
  const [tab, setTab] = useState<Tab>("result");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const tabId = useId();
  useEffect(() => {
    let live = true; let timer: ReturnType<typeof setTimeout>;
    const controller = new AbortController();
    const load = async () => {
      try { const data = await request<IncidentRecord>("/incidents/" + encodeURIComponent(id), { signal:controller.signal }); if(live) { setIncident(data); setError(""); } }
      catch(e) { if(live) setError((e as Error).message); }
      if(live) timer = setTimeout(load, 10000);
    };
    load(); return () => { live=false; controller.abort(); clearTimeout(timer); };
  }, [id, refresh]);
  async function mutate(action: "analyze" | "ack") {
    setBusy(true);
    try { await request("/incidents/" + encodeURIComponent(id) + "/" + action, {method:"POST"}); setRefresh(v=>v+1); }
    catch(e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }
  if (!incident || incident.id !== id) return <section className="panel analysis-body"><button className="secondary" onClick={onBack}>Back to incidents</button><p role="status">{error || "Loading incident and its recorded investigation…"}</p></section>;
  const job = incident.agent_runs?.[0];
  const context = incident.context;
  const total = job?.sessions?.some(s=>s.total_tokens != null) ? job.sessions.reduce((n,s)=>n+(s.total_tokens ?? 0),0) : null;
  const delivery = job?.policy?.channels ?? [];
  return <div className="incident-workspace">
    <div className="investigation-breadcrumb"><button className="text-button" onClick={onBack}><ArrowLeft size={14}/> Incidents</button><span>/ INC-{shortId(id)}</span><button className="text-button investigation-refresh" onClick={()=>setRefresh(v=>v+1)} aria-label="Refresh incident"><RefreshCw size={14}/> Refresh</button></div>
    {error && <p className="agent-error" role="alert">Could not refresh this incident. The last snapshot below may be out of date. {error}</p>}
    <header className="panel investigation-identity">
      <div className="investigation-eyebrow">Incident under investigation <span className="mono">INC-{shortId(id)}</span></div>
      <h1>{incident.title}</h1><p className="quiet">{ruleNames[incident.rule] || words(incident.rule)}{context?.service ? ` on ${context.service}` : " · service not recorded"}</p>
      <div className="investigation-badges"><span className={`badge ${incident.severity}`}>Severity: {words(incident.severity)}</span><span className={`badge ${incident.status}`}>Status: {words(incident.status)}</span><span className="tag">{incident.acknowledged ? "Acknowledged" : "Not acknowledged"}</span></div>
      <dl className="investigation-facts"><div><dt>Event detected</dt><dd>{eventDate(incident.opened)}<small>{Intl.DateTimeFormat().resolvedOptions().timeZone} · incident opening time</small></dd></div><div><dt>Event type</dt><dd>{ruleNames[incident.rule] || words(incident.rule)}<small>Category: {words(incident.category)}</small></dd></div><div><dt>Affected service</dt><dd>{context?.service || "Not recorded"}<small>Observed instances: {context?.instances.join(", ") || "Not recorded"}</small></dd></div></dl>
      <div className="investigation-links"><span>Subject: {incident.subject}</span><span>Simulation: {context?.runs.length ? context.runs.map(r=><button className="text-button" key={r.id} onClick={()=>onRun(r.id)}>RUN-{shortId(r.id)} · {r.scenario}</button>) : "No recorded request correlation"}</span><span>AI investigation: {job ? <b className="mono">AI-{shortId(job.id)}</b> : "Not started"}</span></div>
      <details className="investigation-identifiers"><summary>Full identifiers & incident lifecycle</summary><p>Incident: {id}</p><p>AI investigation: {job?.id || "None"}</p><p>Updated: {eventDate(incident.updated)} · Resolved: {incident.closed ? eventDate(incident.closed) : "Not resolved"}</p><p>Run links use tagged request evidence; temporal overlap alone does not establish a link or a cause.</p></details>
    </header>
    {context ? <IncidentTraffic context={context} rule={incident.rule} hasAI={!!job}/> : <p className="agent-callout">Incident context is not available from this server version.</p>}
    <div className="investigation-analysis-context"><strong>AI analysis of INC-{shortId(id)}</strong><span>{job ? `${words(job.status)} · ${job.status === "completed" ? "Completed" : "Updated"} ${eventDate(job.updated)}` : "No AI analysis recorded"}</span></div>
    <div className="investigation-tabs" role="tablist" aria-label="Incident investigation details">{([['result','AI result'],['activity','Agent activity'],['evidence',`Evidence${job ? ` · ${job.packet.events.length}` : ''}`],['notifications','Notifications'],['usage','AI usage']] as [Tab,string][]).map(([key,name])=><button key={key} role="tab" id={`${tabId}-${key}`} aria-controls={`${tabId}-panel-${key}`} aria-selected={tab===key} onClick={()=>setTab(key)}>{name}</button>)}</div>
    <section role="tabpanel" id={`${tabId}-panel-${tab}`} aria-labelledby={`${tabId}-${tab}`}>
      {job?.error && <p role="alert" className="agent-error">{job.error} No report or delivery is fabricated.</p>}
      {tab === "result" && <>
        {job && <DiagnosticContext job={job}/>}
        {job?.investigation ? <>
          {job.stage === 'investigator_followup' && <p className="agent-callout">Preliminary investigation only. The selected diagnostic follow-up has not completed; no final diagnosis or Manager decision is available.</p>}
          <div className="investigation-verdict"><div className="eyebrow">Conclusion for the captured evidence</div><h2>{words(job.investigation.classification)} · {job.investigation.confidence} confidence</h2><p>{job.investigation.summary}</p></div>
          <div className="analysis-grid"><article className="panel"><div className="panel-heading"><h2><Bot size={17}/> Investigator</h2><span className="tag">Structure & references checked</span></div><div className="analysis-body">
            <DiagnosticReport report={job.investigation}/>
            <details open={!job.packet.diagnostics}><summary>Observations, limitations & further checks</summary><h3>What the AI observed</h3>{job.investigation.findings.map((f,i)=><div className="finding" key={i}><p>{f.observation}</p><small className="mono">{f.evidence_ids.map(v=>v.startsWith('ctx-')||v.startsWith('check-')?v:shortId(v)).join(" · ") || "Aggregate or availability evidence"}</small></div>)}<h3>Uncertainty & limitations</h3><ul>{job.investigation.limitations.map((l,i)=><li key={i}>{l}</li>)}</ul><h3>Recommended checks · not executed</h3><ul>{job.investigation.recommended_checks.map((l,i)=><li key={i}>{l}</li>)}</ul></details></div></article>
          <article className="panel"><div className="panel-heading"><h2><ShieldCheck size={17}/> Manager & policy</h2><span className="tag">{job.decision ? words(job.decision.action) : "No decision"}</span></div><div className="analysis-body">{job.decision ? <><h3>{words(job.decision.action)} · {job.decision.priority} priority</h3><p>{job.decision.rationale}</p>{job.policy ? <div className="policy-result"><h3>Policy outcome: {words(job.policy.effective_action)}</h3><p>{job.policy.reason}</p><p>Human review: {job.policy.human_review_required ? "Required" : "Not required"}</p><small>Incident was {job.policy.incident_status_at_decision} at decision time; current status is shown above.</small></div> : <p className="quiet">Policy has not completed. The proposal is not an executed action.</p>}</> : <p className="quiet">The manager runs after a validated investigation. No decision has been received.</p>}</div></article></div>
        </> : <section className="panel analysis-body"><h2>{job ? `AI investigation ${words(job.status)}` : "No AI analysis yet"}</h2><p>{job ? "No validated investigation report is available. View Agent activity for the recorded execution state." : "Detection is rule-based. AI may be disabled, waiting for admission, or limited by quotas. Opening this page does not invoke a model."}</p></section>}
        {job?.settings.judge_enabled && <section className="panel analysis-body"><h2>Second judge · {job.judge?.verdict || "No report"}</h2>{job.judge ? <><p>{job.judge.rationale}</p><ul>{job.judge.concerns.map((c,i)=><li key={i}>{c}</li>)}</ul></> : <p>No validated judge report is recorded.</p>}</section>}
        <div className="investigation-outcome"><Hand size={18}/><div><strong>No automatic remediation performed</strong><p>No IP blocked, no server changes. {job?.policy?.remediation || "Model output is advisory; policy controls notification delivery."}</p><button className="text-button" onClick={()=>setTab("notifications")}>View actual notification delivery</button></div></div>
        <details className="panel investigation-rule"><summary>Current rule-based observation · separate from the AI snapshot</summary><p>{incident.report.summary}</p><p>Observed: {incident.evidence.value} · Threshold: {incident.evidence.threshold}</p><p>The incident can change after AI capture. Current counts are not necessarily the counts used by the report.</p></details>
      </>}
      {tab === "activity" && <section className="panel"><div className="panel-heading"><h2><Clock3 size={17}/> Recorded agent activity</h2></div>{job?.timeline.length ? <ol className="agent-timeline">{job.timeline.map(e=><li key={e.id}><time>{eventDate(e.ts)}</time><div><strong>{words(e.stage)}</strong><p>{e.message}</p></div></li>)}</ol> : <p className="analysis-body quiet">No AI execution is recorded for this incident.</p>}<p className="analysis-body tiny quiet">Audit events and concise explanations, not private model reasoning. A recorded notification stage is not proof of external delivery.</p></section>}
      {tab === "evidence" && <section className="panel"><div className="panel-heading"><h2><FileText size={17}/> Captured evidence & diagnostic context</h2></div><div className="analysis-body">{job ? <><p>Frozen at {eventDate(job.packet.captured_at)} · Observed {job.packet.observed_value} · Threshold {job.packet.threshold}</p><p className="quiet">{job.packet.events.length} selected events · {job.packet.missing_evidence_count} omitted or unavailable. Window aggregates plus selected samples, not every request in the chart. {job.packet.diagnostics && 'The first call receives baseline/during aggregates; only the selected diagnostic check is subsequently supplied. Unselected checks in this captured packet are not sent to the model.'}</p><div className="investigation-evidence">{job.packet.events.map(e=><div key={e.id}><time>{eventDate(e.ts)}</time><span>{e.kind}{e.status > 0 ? ` · HTTP ${e.status}` : ""}{e.cpu_percent != null ? ` · CPU ${e.cpu_percent}%` : ""}{e.memory_mb != null ? ` · RSS ${e.memory_mb} MiB` : ""}</span><code>{e.id}</code></div>)}</div><details><summary>Inspect exact frozen packet</summary><pre className="evidence-json">{JSON.stringify(job.packet,null,2)}</pre></details><small className="mono packet-hash">SHA-256 {job.packet_sha256}</small></> : <p>No AI packet exists. Current rule observations are shown under AI result, explicitly labeled as rule-based.</p>}</div></section>}
      {tab === "notifications" && <>
        <section className="panel analysis-body"><h2>AI proposal and delivery</h2>{job?.decision ? <><p>Suggested channels: {job.decision.suggested_channels.join(", ") || "None"}. A suggestion is not a delivery.</p><div className="notification-draft"><strong>Draft · {job.decision.notification_title}</strong><p>{job.decision.notification_message}</p></div></> : <p>No AI notification draft recorded.</p>}<div className="delivery-grid">{delivery.map(c=><div key={c.channel} className={`delivery-item ${c.status}`}><strong>{c.channel}</strong><span>{words(c.status)}</span></div>)}</div>{!delivery.length && <p className="quiet">No policy delivery outcome recorded. External channels are not configured.</p>}</section>
        <section className="panel analysis-body"><h2>Recorded notices for this incident</h2>{incident.notices?.length ? incident.notices.map(n=><article className="investigation-notice" key={n.id}><small>{eventDate(n.ts)} · {n.channel} · {words(n.kind)}</small><h3>{n.body.title}</h3><p>{n.body.message}</p></article>) : <p>No notification records.</p>}</section>
      </>}
      {tab === "usage" && <section className="panel"><div className="panel-heading"><h2>Invocation attempts for this investigation</h2><button className="text-button" onClick={()=>onUsage(id)}>Open incident usage report</button></div><SessionTable sessions={job?.sessions || []}/></section>}
    </section>
    <div className="investigation-footer"><span>{job ? `${job.settings.provider === "bedrock" ? "Amazon Bedrock" : "Codex"} · ${total == null ? "Token usage not reported" : `${total.toLocaleString()} reported tokens (may be partial)`}` : "No model invoked by this view"}</span><div>{!job && <button className="primary" disabled={!canOperate || busy || !agents?.enabled} onClick={()=>mutate("analyze")}>Analyze with AI</button>}<button className="secondary" disabled={!canOperate || busy || incident.acknowledged} onClick={()=>mutate("ack")}><Check size={15}/>{incident.acknowledged ? "Acknowledged" : "Acknowledge incident"}</button></div></div>
  </div>;
}
