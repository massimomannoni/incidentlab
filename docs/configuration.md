# Installation and configuration

IncidentLab is a two-day, AI-assisted development sample. This guide explains how to install your own test environment and which integrations are actually implemented. Use synthetic data only. AWS infrastructure is billable even when AI is disabled.

## 1. Install the private laboratory

Use an approved AWS identity, an existing laboratory VPC/subnet with outbound access, Git, Node 22+, npm and AWS CLI v2. The deployment example uses Milan (`eu-south-1`). Review the [permissions, network and cost prerequisites](../infra/aws/README.md) first; these scripts do not create your VPC or override company policies.

In CloudShell, using its current console identity:

```sh
git clone https://github.com/massimomannoni/incidentlab.git
cd incidentlab
git rev-parse HEAD
npm --prefix tools ci
npm --prefix tools run build
cp infra/aws/lab.config.example.json infra/aws/lab.config.json
nano infra/aws/lab.config.json
```

Set your own `account_id`, `vpc_id`, `subnet_id` and unique `stack_name`. Review the pinned Compose/Buildx versions and official SHA-256 checksums. Leave private DNS fields empty unless you have an approved private zone. Keep the ignored configuration file private; do not copy the author's infrastructure identifiers.

```sh
bash scripts/lab.sh aws preflight
bash scripts/lab.sh aws create --apply
bash scripts/lab.sh aws status
```

Wait for CloudFormation completion and **both hosts Online in Systems Manager** before continuing:

```sh
bash scripts/lab.sh aws configure --apply
bash scripts/lab.sh aws install --apply
bash scripts/lab.sh aws verify --apply
```

`--apply` changes real resources. The installer ships **committed HEAD**, rejects tracked changes and uses a checksummed private S3 bundle plus SSM. It creates two EC2 application hosts, starts Compose and generates laboratory secrets in encrypted Parameter Store. It does not depend on GitHub Actions. `verify` checks real telemetry through the pipeline without injecting faults or calling AI.

On a Mac, use the same checkout/configuration, an approved AWS CLI profile and the Session Manager plugin. Append `--profile YOUR_APPROVED_PROFILE` to AWS commands. Open private operator access with:

```sh
bash scripts/lab.sh aws tunnel --profile YOUR_APPROVED_PROFILE
```

Keep the terminal open and visit `http://localhost:8010`. A tunnel in CloudShell does not open a port on your Mac. See the [complete test-deployment walkthrough](test-deployment.md) for authentication, public sharing, updates and shutdown.

## 2. Understand the OpenSearch connection

The default deployment requires **no manual OpenSearch setup or index creation**. [`compose.monitor.yml`](../compose.monitor.yml) starts PostgreSQL and a single-node OpenSearch container. The Monitor uses:

| Runtime setting | Default deployment value / purpose |
|---|---|
| `TELEMETRY_BACKEND` | `opensearch` |
| `OPENSEARCH_URL` | `http://opensearch:9200`, reachable only inside the private Docker network |
| `DATABASE_URL` | Generated PostgreSQL connection for application state, reports and the telemetry outbox |
| `TARGET_URL` | Configured private Target endpoint; not an arbitrary public test target |
| `LAB_INGEST_KEY` / `LAB_CONTROL_KEY` | Separate generated secrets for telemetry ingest and bounded fault controls |

The application creates `incidentlab-events-v1` and forwards measured events from the SQL outbox. Detection reads OpenSearch; the incident context chart reads the persisted SQL ingest mirror. Missing data is unavailable, not a fabricated zero. Confirm the console identifies OpenSearch and `aws verify --apply` succeeds.

`OPENSEARCH_URL` is explicitly set in Compose, **not interpolated from `.env`**. Adding another URL to `.env` alone will not redirect the Monitor. A different endpoint requires a reviewed deployment change, connectivity checks and compatible authentication. This client has no OpenSearch username/password or AWS SigV4 support; pointing it at a managed Amazon OpenSearch Service domain is not a supported drop-in configuration.

The demo container disables OpenSearch's security plugin and publishes **no host port**. Never expose port 9200 or use production logs with this configuration. Back up persistent data and plan retention; the sample does not implement automatic retention or backups. See [persistence and upgrades](architecture.md#persistence-backups-and-upgrades).

## 3. Connect AI only after the pipeline works

