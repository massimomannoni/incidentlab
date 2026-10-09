# Codex worker and escalation control

The laboratory can use Codex on your Mac to analyze a real incident and show how it leads to a notification. Execution is local; the model is not: the CLI's ChatGPT login accesses remote models and consumes account usage. No Codex authentication token is exported to AWS or the database.

This guide describes the **Codex provider**. For execution entirely on AWS through an instance role, use [Amazon Bedrock setup and cost controls](bedrock.md). The same evidence, report validation, escalation policy and session ledger serve both providers; Codex does not inherit Bedrock's monetary budget controls.

## Local setup

Prerequisites: an up-to-date .NET 8 SDK, Node 22+, and Codex CLI authenticated through `codex login`. The monitor and worker use C#; the launcher and smoke tests use TypeScript. Python is not required. The launcher supports macOS/Linux, or WSL2 on Windows. From the project root:

```sh
dotnet build target/Target.csproj
dotnet build backend/IncidentLab.Api/IncidentLab.Api.csproj
dotnet build backend/IncidentLab.Worker/IncidentLab.Worker.csproj
npm --prefix frontend ci
npm --prefix frontend run build
npm --prefix tools ci
npm --prefix tools run build
bash scripts/lab.sh local --with-codex
```

Open `http://127.0.0.1:8010`. The launcher starts the monitor, real API and worker, generates temporary laboratory keys in memory, and uses `.local/lab.db`. Use `--database /path/to/lab.db` to reopen an existing store. It does not start experiments or automatically enable AI: database settings are preserved. Ctrl+C stops the processes it started without deleting reports or telemetry. SQLite is an explicit development mode, not a mock or a validation of OpenSearch.

## Using the console

1. **Settings**: select **Codex — local worker**, provider **OpenAI · Codex / ChatGPT account**, enable **Enable AI execution**, and save. Configure family, model and reasoning effort for the Investigator and Manager; the additional Judge is optional. Set the daily limit, per-agent timeout and maximum event count.
2. **Scenario tester**: run an experiment, such as HTTP errors. The target receives real requests and the detector opens an incident.
3. **Incidents**: open the incident and select **Analyze with AI**. The operation is idempotent: an incident already processed or failed is not run again.
4. **Incident investigation**: the header identifies the incident and event date; the chart highlights the captured evidence interval. Read **AI result**, then **Agent activity**, **Evidence**, **Notifications** and **AI usage** without losing incident context. From **Agents**, choose an investigation and select **Open incident investigation**. See the [visual workflow guide](investigation.md) for exact meanings and historical-data limitations.
5. **Notifications**: inspect the notification actually recorded. `delivered` refers only to the internal inbox, not Slack, Telegram or email.
6. **AI usage**: inspect totals and usage by agent, requested model and incident; filter by time/agent/incident; and export the JSON report. Each session exposes the requested model and effort, observed duration, Codex ID when available, status, and input/output tokens.

The **Automatically analyze new incidents** checkbox applies the same flow to incidents opened after activation, within the daily limit. It does not invoke AI for every event or dashboard refresh. To stop new invocations, disable **Enable AI execution**: queued jobs are cancelled, and the worker stops the active job at the next heartbeat, approximately four seconds later. A request already sent may already have consumed account usage.

## Responsibilities and state

```text
Detector → captured evidence → durable queue
                                    ↓
                            Codex Investigator
                                    ↓ validated report
                              Codex Manager
                                    ↓ proposal
                         Optional separate Judge
                                    ↓ review
                          Deterministic policy
                                    ↓
                         internal inbox or suppression
```

