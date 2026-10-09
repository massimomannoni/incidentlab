# Laboratory security

This application deliberately injects faults. Use it only on dedicated resources with synthetic data and the account owner's authorization. Do not connect it to production services or third-party targets.

## Trust boundary

The target exposes application endpoints without a login, as required by the demo. The private admin console on port 8010 relies on IAM-controlled SSM/network access; anyone who can reach that port has administrative access. Both EC2 instances are trusted. Optional [public sharing](docs/public-access.md) uses a separate gateway on port 8020 behind CloudFront/WAF, with CAPTCHA, a shared read-only login and absolute one-hour sessions. An optional separate personal master can operate bounded tests, but public AI configuration is forbidden. This is not a general identity platform and does not expose port 8010.

Fault controls and telemetry ingestion require separate keys generated for each installation. These protect against accidental calls; HTTP on a private network does not provide end-to-end cryptographic confidentiality. Do not use real credentials or sensitive data in requests. Connecting to less trusted networks would require TLS/mTLS, source identities, and console authentication and RBAC.

The OpenSearch security plugin is disabled **only within the unexposed Docker network**. PostgreSQL publishes no host ports. Do not add `0.0.0.0/0` ingress, public load balancers or host port mappings for the data stores. Target IPs are operator configuration, not GUI input; the tester does not accept arbitrary URLs.

The backend rejects cross-origin browser mutations and limits batch sizes. This does not replace authentication or DoS protection. Never expose the raw backend to the Internet. The optional public gateway adds antiforgery checks, route allowlists, body limits, secure cookies and rate limiting; WAF CAPTCHA is a separate required layer, not a substitute for the login.

Use the [test-deployment checklist](docs/test-deployment.md) before sharing. The shared account is read-only. Personal master credentials authorize only simulation start/stop, incident acknowledgement and already-enabled AI analysis; settings, worker and ingest endpoints stay private. Roles come from server-validated credentials and session claims, never request headers or form roles. CAPTCHA immunity lasts five minutes; authenticated sessions last one absolute hour. CloudFront does not cache authenticated content, and origin access requires both the private security-group path and a separate secret. Preserve `Referrer-Policy: same-origin` so native login/logout forms satisfy the exact-origin checks; do not accept `Origin: null` as a workaround. HTTPS terminates at CloudFront; the private origin leg is HTTP.

## Cost exposure

Neither authentication nor throttling is an AWS account-wide spending cap. The gateway limits origin work to 600 requests per fixed minute, eight concurrent requests and 2 MiB per Monitor response, with a 15-second response deadline. These process-local controls include anonymous requests after origin validation, but cannot undo edge request/CAPTCHA charges. They reset on restart, are not distributed quotas, and can reduce availability for legitimate guests during an attack.

The Bedrock pre-call ledger covers only this app's model calls. It does not cover EC2, EBS, CloudFront, WAF, taxes or another workload. AWS Budgets and cost-anomaly alerts are additional, delayed safeguards, not hard stops. No billing alert or automated cloud shutdown is installed by this project. The [public cost guide](docs/public-access.md#costs-and-limits) explains the recurring baseline, residual public-traffic risk and private-access alternative.

Use a dedicated sandbox account and MFA-protected, least-privilege operator access. Do not deploy application root credentials, broaden the runtime role to account administration, or apply a whole-account shutdown policy to a shared company account. The single Monitor enforces 10 started simulations per UTC day from persisted run records, including master/private starts and failed or stopped runs. Preserve that history; database deletion/restoration changes quota accounting. General guest tester identity and per-person admission remain prerequisites for guest write access; a shared password or IP address cannot establish one person. Never share personal master credentials as a workaround.

## Credentials and data

Do not commit `.env`, logs, databases, SSM output containing secrets or company resource details. Image build contexts exclude `.env`. Compose secrets are visible to the host's Docker administrators; the POC does not yet integrate Secrets Manager. Request logs exclude bodies, query strings and authorization headers. Command signatures are not an anonymization mechanism for sensitive data.

