import React, { useEffect, useState } from "react";
import { Bot, ArrowRight, ShieldCheck, Bell, Clock3, Radio, FileText } from "lucide-react";
import { SessionTable, type AISession } from "./usage";

type AgentProfile = {model_family: string; model_id: string; reasoning_effort: string};
type Role = "investigator" | "manager" | "judge";
type ModelOption = {id: string; name: string; family: string; efforts: string[]; default_effort: string; is_default: boolean};
type Catalog = {models: ModelOption[]; updated: number | null; source: string};
export type AISettings = {
  enabled: boolean; execution_mode: "disabled" | "codex_local" | "cloud_api";
  auto_analyze: boolean; daily_job_limit: number; max_evidence_events: number;
  provider: string; region: string; model_id: string; max_input_tokens: number;
  max_output_tokens: number; max_tool_calls: number; timeout_seconds: number; daily_budget_usd: number;
  monthly_budget_usd: number; max_prompt_bytes: number;
  judge_enabled: boolean; agent_profiles: Record<Role, AgentProfile>;
};
export type Investigation = { summary: string; classification: string; confidence: string;
  findings: { observation: string; evidence_ids: string[] }[]; limitations: string[]; recommended_checks: string[];
  diagnosis?: {status: string; explanation: string; evidence_refs: string[]}; impact?: string;
  hypotheses?: {explanation: string; supporting_refs: string[]; contradicting_refs: string[]; test: string}[];
  actions?: {kind: string; action: string; rationale: string; verification: string}[]; follow_up?: string };
export type DiagnosticSummary = {id: string; start: number; end: number; observations: number; observed_bucket_fraction: number;
  requests_per_second: number | null; p95_ms: number | null; error_rate_percent: number | null; process_cpu_mean: number | null; process_memory_mean_mb: number | null};
export type Diagnostics = {version: number; data_state: string; source: string; limitations: string[];
  baseline?: DiagnosticSummary; during?: DiagnosticSummary; checks: Record<string, unknown>};
type Decision = { action: string; priority: string; rationale: string; notification_title: string;
  notification_message: string; suggested_channels: string[]; human_review_required: boolean };
export type AgentJob = {
  id: string; incident_id: string; ts: number; updated: number; status: string; stage: string;
  worker_id: string | null; error: string | null; packet_sha256: string;
  packet: { rule: string; severity: string; captured_at: number; observed_value: number; threshold: number;
    scope: string; missing_evidence_count: number; diagnostics?: Diagnostics; events: {id: string; ts: number; kind: string; status: number; duration_ms: number; cpu_percent: number | null; memory_mb: number | null}[] };
  settings: AISettings; investigation: Investigation | null; decision: Decision | null;
  judge: {verdict: string; rationale: string; concerns: string[]; evidence_ids: string[]} | null;
  sessions: AISession[];
  policy: { proposed_action: string; effective_action: string; priority: string; reason: string;
    human_review_required: boolean; incident_status_at_decision: string; remediation: string;
    channels: { channel: string; status: string }[] } | null;
  usage: Record<string, { input_tokens?: number; output_tokens?: number }>;
  timeline: { id: string; ts: number; stage: string; message: string }[];
};
export type AgentState = { enabled: boolean; mode: string; calls: number; tokens: number | null;
  budget?: {daily_limit: number; monthly_limit: number; daily_remaining: number; monthly_remaining: number; unresolved_reservations: number};
  scheduler_error: string | null;
  worker_online: boolean; worker: { worker_id: string; last_seen: number } | null;
  queued: number; running: number; jobs: AgentJob[] };
const when = (ts?: number) => ts ? new Date(ts * 1000).toLocaleString("en-GB", {timeZoneName:"short"}) : "—";
const words = (text: string) => text.replaceAll("_", " ");

