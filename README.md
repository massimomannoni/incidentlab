# IncidentLab

A security and reliability laboratory: create a problem, observe real telemetry, and inspect the evidence behind an incident.

Built in **two days of AI-assisted development** as a working sample, not a production security platform. The useful result is a verifiable flow from real requests to evidence, an agent report and a policy-controlled decision.

**POC 0.2 · C# / ASP.NET Core + TypeScript · deployed on AWS.** Detection is rule-based. Admitted incidents can be investigated by an Investigator, a Manager and an optional Judge through a Codex worker on your Mac or the optional **Amazon Bedrock** integration on AWS. **Agents** shows reports, escalation and delivery; **AI usage** tracks sessions by agent and incident. AI is disabled by default; Bedrock adds pre-call budget admission and requires separate AWS setup and live acceptance.

The reference installation completed a real Bedrock diagnostic investigation on October 11, 2026: before/during comparison, one selected endpoint check, a likely-cause report, proposed mitigation and internal notification delivery with recorded usage. The [acceptance record](docs/verification.md) includes failures and remaining model-quality limitations. Its approved monthly application limit is USD 100; the stricter USD 0.50/day and 10 jobs/day limits remain, with automatic admission for new incidents and the Judge off. New installations still start with AI disabled and a USD 5/month default.

## Start with a test deployment

Follow [Deploy an IncidentLab test environment on AWS](docs/test-deployment.md) for the complete sequence: prerequisites → two EC2 hosts in Milan → real PostgreSQL/OpenSearch → private GUI → optional public URL → verification → shutdown. AI can stay off throughout.

| Guide | Use it for |
|---|---|
| [Test deployment](docs/test-deployment.md) | First installation and acceptance checklist |
| [Installation and configuration](docs/configuration.md) | Copyable setup steps, OpenSearch wiring, AI activation, notification behavior and limits |
| [AWS / CloudShell](infra/aws/README.md) | IAM, network, configuration, installation, private DNS and operations |
| [Public access](docs/public-access.md) | CloudFront HTTPS, CAPTCHA, shared credentials, one-hour sessions and login troubleshooting |
| [Verification](docs/verification.md) | Results actually observed and checks still pending |
| [AI worker](docs/ai-worker.md) | Optional incident-triggered investigations after infrastructure works |
| [Amazon Bedrock](docs/bedrock.md) | AWS IAM integration, EU processing, budget controls and activation |