The Codex worker is optional and must be explicitly enabled. It sends a bounded laboratory data bundle to OpenAI: do not use company logs without authorization to send them to that destination. The login stays on the Mac. `LAB_WORKER_KEY` is a separate key between monitor and worker, not an OpenAI credential. The worker rejects remote HTTP and redirects; use TLS or an SSM tunnel over loopback.

The optional [Bedrock provider](docs/bedrock.md) instead uses EC2 instance-profile credentials and the Milan endpoint with EU cross-region processing. Corporate approval must cover those destinations. The preparation policy allows only Nova Lite through that profile and only Monitor-origin instance credentials. IMDSv2 remains required; the Monitor hop limit becomes 2 for Docker. Monitor containers and host administrators remain trusted. No AWS static keys or OpenAI API keys are required. Budget admission is scoped to this application's persisted ledger, not an account-wide billing guarantee.

Invocations use separate temporary directories, a read-only sandbox and isolated CLI configuration. Shell, browser, user-configured MCP servers, plugins, apps and subagents are unavailable in this execution path. Managed workstation policies still apply. AWS credentials, laboratory keys and API keys are not passed to the Codex process environment. Free-form logs, IP addresses, paths/queries and command contents are excluded from the bundle; the remaining fields and reports are still untrusted data. React escapes them, and the backend validates schemas and evidence references.

The Manager proposes actions but does not send messages directly. A policy requires human review for high/critical incidents and allows only the internal inbox; it permits no service or network changes. Worker keys are trusted and allow report submission: the POC provides no worker attestation. There are no AI tools for autonomously querying other sources, no per-user authentication and no distributed guarantees. Stop requests take effect at the next heartbeat and cannot undo usage already consumed.

The optional Judge does not replace deterministic policy. Disagreement replaces the Manager's draft with a generic review request; a Judge failure prevents AI delivery. Per-role configuration is frozen in the job and validated against the worker catalog. The catalog does not attest to model access; the ledger records requested settings and reported counters, not billing guarantees. Exported JSON reports contain incident and session IDs: treat them as laboratory data, without credentials or private reasoning.

## AWS installation and secrets

The AWS installer stores configuration in two Parameter Store `SecureString` parameters, using the AWS-managed `aws/ssm` key, and writes root-only `.env` files on the hosts. Secrets are not included in S3 bundles or Run Command parameters. This does not make the target an untrusted host: both hosts share the EC2 role, and the managed SSM policy includes Parameter Store permissions. Host administrators and authorized IAM operators remain inside the trust boundary. Strong isolation requires separate roles, restricted SSM policies and dedicated KMS keys.

Optional sharing adds a third SecureString, `/incidentlab/STACK_NAME/public`, and the gateway's root-only `.env.public`. AWS receives a salted password hash, not the shared plaintext password. The latter is generated once in the preparing checkout's ignored, owner-only `.local/shared-login.json`; preserve it privately, including when preparing from CloudShell. Never publish it with the URL. Restarting the gateway revokes all sessions; individual guest revocation is not available. See the [public access runbook](docs/public-access.md) for disable/rotation and safe troubleshooting.

The deployment bucket is private, blocks public access and requires HTTPS; bundles expire after seven days. Only committed source files from build directories are included. Review the contents before deployment: filename filters are not a secret scanner. Optional DNS accepts only private hosted zones associated with the VPC and does not overwrite existing records. It does not buy domains, create HTTPS endpoints or authorize public access.

## Before sharing

Review dependencies, images and organizational policies. Pinned tags/versions help reproducibility but do not guarantee the absence of vulnerabilities. Images are not yet pinned by digest. Do not publish real incident details in the repository. IncidentLab is released under the [MIT license](LICENSE); dependencies retain their respective licenses.