export function JobView({ job }: { job: AgentJob }) {
  const timeline = job.timeline;
  const reportedUsage = Object.values(job.usage);
  const reportedTokens = reportedUsage.some(u => u.input_tokens != null || u.output_tokens != null)
    ? reportedUsage.reduce((n, u) => n + (u.input_tokens || 0) + (u.output_tokens || 0), 0).toLocaleString() : job.sessions?.length ? "Unavailable" : "0";
  const followup = timeline.some(e=>e.stage === "diagnostic_check");
  const stages = ["detector", "evidence", "investigator", ...(followup ? ["diagnostic_check", "investigator_followup"] : []), "manager", ...(job.settings.judge_enabled ? ["judge"] : []), "policy", "delivery"];
  const stageNames = ["Detection", "Evidence", "Investigator", ...(followup ? ["Read-only check", "Investigator follow-up"] : []), "Manager", ...(job.settings.judge_enabled ? ["Second judge"] : []), "Policy", "Notification"];
  const stageIndex = stages.indexOf(job.stage);
  return <div className="job-view">
    <div className="execution-flow" aria-label="Agent execution stages">
      {stages.map((stage, i) => {
        const seen = timeline.some(e => e.stage === stage);
        const state = job.status === "completed" ? "done" : stage === job.stage ? job.status : seen && i < stageIndex ? "done" : seen ? "done" : "pending";
        return <React.Fragment key={stage}><div className={`flow-node ${state}`}>
          <span>{String(i + 1).padStart(2, "0")}</span><strong>{stageNames[i]}</strong>
          <small>{state === "done" ? "Recorded" : words(state)}</small>
        </div>{i < stages.length - 1 && <ArrowRight className="flow-arrow" size={15}/>}</React.Fragment>;
      })}
    </div>
    <div className="job-meta"><span>{job.settings.provider === "bedrock" ? "Amazon Bedrock" : "Codex"} · {job.settings.judge_enabled ? "Three-agent review" : "Two-agent review"}</span><span>{job.worker_id || "Worker not assigned"}</span>
      <span>Updated {when(job.updated)}</span><span>Reported tokens: {reportedTokens}</span></div>
    {job.error && <div className="agent-error" role="alert">{job.error} — no automatic retry or fabricated report.</div>}
    {job.status === "queued" && <p className="agent-callout">Waiting in the durable queue. No model has been invoked for this job.</p>}
    <div className="analysis-grid">
      <section className="panel analysis-card"><div className="panel-heading"><h2><Bot size={17}/> Investigator</h2><span className="tag">{job.investigation ? job.investigation.confidence + " confidence" : job.stage === "investigator" && job.status === "running" ? "Running" : "No report"}</span></div>
        {job.investigation ? <div className="analysis-body"><span className="eyebrow">{words(job.investigation.classification)}</span><p>{job.investigation.summary}</p>
          <h3>Evidence-backed findings</h3>{job.investigation.findings.map((f, i) => <div className="finding" key={i}><p>{f.observation}</p><small className="mono">{f.evidence_ids.length ? f.evidence_ids.map(id => id.slice(0, 8)).join(" · ") : "No event reference — availability or aggregate evidence only"}</small></div>)}
          <h3>Uncertainty & limitations</h3><ul>{job.investigation.limitations.map((s, i) => <li key={i}>{s}</li>)}</ul>
          <h3>Recommended checks · not executed</h3><ul>{job.investigation.recommended_checks.map((s, i) => <li key={i}>{s}</li>)}</ul></div>
          : <p className="analysis-body quiet">{job.stage === "investigator" && job.status === "running" ? "The model is evaluating the captured evidence. The validated report will appear here." : "No validated investigation has been received."}</p>}
      </section>
      <section className="panel analysis-card"><div className="panel-heading"><h2><ShieldCheck size={17}/> Manager & escalation</h2><span className="tag">{job.decision ? words(job.decision.action) : "Awaiting review"}</span></div>
        {job.decision ? <div className="analysis-body"><div className="decision-summary"><strong>{words(job.decision.action)}</strong><span>{job.decision.priority} priority</span></div>
          <h3>Decision rationale</h3><p>{job.decision.rationale}</p><h3>Notification draft</h3><div className="notification-draft"><strong>{job.decision.notification_title}</strong><p>{job.decision.notification_message}</p></div>
          <p className="tiny quiet">Suggested channels: {job.decision.suggested_channels.join(", ") || "None"}. A suggestion is not a delivery.</p>
          {job.policy && <div className="policy-result"><h3>Policy outcome: {job.policy.effective_action}</h3><p>{job.policy.reason}</p><p>Human review: <strong>{job.policy.human_review_required ? "Required" : "Not required"}</strong> · Incident: {job.policy.incident_status_at_decision}</p><small>{job.policy.remediation}</small></div>}
        </div> : <p className="analysis-body quiet">The manager runs only after a validated investigation. It proposes escalation; server-side policy controls delivery.</p>}
      </section>
    </div>
    {job.settings.judge_enabled && <section className="panel"><div className="panel-heading"><h2><ShieldCheck size={17}/> Second judge</h2><span className="tag">{job.judge ? words(job.judge.verdict) : job.stage === "judge" ? job.status : "Awaiting reports"}</span></div><div className="analysis-body">
      {job.judge ? <><p>{job.judge.rationale}</p><ul>{job.judge.concerns.map((c, i) => <li key={i}>{c}</li>)}</ul><small className="mono">Evidence: {job.judge.evidence_ids.join(", ") || "Aggregate evidence only"}</small></> : <p className="quiet">The judge independently checks the original evidence, investigation and manager proposal. No AI notification is delivered before a valid review. Failure blocks delivery; rule notifications remain.</p>}
    </div></section>}
    <section className="panel"><div className="panel-heading"><h2>AI sessions & usage</h2><span className="tiny quiet">One row per invocation attempt</span></div><SessionTable sessions={job.sessions || []}/></section>
    <section className="panel"><div className="panel-heading"><h2><Bell size={17}/> Notification delivery</h2><span className="quiet tiny">No external channels configured</span></div>
      <div className="delivery-grid">{(job.policy?.channels || ["lab-inbox", "slack", "telegram", "email"].map(channel => ({channel, status: channel === "lab-inbox" ? "awaiting-decision" : "not-configured"}))).map(c => <div key={c.channel} className={`delivery-item ${c.status}`}><strong>{c.channel}</strong><span>{words(c.status)}</span></div>)}</div>
    </section>
    <div className="audit-grid"><section className="panel"><div className="panel-heading"><h2><Clock3 size={17}/> Execution timeline</h2></div>
      <ol className="agent-timeline">{timeline.map(e => <li key={e.id}><time>{when(e.ts)}</time><div><strong>{words(e.stage)}</strong><p>{e.message}</p></div></li>)}</ol></section>
      <section className="panel"><div className="panel-heading"><h2><FileText size={17}/> Evidence supplied to AI</h2></div><div className="analysis-body">
        <p><strong>{job.packet.rule}</strong> · {job.packet.severity}</p><p>Observed {job.packet.observed_value} · threshold {job.packet.threshold}</p>
        <p className="tiny quiet">Snapshot {when(job.packet.captured_at)} · {job.packet.events.length} events · {job.packet.missing_evidence_count} omitted or unavailable. No live tool access.</p>
        <details><summary>Inspect exact evidence packet</summary><pre className="evidence-json">{JSON.stringify(job.packet, null, 2)}</pre></details>
        <small className="mono packet-hash">SHA-256 {job.packet_sha256}</small><p className="tiny quiet">Audit events and concise decision explanations are shown. Private model reasoning is not collected.</p>
      </div></section></div>
  </div>;
}

