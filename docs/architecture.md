# Architecture — C# / ASP.NET Core runtime

## Design decisions

**C# for services, TypeScript for the GUI and tools.** `IncidentLab.Api` exposes ASP.NET Core endpoints; `IncidentLab.Core` contains JSON contracts, pure detectors, EF Core, the OpenSearch outbox, the HTTP simulator and AI policy; `IncidentLab.Worker` is a separate process on the Mac; `IncidentLab.Tests` tests these layers. The target also uses ASP.NET Core. `tools/src` contains the launcher, smoke tests and CloudShell installer, compiled from TypeScript and executed with Node. Bash/jq handle the EC2 bootstrap. The project and CI have no Python dependency.

**AWS, two EC2 instances.** An existing laboratory network hosts the monitor and target. Docker Compose manages processes on each host; Kubernetes is not introduced. The CloudFormation template contains infrastructure only. The Mac is a client and development machine and, in Codex mode, hosts the optional AI worker.

**Two access paths, one lab.** The owner uses IAM-controlled SSM forwarding to the private Monitor on 8010. Optional shared guests use HTTPS CloudFront → WAF CAPTCHA → private VPC origin → `IncidentLab.Gateway` on 8020 → Monitor. The gateway runs on the existing Monitor host and enforces shared credentials, CSRF, route/method allowlists and a 60-minute server-side session. Viewer mode is read-only; worker and ingest routes never pass through it. PostgreSQL/OpenSearch have no public host ports. The viewer-to-CloudFront connection is TLS; the private origin hop is HTTP, not end-to-end TLS.

The base and optional ingress-rule stacks live in the application region; the global CloudFront/WAF stack is managed in `us-east-1`. The [test-deployment walkthrough](test-deployment.md) connects the [AWS installer](../infra/aws/README.md) with [protected sharing](public-access.md). Custom public DNS, per-user identity and a managed OpenSearch service are outside this sample.

**Detection without AI.** Every two seconds, a loop synchronizes the outbox to OpenSearch, reads a time window and applies pure rules. It is not an LLM agent and consumes no tokens. The GUI refreshes data through separate polling.

**PostgreSQL and OpenSearch have different roles.** SQL stores event delivery state, incidents, experiments, notifications, activity and settings. OpenSearch supplies the analysis window. The POC retains an SQL copy of events for the outbox and evidence; the cost of this duplication and retention need attention in the next milestone.

**One active monitor.** Scheduling and state transitions belong to a single process. An asynchronous lock serializes concurrent tester starts. Application-level incident deduplication is not a distributed guarantee: adding workers/replicas requires durable locks, constraints and migrations.

## Contracts

`Event` defines a validated envelope: `id`, UTC epoch timestamp, service, instance, type and scope. Specific fields include HTTP path/status/duration, command signature, request ID, CPU and RSS. It does not include the scenario, expected outcome or fault name.

`Finding` contains the rule, category, severity, service, subject, value, threshold, evidence IDs and description. Its stable key is `service:rule:subject`.

`Incident` persists state, timestamps, evidence, a template-based report and acknowledgment. `Notice` is a delivery to the laboratory inbox, in the same transaction as incident opening or resolution. It does not represent delivery through an external provider.

`Run` stores scenario configuration and expectations. Its result contains requests/outcomes and expected, observed, missing, additional and unresolved rules. Evaluation is separate from the detectors; temporal coincidence does not prove causation.

Concrete schemas are in `backend/IncidentLab.Core/Contracts.cs`, `Detection.cs` and `Persistence.cs`. Endpoints are defined in `backend/IncidentLab.Api/LabApplication.cs`; `/api/contracts` exposes the JSON schemas.

EF Core mapping uses explicit table/column names, epoch timestamps and JSON columns. Startup does not delete data; it only marks experiments still recorded as running as interrupted. There is no automatic reset for incompatible schemas.

### Persistence, backups and upgrades

1. Stop experiments, disable AI, and stop the worker and monitor. Do not run two monitors against the same database simultaneously.
2. Back up the database and volumes; do not use `docker compose down -v`. Existing keys and settings remain valid.
3. Before upgrading, rebuild the .NET monitor and worker, GUI and TypeScript tools. Locally, first point the documented launcher's `--database` option at a SQLite copy. On AWS, redeploy through the installer only after testing the new container.
4. Check historical incidents/reports, live data and a normal scenario; enable the worker and AI only if desired. Reopening a database preserves its settings, including automatic mode if previously enabled.
5. To roll back, stop the processes and restore the previously installed revision with its corresponding backup.

If sharing is installed, update its separate Compose project with `aws public-install --apply` through the lab CLI after the application update. It preserves configuration and data; a gateway restart discards in-memory sessions. Neither this step nor the private installer updates existing CloudFormation templates automatically. Keep guests disabled during any intentionally incompatible change and repeat the public browser acceptance checklist before sharing again.

