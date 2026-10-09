# AWS installation from CloudShell or your workstation

This is the executable **installation path for a private, non-production laboratory**. On October 10, 2026, these scripts provisioned and installed the two-EC2 sample in Europe (Milan), with real PostgreSQL/OpenSearch and SSM access. See the [verification record](../../docs/verification.md) for tested behavior and limitations; a successful reference deployment does not validate every account/network configuration. AI is disabled on a new database and no model credentials are deployed.

Start with the [test-deployment walkthrough](../../docs/test-deployment.md) for the full private-to-public sequence and acceptance checklist. This guide details the private foundation; the [public-access guide](../../docs/public-access.md) adds the optional gateway without opening the raw admin API. Hosted GitHub Actions is not required to run the installer.

## What gets installed

| Location | Components | Access |
|---|---|---|
| Monitor EC2 (`t3.large`, encrypted 50 GiB gp3) | React console, ASP.NET Core, rule analyzer, PostgreSQL 17.6, OpenSearch 3.3.0 | Private IP, TCP 8010; browser via SSM tunnel |
| Target EC2 (`t3.small`, encrypted 20 GiB gp3) | .NET 8 API, real request logs and process metrics | Monitor security group only, TCP 8080 |
| Private S3 bucket | Checksummed source bundles, seven-day expiry | HTTPS; instance role reads release objects |
| Parameter Store | Separate Monitor/Target `SecureString` configuration | IAM-controlled; never printed by installer |
| Operator Mac (optional) | Codex worker | SSM tunnel; existing login stays on the Mac |

OpenSearch is a **single-node Docker container**, not Amazon OpenSearch Service. It and PostgreSQL publish no host ports. OpenSearch security is disabled only inside the private Docker network. This is a trusted two-host lab, not a hardened multi-tenant deployment.