export function AgentsView({ state, stale, initialIncident, onIncident }: { state: AgentState | null; stale: boolean; initialIncident: string | null; onIncident: (id: string) => void }) {
  const [selected, setSelected] = useState<string | null>(initialIncident ? state?.jobs.find(j => j.incident_id === initialIncident)?.id || "none" : null);
  const job = selected ? state?.jobs.find(j => j.id === selected) : state?.jobs[0];
  return <>
    {state?.scheduler_error && <div className="agent-error" role="alert">{state.scheduler_error}</div>}
    <div className="agent-page-heading"><div><div className="eyebrow">EVENT-DRIVEN AI OPERATIONS</div><h1>Agent control room</h1><p className="quiet">Real executions, evidence and escalation decisions. No model calls while the queue is idle.</p></div>
      <span className={`worker-status ${state?.worker_online && !stale ? "online" : ""}`}><Radio size={16}/>{stale ? "Connection lost" : state?.worker_online ? "Worker online" : "Worker offline"}</span></div>
    <div className="agent-stat-grid">{[["Mode", state?.enabled ? state.mode === "cloud_api" ? "Amazon Bedrock" : "Codex local worker" : "AI disabled"], ["Queue / running", state ? `${state.queued} / ${state.running}` : "—"], ["Invocation attempts", state?.calls ?? "—"], ["Reported tokens", state?.tokens?.toLocaleString() ?? "—"]].map(([name, value]) => <div className="panel agent-stat" key={name}><small>{name}</small><strong>{value}</strong></div>)}</div>
    {state?.mode === "cloud_api" && state.budget ? <p className="agent-callout">AI budget remaining: ${state.budget.daily_remaining.toFixed(4)} / ${state.budget.daily_limit.toFixed(2)} today · ${state.budget.monthly_remaining.toFixed(4)} / ${state.budget.monthly_limit.toFixed(2)} this month (UTC). {state.budget.unresolved_reservations} unresolved reservations. Infrastructure and taxes are separate. New calls stop when their reservation does not fit.</p> : <p className="tiny quiet">Codex execution uses your ChatGPT plan, not an offline model. Monetary cost is unavailable; missing usage is not zero usage.</p>}
    {!!state?.jobs.length && <div className="agent-job-selector"><label>Investigation<select value={job?.id || "none"} onChange={e => setSelected(e.target.value)}><option value="none">Choose an investigation</option>{state.jobs.map(j => <option key={j.id} value={j.id}>{j.packet.rule} · {when(j.ts)} · {j.status}</option>)}</select></label><button className="secondary" disabled={!job} onClick={() => job && onIncident(job.incident_id)}>Open incident <ArrowRight size={15}/></button><span className={`tag job-status-${job?.status}`}>{job?.status}</span></div>}
    {job ? <section className="panel analysis-body"><div className="eyebrow">Investigation of incident INC-{job.incident_id.slice(0,8).toUpperCase()}</div><h2>{job.packet.rule} · {job.status}</h2><p>{job.investigation?.summary || "No validated AI report yet."}</p><p className="quiet">Open the incident to see its date, affected service, evidence window, AI result and actual notification delivery together.</p><button className="primary" onClick={()=>onIncident(job.incident_id)}>Open incident investigation <ArrowRight size={15}/></button></section> : <section className="panel agent-empty"><Bot size={32}/><h2>No AI execution selected</h2><p>The owner can enable an AI provider in Settings. Automatic mode admits only new incidents after activation, subject to quotas and budget. Existing reports can be selected above.</p></section>}
  </>;
}