A SQLite schema fixture verifies that telemetry, incidents, settings and reports can be reopened without recreating the database. The October 10 AWS check also restarted real PostgreSQL, OpenSearch and the monitor: stored experiments, incidents, notices and settings survived, and historical search documents remained present. This is not a backup/restore or disaster-recovery test; see the [verification record](verification.md#storage-interruption-and-persistence). Evidence hashing uses canonical JSON without spaces; saved evidence bundles are not modified.

## Delivery and data quality

1. The target measures each event and also writes it to its JSON logs.
2. An in-memory queue sends authenticated batches of up to 200 events, retaining the same IDs across retries. After three attempts, it increments the dropped-event counter.
3. The monitor validates and inserts new IDs into the outbox. The primary key prevents retries or within-batch duplicates from being counted again.
4. The writer uses idempotent OpenSearch bulk requests with stable `_id` values; it marks only accepted items as delivered in SQL.
5. Reads report an error if the window exceeds 10,000 events. A truncated result is not interpreted as the absence of anomalies.
6. Stale sources or unavailable storage produce monitoring incidents. Application incidents do not resolve simply because data is missing.

View queries read 120 seconds; detectors apply a standard 20-second window. Resource samples arrive every two seconds. Ingestion rejects future or overly old events according to its configured bounds; detectors ignore events outside their window. EC2 clocks must be synchronized.

The target queue is not durable, database backups are not configured, and the monitor process is not independently monitored. This is not a guarantee of end-to-end delivery or production availability.

## Incident lifecycle

```text
no incident → signal present → active → signal absent + fresh data → recovering
                                  ↑                                    │
                                  └──────── signal returns ────────────┤
                                                                       ↓
                                                  8 healthy seconds → resolved
```

If data quality degrades during recovery, recovery timers for the service's incidents are reset. A recurrence after resolution opens a new incident; automatic linking to the previous incident is not implemented yet. Acknowledgment is a separate attribute and does not close the problem. Multiple rules are not yet correlated into a parent incident.

## AI orchestration

`GET /api/incidents/{id}` returns read-only display context from `IncidentContext.cs`: a bounded two-minute histogram from the persisted ingest mirror, service/instance metadata and evidence-linked runs. New detections record detector bounds; new AI packets freeze those bounds. Older packets remain unchanged and their estimated intervals are labeled. Target requests carry a validated `X-Lab-Run` ID for correlation, but the ID and scenario name are excluded from the model packet. The [investigation workspace](investigation.md) keeps the header, chart and report tabs attached to one incident; reads never enqueue AI work.

`AISettings` selects Disabled, Codex local worker or Amazon Bedrock Cloud API. Codex profiles select family, model and reasoning effort from the worker's `model/list` catalog. The Bedrock provider uses the approved Nova Lite EU profile and default inference settings. `AgentJob` stores evidence and resolved-settings snapshots, the lease, investigation report, Manager proposal, policy result, reported usage and errors. `AgentEvent` records transitions for the control room. `AgentSession` stores requested configuration, provider request/session ID, start/end times, status, tokens and each role's report. Bedrock reservations and settlement use its existing JSON configuration field without adding a table. The incident's rule-based report remains separate from the AI report.

- An operator queues an incident or enables automatic admission for new incidents only; a unique incident constraint and a UTC daily job quota apply.
- The worker on the Mac polls the queue without invoking LLMs, authenticates with a dedicated key and acquires a lease. The single-process monitor handles one job at a time.
- The Investigator receives measured baseline/during aggregates and may choose one bounded diagnostic check captured in the immutable packet. An optional `investigator_followup` invocation uses only that selected check, then the Manager receives the final report and policy. The Judge adds one further call and holds notification until review: four calls maximum, all accounted and budget-admitted. Disagreement requires human review; failure blocks AI delivery. There is no unrestricted shell, browser, SQL, MCP or AWS access. No new database columns are required; the packet and per-invocation reports retain the audit trail.
- The backend validates schemas and references, requires human escalation for high/critical rules, and saves the internal notification in the same transaction as job completion.
- The timeline, available reported usage counters and each channel's state are exposed in the GUI. There is no private reasoning, mock reporting or simulated external delivery.
- Stop requests, timeouts and expired leases do not trigger automatic retries. Rules and deterministic notifications continue independently of AI.

`GET /api/ai/usage` returns totals and aggregates by agent, incident, job and requested model. The `role`, `incident_id` and `since` filters apply to the entire report, which can be exported from the GUI. Historical jobs without `AgentSession` are read from start events and stored usage without duplicating counters. Missing data stays null; cached input and reasoning output are not added again to input + output. Rows can have complete, partial or missing counters. No monetary price is calculated for the ChatGPT plan.

Bedrock runs through a separate background executor in the Monitor, not the public worker endpoints. It reserves budget in a SQL transaction before each SDK call, settles reported usage and retains unresolved reservations across restarts. A Codex worker cannot claim or submit Bedrock jobs. Changing provider cancels unfinished jobs rather than executing a frozen job with a different provider. Read-only investigation tools, OpenAI API and external channels remain unimplemented. See the [Codex guide](ai-worker.md) and [Bedrock deployment and budget scope](bedrock.md).