The Target has a 512 MiB container limit and an explicit `shm_size: 256m`. Its 128 MiB memory-fault mapping needs more than Docker's default 64 MiB `/dev/shm`; without this setting, the first Linux container test crashed during page writes. Keep the shared-memory capacity above the fault size. It is not preallocated RAM, and the total container memory cap still applies. See [Docker's service shared-memory option](https://docs.docker.com/reference/compose-file/services/#shm_size).

No VPC, subnet, NAT gateway, public endpoint, domain registration, TLS certificate, Bedrock integration or budget is created by the private base stack. The optional public extension separately adds CloudFront/WAF and the default CloudFront HTTPS certificate. Use an approved existing laboratory VPC/subnet. The hosts need outbound access to SSM, S3, Amazon Linux repositories, GitHub releases and container/package registries. With a private subnet this typically requires existing egress infrastructure; SSM endpoints alone do not provide access to public build registries. Security-group egress remains default. Public-IP assignment depends on the selected subnet, but no public inbound rules are added.

## 1. Authenticate and get a reviewed source revision

Choose one operator environment. CloudShell uses the current console identity. On a workstation, use a dedicated approved profile; never assume an existing profile belongs to the same account as the browser. Prefer an IAM/SSO deployment identity rather than root, and do not create long-lived access keys for this sample.

For IAM Identity Center:

```sh
aws sso login --profile YOUR_APPROVED_PROFILE
aws sts get-caller-identity --profile YOUR_APPROVED_PROFILE
```

For supported console identities, AWS CLI 2.32.0+ also supports [temporary browser-based login](https://docs.aws.amazon.com/cli/latest/userguide/cli-configure-sign-in.html):

```sh
aws login --profile YOUR_APPROVED_PROFILE --region eu-south-1
aws sts get-caller-identity --profile YOUR_APPROVED_PROFILE --region eu-south-1
```

This is an alternative to SSO, not an additional required login. The browser approval grants local tools the selected identity's permissions; it does not reduce them. Check the account and role before approving. AWS manages the short-lived credentials and refreshes them for up to 12 hours. If the approval expires, restart the login; never paste tokens into chat or the repository. No AWS coding-agent plugin or skill installation is required.

For workstation commands below, append `--profile YOUR_APPROVED_PROFILE` to every `bash scripts/lab.sh aws ...` invocation. The install commands work on the workstation too; Docker builds execute on EC2, not the Mac. Use `aws logout --profile YOUR_APPROVED_PROFILE` to remove a temporary console-login session when finished. This is distinct from stopping EC2 or closing an SSM session.

### Source and prerequisites

Use **standard CloudShell**, in the intended account and region. Node 22+, npm, Git and AWS CLI v2 are required; Docker is not required in CloudShell because builds run on EC2. The installer is TypeScript with no runtime npm dependencies and does not require Python. The session uses your console identity—do not paste AWS access keys into files. CloudShell includes Node/npm, but AWS does not guarantee a fixed version: check `node --version` against the [current environment documentation](https://docs.aws.amazon.com/cloudshell/latest/userguide/vm-specs.html) and use an approved Node 22+ installation if needed.

Clone the repository and select a reviewed commit:

```sh
git clone https://github.com/massimomannoni/incidentlab.git
cd incidentlab
git checkout REVIEWED_COMMIT_SHA
node --version
npm --prefix tools ci
npm --prefix tools run build
bash scripts/lab.sh --help
```

If direct cloning is unavailable, upload a reviewed Git bundle through **CloudShell → Actions → Upload file**, then `git clone incidentlab.bundle incidentlab`. A maintainer can create the bundle with `git bundle create incidentlab.bundle HEAD` after committing the reviewed source. A ZIP without Git history cannot be deployed by this installer.

The installer archives committed `HEAD`, not arbitrary working-directory files. It refuses tracked modifications and known credential/database filenames. Review source for embedded secrets yourself; filename checks are not secret scanning. Commit the installer scripts before attempting installation. Uncommitted local work is not deployed.

## 2. Configure the destination and tool versions

```sh
cp infra/aws/lab.config.example.json infra/aws/lab.config.json
nano infra/aws/lab.config.json
```

Set `account_id` to the expected 12-digit account, `region`, `stack_name`, `vpc_id` and `subnet_id`. The example uses Europe (Milan), `eu-south-1`; change it to your approved destination. Milan is an opt-in region: an authorized account administrator must enable it and wait for `ENABLED` before deployment. Region activation propagates IAM identities to that region; it is not an infrastructure deployment. `lab.config.json` is ignored by Git and contains no secrets. Both VPC DNS attributes must already be enabled. The preflight checks them but will not modify a shared VPC.

The installer downloads the **x86_64 Linux** Compose and Buildx plugins. Pin the version **and its published SHA-256** in the config. The example versions are fixed references, not a promise that they are the newest or vulnerability-free:

- [Compose v2.39.4 release](https://github.com/docker/compose/releases/tag/v2.39.4): the example includes the official `docker-compose-linux-x86_64` checksum, checked on October 10, 2026.
- [Buildx v0.28.0 release](https://github.com/docker/buildx/releases/tag/v0.28.0): the example includes the official `buildx-v0.28.0.linux-amd64` checksum, checked on the same date.

Use an approved newer pair if required, updating both versions and digests. The scripts reject missing checksums and verify each downloaded binary before installation. They never execute a `curl | sh` installer. See the [official Compose plugin instructions](https://docs.docker.com/compose/install/linux/).

The operator needs reviewed permissions for CloudFormation, EC2, IAM role/profile creation and `iam:PassRole`, S3 deployment objects, SSM managed-node sessions/commands and the two Parameter Store names. Optional DNS also needs Route 53 permissions for the chosen zone. SCPs, permissions boundaries, quotas and egress can still block deployment. Do not grant administrator access just to bypass a failed check.

## 3. Check and create the VMs

```sh
bash scripts/lab.sh aws preflight
bash scripts/lab.sh aws create --apply
bash scripts/lab.sh aws status
```

`preflight` is read-only: account match, subnet/VPC match, DNS attributes and template validation. It does **not** prove egress, sufficient capacity, budget approval or successful container builds.

`create --apply` creates billable resources and IAM permissions. It refuses to replace an existing stack of the same name. New stacks have termination protection. Review compute, storage, public IPv4, data-transfer and existing NAT costs with the account owner first. AI disabled does not mean AWS is free. T3 uses **standard CPU credits**; sustained experiments can exhaust credits and affect results.

CloudFormation initially installs Docker, Git and jq; it configures the OpenSearch kernel setting on the Monitor. It does not install Python for IncidentLab. Stack completion is not bootstrap completion. Wait until both instances appear **Online** in Systems Manager. If not, check cloud-init output, the instance role and outbound connectivity. Commands for an existing pre-installer stack need an explicitly reviewed template update; do not delete it to upgrade.

The template selects the standard Amazon Linux 2023 x86_64 AMI, which [includes AWS CLI v2](https://docs.aws.amazon.com/linux/al2023/ug/awscli2.html). The installer checks that the CLI exists before retrieving its bundle or configuration; a custom/minimal AMI needs a separate review.

## 4. Generate configuration and install the application

```sh
bash scripts/lab.sh aws configure --apply
bash scripts/lab.sh aws install --apply
bash scripts/lab.sh aws verify --apply
```

`configure` creates random ingest, control, worker and database secrets without printing them. Configuration is stored under `/incidentlab/STACK_NAME/monitor` and `/incidentlab/STACK_NAME/target`, encrypted with the default AWS-managed Parameter Store key. Re-running preserves existing secrets and refreshes host addresses. Mismatched or incomplete existing secrets cause a refusal rather than a silent password reset. The Target configuration has no PostgreSQL password or worker key; both EC2 hosts nevertheless remain trusted under the shared instance role. See [security boundaries](../../SECURITY.md).

`install` uploads one source bundle to the private S3 bucket and runs the same digest on both EC2 hosts through SSM Run Command. Each host verifies the bundle, installs the checksum-pinned Docker plugins, retrieves its configuration and writes a root-only `.env`. Compose builds and starts the services. Releases live under `/opt/incidentlab/releases/BUNDLE_SHA256`; `/opt/incidentlab/current` points to the last successfully started release on each host. Stable Compose project names preserve database volumes across releases. This is **not** an atomic two-host rollout or an automatic database rollback.

OpenSearch starts automatically from `compose.monitor.yml`. The analyzer creates `incidentlab-events-v1` with one shard and zero replicas; the SQL outbox forwards measured events with stable IDs. No manual index creation or public OpenSearch URL is required.

`verify` runs read-only diagnostics on the Monitor through SSM, so it requires `--apply` even though it does not inject faults. It checks PostgreSQL readiness, OpenSearch cluster reachability, indexed events and **live target telemetry read through the analyzer**. A passing `/api/health` alone is insufficient. It never invokes AI. Each remote command ID is printed for troubleshooting; inspect failures in Systems Manager → Run Command without pasting secret values into issues.

If installation is interrupted, inspect that command's terminal status before re-running: the EC2 command may still be active. A completed installation can be repeated with the same release; secrets and volumes are retained. A failed release does not automatically restore earlier application versions.

## 5. Open the GUI from the Mac

Install AWS CLI v2 and the [Session Manager plugin](https://docs.aws.amazon.com/systems-manager/latest/userguide/install-plugin-macos-overview.html) using the official/organizational procedure. Authenticate with your approved SSO or temporary console-login profile as described above. Keep the same reviewed source and destination config on your Mac, then run:

```sh
bash scripts/lab.sh aws tunnel --profile YOUR_APPROVED_PROFILE
```

Open **http://localhost:8010** in the Mac browser. Keep the terminal running; Ctrl+C closes only the tunnel. The script forwards to the Monitor's private IP, not the instance loopback, because Compose binds to that private address. A tunnel launched in CloudShell terminates in CloudShell, **not on the Mac**, so run this step locally. See [AWS remote-host port forwarding](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-sessions-start.html#sessions-remote-port-forwarding).

The GUI should display **OpenSearch**, with live process measurements. A quiet target can have no request traffic yet; missing measurements are not zeros. To test real HTTP requests and recovery, build the TypeScript tools locally using the same npm commands, keep the tunnel open, and run:

```sh
bash scripts/lab.sh smoke --url http://localhost:8010 --scenarios normal retry probing
```

**This smoke test really generates traffic and bounded faults.** Use only the dedicated lab. Omit `--scenarios` for the full suite (approximately eight minutes). Successful infrastructure verification does not replace these behavioural tests or AWS threshold calibration.

## 6. Give the Monitor a private DNS name (optional)

An EC2 instance already has a private IP/hostname. A DNS record is an optional name, not a new server, a domain purchase or access control.

Choose an existing **private hosted zone associated with this VPC**, owned/approved by your organization. Set `private_zone_id` and `dns_name`, for example a zone `lab.example.internal` and name `incidentlab.lab.example.internal`, then:

```sh
bash scripts/lab.sh aws dns --apply
```

The script creates a private A record pointing to the Monitor private IP, TTL 60. It refuses public zones, unrelated VPCs and existing conflicting A/CNAME records. Re-running an identical record is a no-op. Allow DNS propagation and resolve it from an associated VPC. If EC2 is replaced and the address changes, review the existing record manually; it is not automatically overwritten.

Private DNS does not make the service reachable from a normal home Internet connection. The Mac still uses `localhost:8010` through SSM. Direct access by private name requires an approved VPN/routing/DNS configuration and separately reviewed security-group ingress; the script does not add it. See [Route 53 private zones](https://docs.aws.amazon.com/Route53/latest/DeveloperGuide/hosted-zones-private.html).

**For a public URL, use the separate [protected-sharing installation](../../docs/public-access.md).** It adds CloudFront HTTPS, AWS WAF CAPTCHA and an authenticated gateway with a one-hour session. The default private installation above remains unchanged. Never point a public domain directly at the unauthenticated Monitor/Target or expose TCP 8010/8080/9200.

An optional [personal master](../../docs/public-access.md#personal-master-setup) uses separate application credentials for bounded experiments, incident acknowledgement and already-enabled AI analysis. Prepare with `bash scripts/lab.sh aws public-master-prepare --apply`, then deploy with `aws public-install --apply` through the same lab CLI. No AWS identity is created. Shared visitors stay read-only and AI configuration stays private. The authenticated master has no daily simulation cap; other callers retain 10 starts per UTC day. All AI budgets, experiment load limits and one-at-a-time admission remain enforced. `/api/limits` reports the server-verified allowance. Keep the master password private and retain run history; the installer provisions its narrow server-to-server admission credential without exposing it to browsers.

## 7. Enable AI only when desired

Base deployment does not install a worker or copy Codex credentials. For the Mac path use the [worker guide](../../docs/ai-worker.md) with the tunnel running. Retrieve only the `LAB_WORKER_KEY` field from the Monitor SecureString into your authorized local worker environment; do not paste it into chat, commit it or export the whole config. The worker uses `MONITOR_URL=http://127.0.0.1:8010`.

For AWS-native execution, follow [Amazon Bedrock setup and cost controls](../../docs/bedrock.md). The optional `bedrock-prepare --apply --allow-eu-inference` command creates narrowly scoped IAM access and prepares the Monitor for rotating instance credentials. It does not enable AI database settings or call a model. EU cross-region processing and costs require approval. The base installer needs no Bedrock permissions.

The default AI ceilings are USD 5/month and USD 0.50/day. After explicit approval, `bedrock-budget --apply --monthly-usd 100` can change the Monitor monthly ceiling, preserving all other configuration. Reinstall Monitor and update the private AI setting to apply it; follow the [monthly-budget runbook](../../docs/bedrock.md#change-the-monthly-ceiling). The daily maximum stays USD 0.50. These are application admission limits, not account-wide AWS billing caps.

## 8. Operations, cost control and removal

Reference estimate checked October 10, 2026 for Milan, Linux shared-tenancy On-Demand: `t3.large` at USD 0.0958/hour and `t3.small` at USD 0.024/hour, plus 70 GiB gp3 at USD 0.0924/GiB-month and two public IPv4 addresses at USD 0.005/address-hour. At 730 hours this is about **USD 101.22/month before tax, S3 and transfer**. Sources: [AWS regional EC2 price list](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonEC2/current/eu-south-1/index.csv) and [VPC public IPv4 pricing](https://aws.amazon.com/vpc/pricing/). Recheck current rates for your region; existing NAT gateways, endpoints or other shared infrastructure are not included. T3 uses standard CPU credits in this template.

This estimate is not a billing limit. Configure organization-approved cost alerts separately if required; neither AWS Budgets nor automatic shutdown is created by the sample.

From an SSM session on the relevant host:

```sh
cd /opt/incidentlab/current
sudo docker compose -f compose.monitor.yml ps
sudo docker compose -f compose.monitor.yml logs --tail 100 monitor opensearch
```

Use `compose.target.yml` on the Target. Do not run `docker compose config` without `--quiet`, or print `.env`/decrypted parameters: they expose secrets. For bootstrap failures inspect `cloud-init status`, `/var/log/cloud-init-output.log` and `systemctl status docker`.

Stop experiments in the GUI and await recovery before stopping services. `docker compose ... stop` preserves volumes but **does not stop EC2 billing**. Stop the exact two instance IDs from `status` in the AWS console when unused; EBS and other resources remain chargeable. No automatic shutdown is configured.

If public sharing exists, disable it first with `bash scripts/lab.sh aws public-disable --apply` and wait for the edge update. Closing the browser, the SSM tunnel or CloudShell does not shut down AWS. A disabled CloudFront distribution leaves the WAF resources and their fixed charges in place. To resume, start the same two instances, wait for SSM Online, run `aws verify --apply` through the lab CLI, then reopen the tunnel and optionally run `aws public-enable --apply`. If you explicitly stopped Compose services, start those services too; `restart: unless-stopped` does not undo an intentional stop. See [EC2 stop/start behavior](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/Stop_Start.html).

Before deletion, export required reports and back up PostgreSQL/OpenSearch volumes. Disabling termination protection and deleting the stack removes EC2 and its EBS data (`DeleteOnTermination=true`). **There is no automatic backup.** The S3 bucket is retained; its objects expire after seven days, but the bucket remains. SecureString parameters and optional DNS records are outside CloudFormation and also remain. Remove those exact reviewed resources separately when no longer needed. No destructive cleanup script is supplied.

For a shared installation, retire the disabled `STACK_NAME-public-edge` stack in `us-east-1` first, including its CloudFront/VPC origin and WAF resources, then the `STACK_NAME-public-access` ingress-rule stack in the application region, before deleting the base stack. Review dependencies and disable termination protection only for each exact stack you intend to remove. The separate `/incidentlab/STACK_NAME/public` parameter and your owner-only local credentials file also require explicit cleanup. Never delete a shared VPC, subnet or AWS-managed CloudFront security group as part of laboratory cleanup.

## Offline verification of these scripts

```sh
npm --prefix tools ci
npm --prefix tools test
bash -n scripts/lab.sh infra/aws/install-node.sh infra/aws/verify-node.sh infra/aws/install-public.sh infra/aws/verify-public.sh
```

The Node test suite uses a fake AWS client to check routing, refusal cases, secret preservation and command construction. It also checks template references and security invariants, dotenv conversion with real jq, release archives with real Git, and owned-process shutdown. These are focused checks, not a full CloudFormation resource-schema validator; `aws preflight` calls AWS `validate-template` before creation. Offline tests neither authenticate to AWS nor create resources. A successful offline suite is not an EC2 installation result.

The separate [October 10 AWS acceptance record](../../docs/verification.md#aws-reference-deployment--october-10-2026) covers bootstrap/egress, both builds, all eight real scenarios, a bounded OpenSearch outage, service restart and persisted historical data. The restart test also observed dropped target events: the in-memory sender is not a durable queue. Private DNS, live AI, external notifications, backup/restore, long-running load and actual cost/storage growth remain outside that validation.
