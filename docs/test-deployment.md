# Deploy an IncidentLab test environment on AWS

Use this walkthrough to deploy your own synthetic-data laboratory, open its real dashboard, and optionally share a protected read-only URL. Application services run in **Europe (Milan), `eu-south-1`** in the example. AI is **disabled on a new database**; infrastructure verification does not invoke a model. This is a billable test environment, not a production security service.

For a compact setup checklist and the exact configuration boundaries for OpenSearch, AI and notifications, see [Installation and configuration](configuration.md). This working sample was built in two days with AI assistance; that is not a production-hardening or deployment-time guarantee.

## Choose the access path

| Audience | Entry point | What it allows |
|---|---|---|
| Owner/operator | IAM-authorized SSM tunnel from the Mac to `http://localhost:8010` | Start experiments, inspect incidents, change settings |
| Shared guest, optional | Generated HTTPS CloudFront URL | CAPTCHA, shared login, read-only dashboard, absolute 60-minute session |
| Optional AI worker | The owner's loopback SSM tunnel plus its separate worker key | Claim admitted investigations; never use the public guest URL |

Monitor EC2 runs ASP.NET Core, React, PostgreSQL and containerized OpenSearch. Target EC2 runs the API and bounded fault injection. The optional gateway runs on the Monitor; CloudFront/WAF reach it through a private VPC origin. No public EC2, database or SSH ingress is required. The [architecture](architecture.md) and [security boundaries](../SECURITY.md) explain the trust model.

## 1. Prepare an approved account and checkout