export function AISettingsForm({ value, onChange, onSave, busy }: { value: AISettings; onChange: (s: AISettings) => void; onSave: () => void; busy: boolean }) {
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [catalogError, setCatalogError] = useState("");
  useEffect(() => {
    let live = true;
    const load = async () => {try { const r = await fetch("/api/ai/catalog?provider=" + encodeURIComponent(value.provider)); if (!r.ok) throw Error("Model catalog unavailable"); const data = await r.json(); if (live) {setCatalog(data); setCatalogError("");}} catch {if (live) setCatalogError("Model catalog unavailable. No model list is fabricated.");}};
    load(); const timer = setInterval(load, 5000); return () => {live = false; clearInterval(timer);};
  }, [value.provider]);
  const models = catalog?.models || [];
  const bedrock = value.execution_mode === "cloud_api";
  const chooseMode = (mode: AISettings["execution_mode"]) => onChange({...value, execution_mode: mode, enabled: false, provider: mode === "cloud_api" ? "bedrock" : "codex",
    ...(mode === "cloud_api" ? {region: "eu-south-1", model_id: "eu.amazon.nova-lite-v1:0", max_tool_calls: 0, daily_job_limit: Math.min(10, value.daily_job_limit), max_output_tokens: Math.min(2000, value.max_output_tokens),
      agent_profiles: {investigator: {model_family: "auto", model_id: "", reasoning_effort: "default"}, manager: {model_family: "auto", model_id: "", reasoning_effort: "default"}, judge: {model_family: "auto", model_id: "", reasoning_effort: "default"}}} : {})});
  const profileChange = (role: Role, profile: AgentProfile) => onChange({...value, agent_profiles: {...value.agent_profiles, [role]: profile}});
  return <form className="ai-settings-form" onSubmit={e => {e.preventDefault(); onSave();}}>
    <label>AI execution mode<select value={value.execution_mode} onChange={e => chooseMode(e.target.value as AISettings["execution_mode"])}>
      <option value="disabled">Disabled — rules only</option><option value="codex_local">Codex — local worker</option><option value="cloud_api">Amazon Bedrock — AWS IAM</option></select></label>
    <label>Provider<input readOnly value={bedrock ? "Amazon Bedrock · Nova Lite EU" : "OpenAI · Codex / ChatGPT account"}/></label>
    <p className="agent-callout">{bedrock ? "AWS IAM role, no API keys. Milan endpoint with EU cross-region inference: evidence can be processed outside Milan within the EU. Server deployment approval is required. Paid usage is charged to your AWS account." : "Codex runs on your Mac using its existing ChatGPT login. Evidence is sent to OpenAI; account limits apply. No credentials are copied to AWS."}</p>
    <label className="checkbox-row"><input type="checkbox" checked={value.enabled} disabled={value.execution_mode === "disabled"} onChange={e => onChange({...value, enabled: e.target.checked})}/> Enable AI execution</label>
    <label className="checkbox-row"><input type="checkbox" checked={value.auto_analyze} disabled={value.execution_mode === "disabled"} onChange={e => onChange({...value, auto_analyze: e.target.checked})}/> Automatically analyze new incidents</label>
    <label className="checkbox-row"><input type="checkbox" checked={value.judge_enabled} disabled={value.execution_mode === "disabled"} onChange={e => onChange({...value, judge_enabled: e.target.checked})}/> Enable second judge · one additional invocation per incident</label>
    <p className="tiny quiet">The judge audits both reports before delivery. Disagreement requires human review; a failed judge blocks the AI notification. The judge may use a different model but is not guaranteed to be unbiased or independent of the provider.</p>
    {catalogError && <p role="alert" className="agent-error">{catalogError}</p>}
    <p className="tiny quiet">{bedrock ? catalog?.source : catalog?.updated ? `Catalog refreshed ${new Date(catalog.updated * 1000).toLocaleString("en-GB")}. Restart the worker to refresh.` : "Start the worker to discover models. Only CLI defaults are available until then."} Catalog membership does not guarantee account access; family labels group model names.</p>
    {(["investigator", "manager", "judge"] as Role[]).map(role => {
      const p = value.agent_profiles[role];
      const model = p.model_id ? models.find(m => m.id === p.model_id) : models.find(m => m.is_default);
      return <fieldset key={role} className="profile-settings" disabled={value.execution_mode === "disabled" || (role === "judge" && !value.judge_enabled)}><legend>{role === "judge" ? "Second judge" : words(role)}</legend><div className="profile-fields">
        <label>Model family<select aria-label={`${role} model family`} value={p.model_family} onChange={e => {const family = e.target.value; profileChange(role, {model_family: family, model_id: family === "auto" ? "" : models.find(m => m.family === family)?.id || "", reasoning_effort: "default"});}}><option value="auto">All families / default</option>{[...new Set(models.map(m => m.family))].map(f => <option key={f}>{f}</option>)}</select></label>
        <label>Model<select aria-label={`${role} model`} value={p.model_id} onChange={e => profileChange(role, {...p, model_id: e.target.value, reasoning_effort: "default"})}>
          {p.model_family === "auto" && <option value="">Catalog default{models.find(m => m.is_default) ? ` · ${models.find(m => m.is_default)?.name}` : " / CLI default"}</option>}
          {p.model_id && !models.some(m => m.id === p.model_id) && <option value={p.model_id}>{p.model_id} · unavailable in catalog</option>}
          {models.filter(m => p.model_family === "auto" || m.family === p.model_family).map(m => <option key={m.id} value={m.id}>{m.name}</option>)}</select></label>
        <label>Reasoning effort<select disabled={bedrock} aria-label={`${role} reasoning effort`} value={p.reasoning_effort} onChange={e => profileChange(role, {...p, reasoning_effort: e.target.value})}><option value="default">{bedrock ? "Not supported by Nova Lite" : `Model default${model ? ` · ${model.default_effort}` : ""}`}</option>{(model?.efforts || []).filter(e => e !== "default").map(e => <option key={e}>{e}</option>)}</select></label>
      </div></fieldset>;
    })}
    <div className="form-grid">
      {([["daily_job_limit", "Maximum jobs per UTC day", 1, bedrock ? 10 : 100], ["timeout_seconds", "Timeout per agent (seconds)", 5, 180], ["max_evidence_events", "Maximum evidence events", 1, 30]] as const).map(([key, text, min, max]) => <label key={key}>{text}<input type="number" required min={min} max={max} value={value[key]} onChange={e => onChange({...value, [key]: +e.target.value})}/></label>)}
    </div><p className="agent-callout">Two calls normally, plus at most one Investigator follow-up on a frozen read-only check{value.judge_enabled ? " and one Judge call" : ""}. At most {value.judge_enabled ? "4" : "3"} invocations per job · up to {value.daily_job_limit * (value.judge_enabled ? 4 : 3)} attempts for new jobs admitted per UTC day, still subject to budget. No idle inference or automatic retries.{!bedrock && " Higher effort can increase usage and latency."}</p>
    {bedrock && <div className="form-grid">{([["daily_budget_usd", "AI daily budget (USD, UTC)", 0, .5, .01], ["monthly_budget_usd", "AI monthly budget (USD, UTC)", 0, 100, .01], ["max_output_tokens", "Maximum output tokens per agent", 100, 2000, 100], ["max_prompt_bytes", "Maximum complete prompt bytes", 4000, 24000, 1000]] as const).map(([key, label, min, max, step]) => <label key={key}>{label}<input type="number" required min={min} max={max} step={step} value={value[key]} onChange={e => onChange({...value, [key]: +e.target.value})}/></label>)}</div>}
    <p className="tiny quiet">One job per incident. Model settings are frozen at admission; a lower live budget still stops new calls. No shell, browser or MCP tools. {bedrock ? "Private budgets cannot exceed the operator's deployment ceilings. Daily and monthly limits both apply. Calls reserve the full model context at conservative rates before dispatch. Reported tokens settle the reservation; unknown outcomes remain reserved. Budget accounting is not an AWS invoice. No token-count approximation is used as a hard cap." : "Codex token and dollar fields are not enforced. The daily job limit is not a token or money cap."}</p>
    <button className="primary" disabled={busy}>Save settings</button>
  </form>;
}