Owners administer the lab through the private SSM tunnel. Shared guests are read-only; an optional [personal master](docs/public-access.md#personal-master-setup) can operate bounded experiments and request already-enabled AI analysis, but cannot change AI settings or budgets. The authenticated personal master has **no daily simulation cap**; duration, traffic bounds, one concurrent experiment and **all AI quotas/budgets still apply**. Other callers, including the private console, retain 10 simulation starts per UTC day. This is not a per-person guest quota; general guest experimentation remains disabled. The public gateway never exposes raw Monitor, Target or database ports. Source and documentation use the MIT license; deployment creates billable AWS resources in your account. **Login, WAF and AI budgets do not cap the total AWS bill.** Review the [public cost exposure](docs/public-access.md#costs-and-limits) before enabling a public URL.

## The end-to-end flow

The monitor and target expose ASP.NET Core APIs. The React console lets you generate traffic, inspect incidents and follow investigations. The `/api/contracts` endpoint exposes JSON schemas. To preserve data during upgrades, follow the [persistence and backup guidance](docs/architecture.md#persistence-backups-and-upgrades).

Open `IncidentLab.sln` in Visual Studio/Rider, or run `dotnet build IncidentLab.sln`. The solution contains API, Core, Gateway, Worker, Tests and Target projects; the GUI and operational CLI are built with npm. No project component or script requires Python: services use C#, the GUI and tools use TypeScript, and the EC2 bootstrap uses Bash/jq.

### Where are the C# controllers?

This sample uses **ASP.NET Core Minimal APIs**, not MVC `Controller` classes. Routes are registered with `MapGet`, `MapPost` and `MapPut`:

- [Monitor endpoints — LabApplication.cs](backend/IncidentLab.Api/LabApplication.cs): incidents, telemetry ingestion, simulations, settings and AI jobs. [Program.cs](backend/IncidentLab.Api/Program.cs) only starts the application (or pipeline verification).
- [Target endpoints — target/Program.cs](target/Program.cs): `/items`, `/commands`, `/compute` and protected fault controls.
- [Public login and proxy — GatewayApplication.cs](backend/IncidentLab.Gateway/GatewayApplication.cs): login, session enforcement and allowed forwarding routes.

HTTP handlers call application services. [Agents.cs](backend/IncidentLab.Core/Agents.cs) owns investigation scheduling and policy, [AgentPrompts.cs](backend/IncidentLab.Core/AgentPrompts.cs) builds role prompts, and [BedrockExecutor.cs](backend/IncidentLab.Api/BedrockExecutor.cs) executes the AWS model calls. These are not HTTP controllers.

The [current architecture, workflow and screenshots](docs/article-assets/README.md) map these files to the running laboratory.

### Request and investigation flow

```text
Scenario tester → HTTP requests → .NET API on Target EC2
                                         │
                              request logs + CPU/RSS
                                         ↓
                       authenticated ingest → PostgreSQL outbox
                                         ↓
                                     OpenSearch
                                         ↓
                             rule analyzer every 2 s
                                         ↓
                         incidents + evidence + internal inbox
                                         ↓
                                   Admin console

AI execution (one selected provider):
admitted incident → SQL queue → Codex on Mac OR Bedrock on AWS → Investigator → Manager → optional Judge
                                                                            ↓
                                                               policy → inbox + audit
```

The tester knows the scenario; the rules receive only events and measurements. Experiments store expectations separately from results. A repeated signal updates the same incident. Internal notifications are created when an incident opens or resolves, not on every poll.

### Real data and active fault injection

The dashboard does not use demo datasets, random metrics or values assigned by a scenario. The tester sends HTTP requests to the target, which records their outcome, duration and request ID and samples its own process CPU and memory. The analyzer reads the collected events from storage. Selecting SQLite for development changes the store; it does not replace measurements with mocks.

Faults are deliberately introduced into the service: calculations consume CPU, allocations consume memory, responses are delayed, and requests receive actual HTTP 500 responses. The error profile uses a deterministic sequence: at a rate of 0.6, three of every five requests fail. This is not a real compromise or a spontaneous bug, but the observed effects and received responses are real.

Without fresh data, current measurements are `null` and the console shows `Live data unavailable`, not zero requests or zero errors. Charts distinguish intervals without samples from observed intervals without traffic. An observed but idle service can show zero requests; its error rate remains unavailable when there are no requests.

The tester distinguishes HTTP attempts, received responses, transport errors and cancelled requests. Its JSON results retain a bounded sample of response IDs, paths, status codes and client-side timings. The smoke test matches these IDs against server-collected events: a changing chart alone is not proof that the pipeline works.

Unit-test mocks isolate dependencies and failure cases; they do not supply dashboard data at runtime. End-to-end checks require running monitor and target services and real traffic.

### What's included

- ASP.NET Core target API with unauthenticated application endpoints, JSON logging, command signatures and real process metrics.
- Tester with eight scenarios, configurable rate/client count/duration, stop and recovery controls, and JSON result export.
- React/TypeScript console: overview, incidents with evidence, events, agent control room, notifications and settings.
- C# / ASP.NET Core backend, analyzer implemented as a `BackgroundService`, EF Core persistence on PostgreSQL, and a separate C# AI worker.
- Containerized OpenSearch for telemetry storage. This is not Amazon OpenSearch Service.
- Private Monitor/Target Compose configurations and a CloudFormation template for two EC2 instances in an existing VPC; optional gateway Compose and CloudFront/WAF sharing templates.
- Automated tests, scenario smoke tests and a GitHub Actions workflow, without automatic deployment.

### Scenarios and detection rules

| Scenario | Category | Initial rule |
|---|---|---|
| Normal traffic | Baseline | No incident expected under the reference profile |
| Retry loop | Behavior | At least 12 requests with the same signature and client in 20 seconds |
| HTTP probing | Security | At least 12 HTTP 404 responses across 8 distinct paths per IP in 20 seconds |
| Errors | Application | At least 5 HTTP 5xx responses and an error rate of at least 20% |
| Latency | Application | At least 8 responses taking 500 ms or longer |
| CPU | Resources | Three consecutive samples at or above 60% of one core |
| Memory | Resources | Three consecutive samples at or above 160 MiB RSS |
| Telemetry gap | Monitoring | No recent sample for more than 12 seconds |

CPU and RSS describe the **process**, not the entire server. CPU usage can exceed 100% when multiple cores are used. The target uses workstation GC and a 64 MiB managed-heap limit; this is not a limit on total RSS. The memory fault uses a separate 128 MiB mapping, released during recovery. The [.NET GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit) makes this laboratory limit explicit. Probing and repetition are suspicious signals, not proof of compromise. Thresholds are deliberately simple and need calibration in the AWS environment.

Each experiment includes warmup, fault injection, recovery and a final settling period. Faults expire independently on the target even if the monitor stops. Requests are capped at 30/s, 10 clients and 180 seconds; CPU and memory have additional target and container limits. These are not load-testing tools for external systems.

## Running on AWS

The [AWS / CloudShell guide](infra/aws/README.md) covers installation step by step: obtaining the source, prerequisites and permissions, account/VPC/subnet selection, VM creation, secure configuration, installation, OpenSearch, GUI access, DNS, verification and shutdown. Read it before running commands: `--apply` creates or changes real resources and can incur charges.

You need an approved laboratory VPC/subnet, outbound access for SSM and dependency downloads, and a **committed** source revision. From the repository root in CloudShell:

```sh
npm --prefix tools ci
npm --prefix tools run build
cp infra/aws/lab.config.example.json infra/aws/lab.config.json
nano infra/aws/lab.config.json
# Set the account, region, VPC/subnet and official Compose/Buildx SHA-256 checksums.
bash scripts/lab.sh aws preflight
bash scripts/lab.sh aws create --apply
bash scripts/lab.sh aws status
# Continue when both instances are Online in Systems Manager.
bash scripts/lab.sh aws configure --apply
bash scripts/lab.sh aws install --apply
bash scripts/lab.sh aws verify --apply
```

CloudFormation creates Monitor EC2, Target EC2, security groups, an IAM role and a private S3 bucket. The installer deploys the same verified archive to both VMs through SSM, generates secrets in Parameter Store and starts Docker Compose. The Monitor hosts the console, analyzer, PostgreSQL and OpenSearch; the Target hosts the API and sampler. The OpenSearch index does not need to be created manually. Deployment leaves AI disabled and does not copy model credentials.

The Mac is used for development and access; Docker is not required to use the remote laboratory. With AWS CLI, the Session Manager plugin and the same destination configuration available locally, authenticate with an approved IAM/SSO identity:

```sh
aws sso login --profile YOUR_APPROVED_PROFILE
# Alternative for supported console identities: aws login --profile YOUR_APPROVED_PROFILE --region eu-south-1
bash scripts/lab.sh aws tunnel --profile YOUR_APPROVED_PROFILE
```

Open **http://localhost:8010** on the Mac. Running the tunnel in CloudShell does not make it available on your local computer. For an internal DNS name, configure an existing private Route 53 zone and run `bash scripts/lab.sh aws dns --apply`: this does not purchase a domain, expose ports or overwrite conflicting records. For optional HTTPS sharing with login, CAPTCHA and a one-hour session, follow the separate [public-access guide](docs/public-access.md); never expose the raw Monitor port.

**Deployment record — October 10, 2026:** the two-EC2 laboratory was installed in Europe (Milan), `eu-south-1`, using these scripts. CloudFormation, both container builds, private SSM access and the real Target → PostgreSQL outbox → OpenSearch pipeline were verified. An optional CloudFront/WAF gateway was subsequently installed for protected public sharing; the default installation remains private. AI remained disabled. See the [verification record](docs/verification.md) for acceptance results and the pending human CAPTCHA/login check. OpenSearch is containerized, not a managed AWS domain.

The reference sizes cost approximately **USD 101.22/month before tax**, at 730 running hours: two Linux EC2 instances, 70 GiB gp3 and two public IPv4 addresses for outbound connectivity. S3 and data transfer are additional; prices vary and this is not a spending cap. No budget alarm or automatic shutdown is installed. Stop the two instances when unused; retained storage still costs money. See the [AWS operations guide](infra/aws/README.md#8-operations-cost-control-and-removal).

Optional sharing adds about **USD 10/month in fixed WAF charges**, plus CAPTCHA, request and CloudFront usage. Disable sharing before stopping the lab; a disabled distribution does not remove the WAF or its charges. Retrieve the generated URL with `aws public-status` through the lab CLI; credentials remain in the preparing operator's ignored `.local/shared-login.json`, never in this repository.

## Code verification

Run these commands from the repository root. Use an up-to-date .NET 8 SDK patch and Node 22+. Installer tests also require Git, Bash, tar and jq. Python, pip and virtualenv are not required. NuGet and npm dependencies have lockfiles; the TypeScript CLI uses only built-in Node modules at runtime.

```sh
dotnet restore backend/IncidentLab.Tests/IncidentLab.Tests.csproj --locked-mode
dotnet test backend/IncidentLab.Tests/IncidentLab.Tests.csproj --no-restore --nologo
dotnet build target/Target.csproj --nologo
npm --prefix frontend ci
npm --prefix frontend run build
npm --prefix tools ci
npm --prefix tools test
bash -n scripts/lab.sh infra/aws/install-node.sh infra/aws/verify-node.sh infra/aws/install-public.sh infra/aws/verify-public.sh
```

To start the local laboratory on macOS/Linux (WSL2 on Windows), build the full solution in Debug and use the TypeScript launcher:

```sh
dotnet build IncidentLab.sln
bash scripts/lab.sh local
```

The GUI is at `http://127.0.0.1:8010`. Ctrl+C stops only processes started by the launcher, without deleting `.local/lab.db`. Use `--monitor-port`, `--target-port` and `--database` to run a separate laboratory instance. The AI worker starts only when `--with-codex` is added; existing settings are preserved.

With the local laboratory already running, or an SSM tunnel open to AWS:

```sh
bash scripts/lab.sh smoke --url http://localhost:8010
```

**This command generates real traffic and bounded faults in the configured target.** Run it only in the dedicated environment. The suite takes approximately eight minutes. Select a subset with `--scenarios normal retry probing`. The test does not change the target to which the monitor sends requests.

For development checks without OpenSearch, `TELEMETRY_BACKEND=sqlite` is an explicit mode, labeled `DEV · SQLite` in the GUI—not a silent fallback. Results in this mode do not prove that OpenSearch or AWS deployment works. See [verification results and limitations](docs/verification.md).

## AI configuration and visibility

**Settings → AI execution mode** offers Disabled, Codex local worker and Amazon Bedrock. Codex uses the Mac's ChatGPT login without copying credentials to AWS; it is not an offline model. Bedrock uses the Monitor's AWS role after separate deployment approval. Automatic mode is off by default; when enabled, it considers only incidents opened after activation and does not backfill historical incidents.

**Incidents → open an incident** is the investigation workspace: incident ID, full event date and timezone, type, service and current status stay above a measured chart. The highlighted interval identifies the detector evidence window; AI reports keep their frozen window even when the incident changes. Below it, **AI result**, **Agent activity**, **Evidence**, **Notifications** and **AI usage** all refer to that same incident. **Agents → Open incident investigation** reaches the same workspace. Reading it never invokes a model; **Analyze with AI** explicitly queues an eligible manual investigation. See the [step-by-step guide](docs/investigation.md), including historical-data limitations.

New investigations compare a measured 60-second baseline with the detector window. The Investigator must explain likely causes or insufficient evidence, observed impact, alternative hypotheses and specific proposed actions with success checks. It may select **one** bounded diagnostic check (endpoint breakdown, repeated commands or resource timeline) from the frozen telemetry; an additional Investigator call uses that result before the Manager decides. This is not arbitrary SQL, live log access, profiling or automatic remediation. Historical reports remain unchanged. See [diagnostic investigation](docs/investigation.md#diagnostic-investigation).

Limits: two invocations normally, at most one extra Investigator follow-up and one optional Judge (four maximum); one job per incident; one running job at a time; a UTC daily job quota; per-agent timeouts; and no automatic retries. Idle polling does not invoke models. Prompt byte limits are not exact token limits. Bedrock additionally reserves monetary budget before every call: deployment defaults are USD 5/month and USD 0.50/day. An operator can explicitly raise the monthly ceiling to USD 100; the daily ceiling remains USD 0.50. Private settings cannot exceed either deployment ceiling. Codex does not enforce monetary or token budgets; CLI context adds overhead. Read the [budget scope and failure handling](docs/bedrock.md#cost-controls).

**Settings** lets you select Codex family, model and reasoning effort separately for each role from the worker's actual catalog. Bedrock is restricted to Nova Lite EU; it has no reasoning-effort selector. Choices are captured when a job is queued. The Judge can agree or request human review; if it fails, AI notification delivery is blocked. OpenAI API remains unimplemented.

**AI usage** shows attempts, reported tokens, counter coverage and aggregates by agent, model and incident, with filters and JSON export. Bedrock sessions also show conservative budget cost and unresolved reservations, not an AWS invoice. Each call links to its job and incident. Input and output are counted once; cached input and reasoning output are subsets. Missing usage and Codex monetary cost remain unknown. These figures cover this laboratory, not the entire provider account.

See the [worker setup and control-room guide](docs/ai-worker.md). The Mac can run a local test environment or act as a worker connected to the AWS monitor through an SSM tunnel. Slack, Telegram and email remain **not-configured** and are never presented as delivered.

## POC boundaries

- No host/disk sensors, VPC Flow Logs, packet capture or port scanning. HTTP traffic alone cannot support those diagnoses.
- No Slack, Telegram or email integration yet: the inbox records internal notifications, not external delivery.
- One monitor process and one experiment at a time. Do not scale out ASP.NET Core replicas: distributed coordination and cross-process locks are not implemented.
- SQL data and the OpenSearch index have no automatic retention policy yet. Do not leave the laboratory running indefinitely; measure storage growth and stop it when unused.
- The target's delivery queue is bounded and in memory; a batch can be lost after three attempts. The `dropped_events` counter is transmitted, but durable replay from the target is not implemented.
- Evaluation compares rules with signals observed during the test window and checks final recovery. Existing incidents and unrelated concurrent load can interfere: start from a healthy environment and inspect additional detections. A stopped test is not counted as successful.
- Fault effects depend on runtime behavior, scheduling and resources. This is neither a deterministic benchmark nor a measure of production detection accuracy.
- Laboratory credentials, private HTTP and SSM access: this is not production-ready software. See the [security boundaries](SECURITY.md).

## Next milestones

1. Complete human acceptance of the public login/session flow, then broaden long-running load and threshold calibration beyond the validated two-EC2 reference run.
2. Add retention, pipeline reliability metrics and host/network sensors.
3. Implement external notification policies/channels with idempotency and error handling.
4. Complete live Bedrock acceptance, then add read-only investigation tools and report-quality evaluations.
5. Complete the article with a reproducible experiment, real results and limitations; the editorial draft remains separate from the public code.

## License

IncidentLab is released under the [MIT license](LICENSE). You may use, modify and distribute the code, including commercially, provided you retain the copyright notice and license. The software is provided without warranty.

Copyright (c) 2026 Massimo Mannoni. Third-party dependencies remain subject to their respective licenses.
