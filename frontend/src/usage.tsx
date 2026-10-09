import { useEffect, useState } from "react";
import { Download, BarChart3 } from "lucide-react";

type Tokens = {input_tokens?: number | null; output_tokens?: number | null; cached_input_tokens?: number | null; reasoning_output_tokens?: number | null};
export type AISession = {id: string; job_id: string; incident_id: string; role: string; started: number; ended: number | null;
  status: string; job_status: string; rule: string; legacy: boolean; duration_seconds: number | null;
  config: {provider: string; model_id: string; model_family: string; reasoning_effort: string};
  provider_session_id: string | null; usage: Tokens; usage_status: string; total_tokens: number | null; error: string | null; budget_cost_usd?: number | null; reserved_usd?: number | null};
type Totals = {sessions: number; completed: number; failed: number; cancelled: number; running: number;
  usage_reported: number; usage_incomplete: number; reported_tokens: number | null; tokens: Tokens; budget_cost_usd: number | null; reserved_usd: number; cost_unavailable_sessions: number};
type Group = Totals & {key: string};
type Report = {generated_at: number; totals: Totals; by_agent: Group[]; by_incident: Group[]; by_model: Group[]; by_job: Group[];
  sessions: AISession[]; billing: string; accounting: string};
const n = (value?: number | null) => value == null ? "—" : value.toLocaleString("en-GB");
const date = (ts: number) => new Date(ts * 1000).toLocaleString("en-GB");

export function SessionTable({sessions, onIncident}: {sessions: AISession[]; onIncident?: (id: string) => void}) {
  return sessions.length ? <div className="usage-table-wrap"><table className="usage-table"><thead><tr>
    <th>Session / incident</th><th>Agent / requested model</th><th>State / duration</th><th>Input</th><th>Output</th><th>Total</th><th>Details</th>
  </tr></thead><tbody>{sessions.map(s => <tr key={s.id}><td><strong className="mono">{s.id.slice(0, 12)}</strong><small>{date(s.started)}</small>{onIncident ? <button type="button" className="usage-link" onClick={() => onIncident(s.incident_id)}>{s.rule} · {s.incident_id.slice(0, 8)}</button> : <small>{s.rule} · {s.incident_id.slice(0, 8)}</small>}</td>
    <td><strong className="role-name">{s.role}</strong><small>{s.config.model_id || "CLI default · unresolved"}</small><small>{s.config.reasoning_effort} effort · {s.config.provider}</small></td>
    <td><span className={`tag job-status-${s.status}`}>{s.status}</span><small>{s.duration_seconds == null ? "Duration unavailable" : `${n(s.duration_seconds)} s`}</small><small>{s.usage_status} usage{s.legacy ? " · legacy record" : ""}</small></td>
    <td>{n(s.usage.input_tokens)}</td><td>{n(s.usage.output_tokens)}</td><td><strong>{n(s.total_tokens)}</strong></td>
    <td><details><summary>Inspect</summary><div className="session-details"><p>Session: {s.id}</p><p>Job: {s.job_id}</p><p>Incident: {s.incident_id}</p><p>Provider request / session: {s.provider_session_id || "Not reported"}</p><p>Budget cost: {s.budget_cost_usd == null ? "Unavailable" : `${s.budget_cost_usd.toFixed(6)}`}</p><p>Unresolved reservation: {s.reserved_usd == null ? "None" : `${s.reserved_usd.toFixed(6)}`}</p><p>Requested family: {s.config.model_family}</p><p>Cached input: {n(s.usage.cached_input_tokens)} (subset of input)</p><p>Reasoning output: {n(s.usage.reasoning_output_tokens)} (subset of output)</p><p>Model and effort above are the captured request configuration, not provider-confirmed runtime metadata.</p>{s.error && <p>Error: {s.error}</p>}</div></details></td>
  </tr>)}</tbody></table></div> : <p className="analysis-body quiet">No invocation sessions in this selection. Queued jobs do not count as model calls.</p>;
}

function GroupTable({title, rows, onIncident}: {title: string; rows: Group[]; onIncident?: (id: string) => void}) {
  return <section className="panel"><div className="panel-heading"><h2>{title}</h2></div><div className="usage-table-wrap"><table className="usage-table"><thead><tr><th>Group</th><th>Attempts</th><th>Reported tokens</th><th>Incomplete usage</th><th>Bedrock budget cost</th></tr></thead><tbody>{rows.map(r => <tr key={r.key}><td>{onIncident ? <button className="usage-link mono" onClick={() => onIncident(r.key)}>{r.key.slice(0, 12)}</button> : <strong className="role-name">{r.key}</strong>}</td><td>{r.sessions}</td><td>{n(r.reported_tokens)}</td><td>{r.usage_incomplete}</td><td>{r.budget_cost_usd == null ? "—" : `${r.budget_cost_usd.toFixed(6)}`}</td></tr>)}</tbody></table>{!rows.length && <p className="analysis-body quiet">No recorded sessions.</p>}</div></section>;
}