AI is off on a new installation. Choose one implemented provider: **Amazon Bedrock on AWS**, or the separate [Codex worker on the Mac](ai-worker.md#aws-monitor-with-a-worker-on-the-mac). There is no generic “any LLM API” connector. Opening a dashboard or reading an incident never invokes a model.

For Bedrock, first approve EU cross-region processing. The bounded integration uses `eu.amazon.nova-lite-v1:0` through Milan; evidence can be processed in other supported EU regions. It uses rotating instance-role credentials, not pasted API keys. See the [Bedrock guide](bedrock.md) for the exact permissions and regional boundaries.

From a clean, configured checkout in CloudShell:

```sh
bash scripts/lab.sh aws bedrock-prepare --apply --allow-eu-inference
# Wait for STACK_NAME-bedrock to reach CREATE_COMPLETE in CloudFormation.
bash scripts/lab.sh aws bedrock-prepare --apply --allow-eu-inference
# Private installation:
bash scripts/lab.sh aws install --apply
# If the public gateway already exists, use this instead:
# bash scripts/lab.sh aws public-install --apply
```

Run preparation twice for a new stack: create its narrow policy, then verify/configure the Monitor. On a workstation, append the approved profile. These commands do not invoke AI, but **existing enabled database settings survive redeployment**. Disable AI privately before upgrading an active installation.

Through the private tunnel, open **Settings → Amazon Bedrock — AWS IAM**:

1. Review model, evidence/output bounds, daily job quota and dollar budgets.
2. Keep automatic admission and the optional Judge off. Enable AI and save.
3. Open one real incident and choose **Analyze with AI**. This is a paid model workflow: two calls normally, plus at most one diagnostic follow-up and one optional Judge (four maximum). Budget admission applies before every call.
4. In that incident, inspect **AI result**, **Agent activity**, **Evidence**, **Notifications** and **AI usage**. Check actual reports and delivery, not just a healthy service badge.
5. Only after acceptance, optionally enable automatic admission for new incidents. The full [investigation walkthrough](investigation.md) explains the fixed evidence window and how results relate to a simulation.

Default deployment ceilings are **USD 5/month and USD 0.50/day**, with at most **10 AI jobs per UTC day**. Private settings can lower, not exceed, the environment ceilings (`LAB_BEDROCK_MONTHLY_CAP_USD` and `LAB_BEDROCK_DAILY_CAP_USD`). The approved reference installation has a USD 100/month ceiling, retaining USD 0.50/day and 10 jobs/day; this does not raise another installation's defaults. Nova Lite has no reasoning-effort setting here.

Budgets reserve usage before dispatch. Unknown outcomes retain their reservation; failures are not automatically retried. These are application-ledger limits, **not a EUR-denominated or account-wide AWS bill cap**. EC2, storage, WAF, traffic, taxes and other account usage remain separate. Do not delete usage rows to reset limits. See [cost controls](bedrock.md#cost-controls).

## 4. Notifications: what can be configured today?

The implemented delivery channel is **`lab-inbox`**, visible in **Notifications**. It needs no webhook, email credentials or additional setup. Rule-based incident-open/recovery notices work without AI.

For AI, the Manager proposes a message and action; deterministic server policy decides delivery. High-severity detections require escalation. An effective `observe` action suppresses the AI notification while retaining existing rule notifications. A disagreeing Judge replaces the Manager draft with a human-review request; a failed Judge blocks AI delivery. `delivered` means an actual internal notice record, not an external message or an executed remediation.

**Slack, Telegram and email adapters are not implemented.** Their channel status is `not-configured`; a suggested channel in a model report is not delivery. There are no supported webhook, bot-token or SMTP settings to enter. Do not publish tokens in the repository, browser settings or article.

To extend the sample, add and test a server-side delivery adapter after policy approval, with secrets stored server-side, bounded retries, idempotency and truthful delivery/failure records. The current boundary is `Deliver` in [`Agents.cs`](../backend/IncidentLab.Core/Agents.cs). This is development work, not an installation toggle.

## 5. Share safely, verify and stop

Optional public access adds a generated CloudFront HTTPS URL, WAF CAPTCHA, shared read-only credentials and absolute one-hour sessions. Follow [public access](public-access.md); the staged commands and status checks matter. The personal master may run bounded experiments **without a daily simulation quota**, but still cannot change AI settings publicly or bypass any AI budget, job quota, traffic/duration limit or single-experiment concurrency rule. General guest testing and a one-per-person quota are not implemented.

To verify behavior without AI, leave AI disabled and run a small experiment from the private console. For an optional command-line check:

```sh
bash scripts/lab.sh smoke --url http://localhost:8010 --scenarios normal errors
```

This deliberately sends real requests and creates actual HTTP 500 responses on the configured lab Target. It consumes the private console's non-master simulation allowance. Inspect the experiment, incident, recovery and internal notices. With AI disabled, **No AI analysis yet** is the correct result.

At the end, stop experiments, await recovery, disable AI, disable public sharing if present, and stop the **two exact laboratory EC2 instances**. Closing the browser or tunnel does not stop billing. Retained EBS/WAF resources can still incur costs. Follow [shutdown and resume](test-deployment.md#6-update-stop-and-resume); this sample has no automatic account-wide spending shutdown.