The Investigator receives the rule, selected events and measured baseline/during aggregates. It does not receive the scenario name or expected outcome. It must report possible causes, impact, alternatives and specific proposed actions, or explain why evidence is insufficient. It may select one frozen diagnostic check (endpoints, repeated commands or resource timeline), followed by one additional Investigator call. It cannot freely query logs, a shell, a browser or AWS. The Manager receives the final report and selected evidence and proposes `observe`, `notify` or `escalate`, considering value beyond the existing rule notice. See [diagnostic investigation](investigation.md#diagnostic-investigation).

The Judge runs only when enabled in the job snapshot: one additional call, with its own model/effort, compares both reports against the supplied evidence. `agree` allows policy application. `needs_review` or `disagree` withholds the Manager's text and generates a generic human-review request in the inbox. If the Judge fails, no AI notification is sent; saved reports, the visible failure and deterministic notifications remain. This is neither a guarantee of statistical independence nor authorization to remediate anything.

Policy prevents silently downgrading high/critical rules: it requires escalation with human review. If the incident has recovered in the meantime, its state at decision time is recorded separately; AI describes the snapshot and does not guarantee the current state. No remediation actions are implemented. AI notification does not replace deterministic opening and recovery notifications.

Each transition has a timestamp and audit message. Structured reports are validated, and cited evidence IDs must belong to the bundle. The diagram shows recorded states, not timed animations or simulated thoughts. Private model reasoning is ignored. SQL transactions save the decision, policy result and notification together.

## Limits and failures

- One job per incident; normally two invocations, at most one Investigator follow-up and one optional Judge (four maximum); one running investigation in the single-process monitor.
- The UTC daily quota reserves jobs when queued, including failed and cancelled jobs. No automatic retry occurs after a timeout or lost result.
- An expired lease fails the job. Loss of the monitor stops the Codex process through heartbeat handling; an offline worker leaves jobs queued without invoking models.
- Application input is capped at 24 KB, with a configurable evidence count. These are not model token limits: the CLI adds its own instructions. Legacy token/dollar settings are reserved for Cloud API mode.
- Counters show attempts and tokens actually reported. Usage can be incomplete after an interruption or error. Monetary cost is not invented.
- Amazon Bedrock is the implemented Cloud API provider; it requires separate setup. OpenAI API, arbitrary/live investigation tools, manual retries and external channels remain unimplemented. The bounded diagnostic checks operate on captured telemetry only.

## Model catalog and usage accounting

At startup, the worker reads `model/list` from a temporary Codex app-server process, without creating threads or starting inference. It sends only the catalog to the monitor, not credentials. Menus use the returned models and reasoning efforts; families group model names rather than assert capabilities. Restart the worker to refresh the catalog. Catalog presence does not guarantee account access: only a successful call verifies access. The separate Bedrock provider uses a server-side model allowlist instead of this Codex catalog. OpenAI API remains unimplemented.

Available defaults are resolved and frozen when the job is queued. The worker explicitly passes model and effort to the CLI; changing settings does not alter investigations already admitted. Without a catalog, only unresolved defaults are allowed. The console distinguishes requested configuration from the actual model, which the JSONL stream does not always confirm. Historical records retain `CLI default · unresolved` rather than receiving retrospective model attribution.

`AgentSession` records each attempt, linked to its job, incident and role. The total is **input + output**; cached input and reasoning output are subsets, not additional items. A missing counter remains unknown; totals indicate how many sessions have complete data. Errors after a usage event retain counters already received. An interruption before such an event can leave usage unmeasurable. Duration runs from the recorded start to the received result/error: it includes overhead and is not pure model latency. For older jobs without a ledger, rows are reconstructed from start events and stored token counts without inventing duration or provider IDs.

The report covers only executions in this IncidentLab database. It does not measure every chat in the account or convert the ChatGPT plan into an API price. The daily quota limits admitted jobs, not tokens or dollars; queued jobs may execute the following day.

## AWS monitor with a worker on the Mac

Complete the [test deployment](test-deployment.md) with AI disabled first. The worker uses the **private owner tunnel**, not the shared CloudFront URL: the public gateway always blocks worker, ingest, settings mutations and AI admission. Shared operator mode is not supported. Public viewer sessions cannot enable AI or queue investigations. This section is an optional next step, not a prerequisite for deploying or testing the real telemetry pipeline.

Configure the same random `LAB_WORKER_KEY` (at least 24 characters) in the monitor's Compose environment and the worker's environment only. Start the SSM tunnel described in the AWS guide on local port 8010. The worker uses `MONITOR_URL=http://127.0.0.1:8010`:

```sh
dotnet run --project backend/IncidentLab.Worker --no-build
```

Configure variables locally without pasting secrets into chat or committing them. The worker rejects remote HTTP endpoints. The backend stays on AWS; the Mac must remain powered on and connected. Do not configure a public port on the Mac or copy `auth.json` to the cloud. The optional Bedrock provider removes this Mac-worker dependency.

## Reproducible test

With all three processes already running and the service healthy:

```sh
bash scripts/lab.sh smoke-ai --confirm-ai
bash scripts/lab.sh smoke-ai --confirm-ai --judge
bash scripts/lab.sh smoke --scenarios normal --manual-ai
```

Each AI test requires `--confirm-ai` because it creates a new incident with real HTTP errors, invokes Codex twice plus at most one diagnostic follow-up and one optional Judge, and checks the report, usage ledger, escalation and single internal notification. It uses the catalog's default model with `low` effort; `--model` and `--effort` allow explicit choices. It consumes account usage and restores previous settings even after an error; if the monitor is unreachable, check its state manually. Wait for one test to finish before starting the next. The normal test with `--manual-ai` verifies real traffic without additional invocations; automatic mode must be disabled.

Integration uses official [Codex non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode), with schema-constrained JSON output and existing CLI authentication. Local mode does not reuse this chat as an endpoint.

Prompts are in `backend/IncidentLab.Core/AgentPrompts.cs`; shared input/output schemas are in `backend/IncidentLab.Core/Contracts.cs`; policy and transitions are in `Agents.cs`. `CodexInvocation.cs` handles CLI execution. Markdown files explain the system but are not automatically loaded into prompts. The C# worker uses `ProcessStartInfo.ArgumentList`, an explicitly allowlisted environment, bounded stdout, process-tree cancellation and no inference retries.

The catalog follows [Codex app-server model/list](https://learn.chatgpt.com/docs/app-server); effort uses `model_reasoning_effort` from the [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).