export function AIUsageView({onIncident, initialIncident = ""}: {onIncident: (id: string) => void; initialIncident?: string}) {
  const [report, setReport] = useState<Report | null>(null);
  const [role, setRole] = useState("");
  const [period, setPeriod] = useState("all");
  const [incident, setIncident] = useState(initialIncident);
  const [error, setError] = useState("");
  useEffect(() => {
    let live = true;
    const controller = new AbortController();
    const load = async () => {
      const query = new URLSearchParams();
      if (role) query.set("role", role);
      if (incident) query.set("incident_id", incident);
      if (period !== "all") query.set("since", String(Date.now()/1000 - Number(period)*86400));
      try {const response = await fetch("/api/ai/usage?"+query, {signal: controller.signal}); if (!response.ok) throw Error();
        const data = await response.json(); if (live) {setReport(data); setError("");}}
      catch {if (live) setError("Usage service unavailable. Last successful snapshot retained; totals may be stale.");}
    };
    setReport(null); load(); const timer = setInterval(load, 5000);
    return () => {live = false; clearInterval(timer); controller.abort();};
  }, [role, period, incident]);
  const total = report?.totals;
  function download() {
    if (!report) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], {type: "application/json"}));
    const link = document.createElement("a"); link.href = url; link.download = "incidentlab-ai-usage.json"; link.click(); URL.revokeObjectURL(url);
  }
  return <>
    <div className="agent-page-heading"><div><div className="eyebrow">AI ACCOUNTABILITY</div><h1>AI usage & sessions</h1><p className="quiet">Every attempt tied to its incident, agent and captured model settings.</p></div><button className="secondary" disabled={!report} onClick={download}><Download size={16}/> Export report</button></div>
    <div className="usage-filters"><label>Agent<select value={role} onChange={e => setRole(e.target.value)}><option value="">All agents</option>{["investigator", "manager", "judge"].map(r => <option key={r}>{r}</option>)}</select></label><label>Period<select value={period} onChange={e => setPeriod(e.target.value)}><option value="all">All time</option><option value="1">Last 24 hours</option><option value="7">Last 7 days</option></select></label><label>Incident ID<input value={incident} onChange={e => setIncident(e.target.value.trim())} placeholder="All incidents · paste full ID"/></label>{incident && <button className="secondary" onClick={() => setIncident("")}>Clear incident filter</button>}</div>
    {error && <p className="agent-error" role="alert">{error}</p>}
    <div className="agent-stat-grid">{[["Invocation attempts", n(total?.sessions)], ["Reported tokens · input + output", n(total?.reported_tokens)], ["Sessions with complete usage", total ? `${total.usage_reported} / ${total.sessions}` : "—"], ["Bedrock budget cost (not invoice)", total?.budget_cost_usd == null ? "Unavailable" : `${total.budget_cost_usd.toFixed(6)}`]].map(([label, value]) => <section className="panel agent-stat" key={label}><small>{label}</small><strong>{value}</strong></section>)}</div>
    <p className="agent-callout">{report?.billing || "Usage from this IncidentLab database only."} {report?.accounting || "Missing usage is unknown, not zero."}</p>
    {total && <div className="usage-counter-strip"><span>Input <b>{n(total.tokens.input_tokens)}</b></span><span>Output <b>{n(total.tokens.output_tokens)}</b></span><span>Cached input <b>{n(total.tokens.cached_input_tokens)}</b></span><span>Reasoning output <b>{n(total.tokens.reasoning_output_tokens)}</b></span><span>Completed <b>{total.completed}</b></span><span>Failed / cancelled <b>{total.failed} / {total.cancelled}</b></span></div>}
    <div className="analysis-grid"><GroupTable title="Usage by agent" rows={report?.by_agent || []}/><GroupTable title="Usage by requested model" rows={report?.by_model || []}/></div>
    <GroupTable title="Usage by incident · click to open" rows={report?.by_incident || []} onIncident={onIncident}/>
    <section className="panel usage-sessions"><div className="panel-heading"><h2><BarChart3 size={17}/> Invocation sessions</h2><span className="tiny quiet">{report ? `Snapshot ${date(report.generated_at)}` : "Loading…"}</span></div><SessionTable sessions={report?.sessions || []} onIncident={onIncident}/></section>
  </>;
}
