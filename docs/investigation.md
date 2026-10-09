# Read an incident investigation

The incident is the unit of navigation. A simulation run, an incident and an AI investigation are different records; one run can generate several incidents, and an incident can exist without an AI job.

## After a simulation

1. In **Scenario tester**, wait for completion and recovery. In the run row, use **Open investigation** when a request-evidence link is recorded. Otherwise open **Incidents** and select the relevant event; time overlap alone is not treated as a proven link.
2. Read the header first: **INC ID**, title, full detection date/time and browser timezone, event type, service, subject, severity and **current** status. Expand **Full identifiers & incident lifecycle** for full IDs and resolution time.
3. Read the measured chart. The shaded interval is the detector window stored with the AI evidence packet, or the latest rule window if no job exists. The surrounding two minutes provide context, not additional evidence automatically supplied to the model.
4. **AI result** contains the Investigator's conclusion, evidence-backed findings and the Manager's proposal, followed by deterministic policy. Expand limitations and recommended checks. Recommended checks were not executed, and no automatic remediation occurs.
5. **Agent activity** shows recorded execution events and full timestamps. **Evidence** shows the exact frozen packet, selected events, capture time and hash. Neither is private model reasoning.
6. **Notifications** separates the AI draft, policy outcome and actual notice records. `lab-inbox / delivered` means the internal inbox only; it does not mean Slack, Telegram or email was sent.
7. **AI usage** lists this investigation's invocation attempts. **Open incident usage report** opens the broader ledger with this incident preselected. Missing usage remains unknown; reported totals can be partial.

Changing tabs keeps the incident header and chart in place. The **Agents** overview also links each selected investigation to this same incident workspace. Opening, refreshing or switching tabs never invokes AI. If AI is disabled, has not admitted a job or has failed, the page says so instead of showing a fabricated report. An authorized operator can use **Analyze with AI** when permitted; automatic mode considers new incidents under the existing quotas and budget controls.

## Diagnostic investigation

New jobs capture diagnostic context in addition to the detector's selected samples. The report must explain **why**, **observed impact**, alternative hypotheses and **what to do next**. `insufficient_evidence` is a valid outcome when the cause cannot be distinguished; a longer restatement of the threshold is not the objective. Schema/reference validation is not verification that a model's diagnosis is true.

`DiagnosticEvidence.cs` reads at most 10,001 rows from the durable SQL ingest mirror, with an effective 10,000-row capacity limit. It freezes the preceding 60 seconds and detector interval, scopes the service and affected instance for resource incidents, and excludes future/late-arriving rows at capture. Over-limit queries expose no partial aggregates. The baseline is a short comparison window, not a learned seasonal normal.

- Initial model input: before/during request rates, HTTP 5xx ratio, p95 response time, mean process CPU/RSS, coverage and known endpoint purposes. Rates are unavailable below 80% occupied five-second buckets. Coverage is not proof of lossless collection; empty data remains unknown.
- Optional check: `endpoint_breakdown`, `command_repetition` or `resource_timeline`. The model selects at most one; the server supplies its bounded, precomputed frozen result. It cannot choose a query, time range, account, host or arbitrary tool. This is **not a new live OpenSearch query** and does not gather profiler/traces that were never collected.
- Privacy: only known laboratory route names are exposed; unknown paths are grouped. Client/signature groups use aliases. Raw messages, query strings, request bodies, scenario names and expected outcomes are excluded. Do not use production data.
- Final report: cause hypothesis or explicit missing evidence, impact, supporting/contradicting references, a discriminating test, and conditional verification/mitigation/fix proposals with success criteria. No remediation runs.
- Manager: assess actionable value beyond the existing rule notice. A recovered warning does not automatically need another notification; high-severity escalation remains mandatory.

The **Context used to investigate why** table and selected-check details are tied to this job's frozen packet. `ctx-baseline`, `ctx-during` and selected `check-*` references identify measured aggregates, not fabricated event IDs. Unselected checks remain in the captured audit packet but are not supplied to the model. The initial and follow-up reports remain in their separate invocation records; the main result shows the final report, or explicitly marks an incomplete follow-up as preliminary.

Task instructions stay outside the untrusted evidence envelope. The final `follow_up` field records the original selected check for audit; a final model answer of `none` is accepted as “no further check” and normalized to that selection. A different check is rejected. Reference and check failures expose only fixed error categories, not rejected provider text.

Two calls are normal; one optional follow-up and one optional Judge mean **four maximum**. The follow-up uses the Investigator's model profile, has its own usage row, and is included in Investigator totals. All existing daily jobs and Bedrock dollar budgets apply. No retry, extra call or budget reset is performed merely because a report is inconclusive. Historical reports are not regenerated or relabeled as diagnostic investigations.

## What the chart does—and does not—claim

- Source: the persisted SQL ingest mirror of the same measured telemetry forwarded to OpenSearch in an AWS deployment. This is not a separate OpenSearch history query. Local SQLite development mode remains explicitly labeled.
- Request charts aggregate all observed traffic for the incident's service, excluding `/health`, in five-second buckets. Amber is HTTP 404 for probing, or HTTP 4xx/5xx for other request incidents. The chart is not restricted to a single client or simulation.
- CPU/RSS charts use measured process peaks for the affected instance, not host-wide CPU or memory. They do not reproduce the detector's three-sample average as a threshold line.
- A bucket without observations is a gap, not zero. A zero request count requires an observed request or metric sample. Retention gaps, unknown services and an over-limit query are explicitly unavailable; queries exceeding 10,000 rows do not show partial counts.
- Availability incidents do not claim a fixed evidence interval. Historical AI packets without stored boundaries use an explicitly labeled **estimated** interval based on capture time and configured window. Saved packets and hashes are never rewritten to make them look newer.
- Newly generated HTTP requests carry a validated run ID. Links require that ID in recorded detector evidence and a matching stored run. Historical untagged requests and process measurements do not acquire invented simulation links. Correlation is not proof of cause. The tester's link lookup covers the latest 1,000 incidents and 30 runs.

The event detection time, evidence interval, AI capture/completion time and current incident status can all differ. For example, a completed AI report may describe a high-severity snapshot while the incident header now says **Resolved**. The policy card preserves the incident status at decision time.

## Install this version

Follow [test deployment](test-deployment.md). This update changes both Monitor and Target: run `aws install --apply`, then `aws public-install --apply` when public sharing is installed. Updating only the gateway/Monitor leaves the old Target without run tags. Preserve database volumes and existing credentials. No database table migration or historical packet rewrite is required.

For an inexpensive local check, use a fresh database with AI disabled and run `bash scripts/lab.sh smoke --url http://localhost:8010 --scenarios probing`. Follow the steps above and verify real request IDs, a linked incident and recovery. This does not validate AWS or live model access.