Ask the account owner to approve costs, deployment permissions and an existing laboratory VPC/subnet with working egress. Enable Milan if necessary. Do not use production data, root credentials or long-lived access keys. Review the [AWS prerequisites and permissions](../infra/aws/README.md#1-authenticate-and-get-a-reviewed-source-revision); the scripts do not build the network or fix corporate IAM policy.

Choose **CloudShell** or your **Mac/workstation** for installation. Both require Git, Node 22+, npm and AWS CLI v2. The Mac additionally needs the official Session Manager plugin for the tunnel. Docker and .NET are not needed on the operator machine just to deploy: builds run on EC2.

- CloudShell uses the console identity. Check `aws sts get-caller-identity`; do not add workstation profile names to commands there.
- On the Mac, authenticate with `aws sso login --profile YOUR_APPROVED_PROFILE` (after your organization's SSO setup), or supported temporary `aws login`. Append `--profile YOUR_APPROVED_PROFILE` to every `bash scripts/lab.sh aws ...` command below. See the [authentication guide](../infra/aws/README.md#1-authenticate-and-get-a-reviewed-source-revision).

In a new checkout:

```sh
git clone https://github.com/massimomannoni/incidentlab.git
cd incidentlab
git rev-parse HEAD
# Review this revision before deploying; retain its SHA or a Git bundle.
node --version
npm --prefix tools ci
npm --prefix tools run build
cp infra/aws/lab.config.example.json infra/aws/lab.config.json
nano infra/aws/lab.config.json
```

Set your own `account_id`, `vpc_id`, `subnet_id` and unique `stack_name`; retain `region: eu-south-1` for Milan. Leave private DNS fields empty unless you have an approved zone. Review the pinned Compose/Buildx versions and official checksums. Do not overwrite an existing deployment's config or substitute the author's company resource IDs. This file is ignored by Git.

The installer deploys **committed HEAD**, not unsaved changes or a downloaded ZIP. It rejects tracked modifications. GitHub Actions is not a deployment dependency; a blocked CI account does not prevent these operator-run commands.

## 2. Create and install the private lab

Run these stages in order, from the repository root. Commands containing `--apply` perform AWS operations.

```sh
bash scripts/lab.sh aws preflight
bash scripts/lab.sh aws create --apply
bash scripts/lab.sh aws status
```

`preflight` checks identity, VPC/subnet, DNS and template syntax. `create` provisions the two VMs, security groups, instance role and private release bucket. Wait for CloudFormation completion **and both SSM agents Online** before continuing; `status` may refuse while bootstrap is incomplete.

```sh
bash scripts/lab.sh aws configure --apply
bash scripts/lab.sh aws install --apply
bash scripts/lab.sh aws verify --apply
```

Configuration generates separate laboratory secrets in encrypted Parameter Store values. Installation ships one checksummed bundle through S3/SSM and starts Docker Compose. PostgreSQL, OpenSearch and its telemetry index are created by the application path; no managed OpenSearch domain or manual index is needed. `verify` checks live Target telemetry through the analyzer and search store, not merely VM or HTTP health. It does not inject faults or call AI.

Keep the source revision, stack outputs, remote command IDs and verification result in your private deployment notes. Do not record decrypted configuration, cookies or passwords in public issues. If an install is interrupted, inspect its SSM command before retrying; it may still be running.

## 3. Open and exercise the real dashboard

On the **Mac**, use the same checkout/config, build the TypeScript tools as above, authenticate and leave this terminal open:

```sh
bash scripts/lab.sh aws tunnel --profile YOUR_APPROVED_PROFILE
```

Open `http://localhost:8010`. A tunnel started in CloudShell is not a tunnel on your Mac. The console should identify OpenSearch and display fresh measurements; idle traffic can be zero, but stale measurements must be shown as unavailable.

For an optional, bounded behavioral check, use a second Mac terminal:

```sh
bash scripts/lab.sh smoke --url http://localhost:8010 --scenarios normal errors
```

This sends real requests and deliberately produces HTTP 500 responses on **your configured lab Target**. Check the experiment result, `server-errors` incident, event evidence, recovery and internal notification. Omit `--scenarios` for all eight scenarios, provided the private laboratory's remaining daily allowance covers all eight. Non-master starts use one of 10 starts per UTC day; stopping or restarting does not refund it. Only the authenticated public personal master is exempt from the daily simulation count, never from AI budgets or load bounds. Check `/api/limits` or the Scenario tester first. Do not run experiments concurrently or point the smoke runner at the public viewer URL: it cannot start them. AI remains off unless an operator explicitly enables it; existing database settings survive redeployment.

## 4. Add a shareable HTTPS URL if needed

Read the [public-access prerequisites, security and costs](public-access.md#install-and-enable) first. CloudFront is global, and the edge/WAF stack lives in `us-east-1`; the application and access-rule stack stay in Milan. No domain purchase is needed. The scripts use the default CloudFront hostname/certificate; custom domains and ACM configuration are not automated.

From the same configured operator checkout (append the profile on the Mac):

```sh
bash scripts/lab.sh aws public-prepare --apply --access viewer
bash scripts/lab.sh aws public-status
# Wait for CREATE_COMPLETE before installing.
bash scripts/lab.sh aws public-install --apply
bash scripts/lab.sh aws public-enable --apply
bash scripts/lab.sh aws public-status
# Wait for UPDATE_COMPLETE and CloudFront deployment propagation.
```

Preparation initially leaves the distribution disabled. `public-status` prints `PublicUrl`, `PublicHost` and `DistributionId`. The generated username/password are written once to **`.local/shared-login.json` on the machine that ran preparation**. If that was CloudShell, the file is in that CloudShell checkout, not on the Mac. Store it in an approved password manager and share credentials privately, separately from the URL. AWS holds a password hash; another checkout cannot recover the plaintext by rerunning preparation.

The public browser flow is **CAPTCHA → shared login → viewer dashboard → logout or one-hour expiry**. Complete it manually before sharing. Confirm that experiments/settings cannot be changed, logout returns to login, and an unsigned API request is rejected. The [browser acceptance checklist](public-access.md#browser-acceptance-and-troubleshooting) covers expiry and common 403 errors. Infrastructure checks do not prove this human step; never bypass CAPTCHA to mark it passed.

`viewer` is the only shared public role. To run tests yourself through the HTTPS URL, follow [personal master setup](public-access.md#personal-master-setup): `aws public-master-prepare --apply` through the lab CLI, then `aws public-install --apply`. Store the separate `.local/master-login.json` privately. The master has no daily simulation cap but cannot configure AI or bypass AI budgets, concurrency or experiment load bounds. Individual guest tester identities and per-person daily quotas are not implemented. AI keys, worker routes and ingest routes are never exposed by this gateway. Read the [billing-risk limits](public-access.md#costs-and-limits) before sharing: login, throttling and AI budgets do not cap the total AWS bill.

## 5. Keep AI optional

The deployment works without an AI subscription or model API key. **Agents** and **AI usage** reflect stored activity; no admitted jobs means no new model calls. Choose the [Codex worker](ai-worker.md#aws-monitor-with-a-worker-on-the-mac) or follow the separate [Amazon Bedrock setup](bedrock.md) for AWS IAM access, EU inference and budget admission. Base deployment does not grant Bedrock access or enable AI. External Slack/Telegram/email delivery remains unimplemented.

For Bedrock acceptance, keep automatic admission and the Judge off, manually analyze one retained real incident, then inspect both validated reports, internal delivery and usage. Only then enable automatic admission for new incidents. The reference installation has passed this check, but each account must complete its own. A failed job is retained without automatic retry; unknown usage keeps its budget reservation. See the [reference results](verification.md#bedrock-live-acceptance-october-10-2026).

The [incident investigation walkthrough](investigation.md) explains what to inspect after a run: event identity, measured chart, frozen evidence window, AI result, agent activity, actual notifications and per-incident usage. With AI disabled, the page explicitly shows that no AI analysis exists. Historical runs without request tags do not acquire invented links.

## 6. Update, stop and resume

For a reviewed code update, stop experiments, leave time for recovery, and back up persisted data. Build the tools from a clean committed revision. Run `aws install --apply` for both application hosts, followed by `aws verify --apply`. If public sharing exists, run `aws public-install --apply` as well; it preserves credentials and volumes but a restarted gateway revokes sessions. Reload the login form after the update. See the [upgrade limitations](architecture.md#persistence-backups-and-upgrades); there is no automatic database rollback.

To end a test session:

1. Stop experiments, await recovery, disable AI if enabled and stop the local worker.
2. If public sharing was created, run `bash scripts/lab.sh aws public-disable --apply` and inspect `public-status` until the update completes. This disables access, **not WAF billing**.
3. Close the SSM tunnel with Ctrl+C. Stop the **two exact laboratory EC2 instances** shown by `aws status`, using the AWS console. Stopping containers or closing CloudShell does not stop EC2 charges.
4. Preserve required reports and credentials. EBS, WAF and other retained resources can still cost money. Log out of a temporary console CLI session with `aws logout --profile YOUR_APPROVED_PROFILE` when finished.

To resume, start those same two instances, wait for SSM Online, run `aws verify --apply`, then reopen the tunnel. If sharing is needed, run `aws public-enable --apply` and wait for deployment. Do not create a second stack or rotate secrets merely to restart a stopped lab. The [AWS operations guide](../infra/aws/README.md#8-operations-cost-control-and-removal) explains logs, persistent volumes, residual costs and reviewed removal.

## Acceptance and limits

Before sharing your installation, record: private pipeline verification; a real normal/error experiment with recovery if behavioral testing is in scope; a human CAPTCHA/login/logout check for public access; and the chosen shutdown plan. Mark unperformed checks as pending.

The [reference verification record](verification.md) separates the tested Milan deployment, offline suites, the login-policy correction and remaining manual acceptance. It does not certify a new account's deployment. Reference costs were about USD 101.22/month for the private lab, or USD 111.22 including WAF's fixed reference charges, **before tax and variable usage**; use the detailed guides' dated sources and recalculate for your network. Bedrock budget admission covers the application's model calls only. No account-wide AWS spending cap, automatic shutdown, backup service or production availability guarantee is implemented.
