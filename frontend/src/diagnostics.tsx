import type { AgentJob, DiagnosticSummary, Investigation } from "./agents";

const words = (value: string) => value.replaceAll("_", " ");
const refs = (values: string[]) => values.join(" · ") || "No supporting measurement";
export function DiagnosticReport({ report }: {report: Investigation}) {
  if (!report.diagnosis || !report.hypotheses?.length) return <p className="agent-callout">Legacy analysis: this report predates diagnostic context. It has not been re-run or rewritten.</p>;
  return <div className="diagnostic-report">
    <h3>Why it may have happened <span className="tag">{words(report.diagnosis.status)}</span></h3>
    <p>{report.diagnosis.explanation}</p><small className="mono">{refs(report.diagnosis.evidence_refs)}</small>
    <h3>Observed impact</h3><p>{report.impact}</p>
    <h3>Hypotheses & how to distinguish them</h3>
    {report.hypotheses.map((h,i)=><div className="finding" key={i}><p><strong>{i+1}.</strong> {h.explanation}</p>
      <small>Supports: {refs(h.supporting_refs)}<br/>Against: {h.contradicting_refs.join(" · ") || "No contradictory measurement cited; not proof of the hypothesis."}</small>
      <p><strong>Discriminating check:</strong> {h.test}</p></div>)}
    <h3>What to do next · proposals, not executed</h3>
    {report.actions?.map((a,i)=><div className="diagnostic-action" key={i}><span className="tag">{a.kind}</span><strong>{a.action}</strong><p>{a.rationale}</p><small><strong>Success check:</strong> {a.verification}</small></div>)}
  </div>;
}
export function DiagnosticContext({job}: {job: AgentJob}) {
  const d=job.packet.diagnostics;
  if(!d) return null;
  const fields: [keyof DiagnosticSummary,string][]=[['requests_per_second','Requests / second'],['p95_ms','Response p95 · ms'],['error_rate_percent','HTTP 5xx · %'],['process_cpu_mean','Mean process CPU · %'],['process_memory_mean_mb','Mean process RSS · MiB']];
  const value=(s:DiagnosticSummary|undefined,key:keyof DiagnosticSummary)=>typeof s?.[key]==='number' ? Number(s[key]).toLocaleString('en-GB',{maximumFractionDigits:2}) : 'Unavailable';
  const check=job.investigation?.follow_up;
  const selected=check && check!=='none';
  const completed=job.sessions.some(s=>s.role==='investigator_followup' && s.status==='completed');
  return <section className="panel diagnostic-context"><div className="panel-heading"><h2>Context used to investigate why</h2><span className="tag">Frozen measured data · {d.data_state}</span></div>
    {d.baseline && d.during ? <><div className="diagnostic-comparison"><table><thead><tr><th>Measurement</th><th>Before · 60 s</th><th>Incident window · {Math.round(d.during.end-d.during.start)} s</th></tr></thead><tbody>{fields.map(([key,label])=><tr key={key}><th>{label}</th><td>{value(d.baseline,key)}</td><td>{value(d.during,key)}</td></tr>)}</tbody></table></div>
      <p className="tiny quiet">Observed five-second buckets: {Math.round(d.baseline.observed_bucket_fraction*100)}% before / {Math.round(d.during.observed_bucket_fraction*100)}% during. Sparse coverage hides request rates; it does not prove zero traffic or lossless collection. Aggregate references: ctx-baseline / ctx-during.</p></> : <p className="analysis-body">Diagnostic telemetry is unavailable or exceeded the safe query bound. The AI must name missing measurements, not invent a cause.</p>}
    <p className="agent-callout">{selected ? `${words(check)} · ${completed ? 'follow-up report recorded' : 'selected; final follow-up not completed'}. One bounded read of captured telemetry; no arbitrary query or system changes.` : 'No additional diagnostic check selected. Context collection itself uses no model calls.'}</p>
    {selected && d.checks[check] != null && <details><summary>Inspect the selected diagnostic check</summary><pre className="evidence-json">{JSON.stringify(d.checks[check],null,2)}</pre></details>}
    <small className="quiet">Source: {d.source}. Temporal association is not proof of cause. No profiler, traces or deployment history are collected.</small>
  </section>;
}
