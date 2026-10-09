# Optional public sharing: HTTPS, CAPTCHA and a one-hour session

Public sharing is an explicit opt-in extension to the private AWS lab. It does not make the existing Monitor API, Target, PostgreSQL or OpenSearch publicly reachable. This is a small, synthetic-data demo, not an enterprise identity platform.

For a first installation, start with the [test-deployment walkthrough](test-deployment.md). Install and verify the private lab before creating this extension. The owner keeps full access through SSM; the default public account cannot run experiments or change AI settings.

```text
Guest browser (HTTPS)
  → CloudFront + AWS WAF (rate limits, managed rules, CAPTCHA at /login)
  → private VPC origin, TCP 8020
  → ASP.NET Core Gateway (shared login, session, CSRF, route allowlist)
  → Monitor container → existing laboratory

Owner → IAM-controlled SSM tunnel → private Monitor, TCP 8010
```

The application stays in the configured region, including Milan. CloudFront is global; its WAF/edge CloudFormation stack is created in `us-east-1`. An AWS-generated `https://…cloudfront.net` hostname and certificate avoid buying a domain. The gateway accepts HTTPS viewer traffic only. The CloudFront-to-gateway leg uses HTTP inside the private VPC connection; this is **not end-to-end TLS**. No public EC2 ingress or database port is added. The origin checks a separate random secret, even on the trusted network.

## Access and session behavior

- `viewer` is the only shared role: authenticated read-only dashboard and API access. Mutation endpoints return 403. The UI explains the restriction; the server enforces it independently.
- The optional **personal master** has separate credentials. It can start/stop bounded experiments, acknowledge incidents and request AI analysis only when already enabled. It cannot edit AI settings, providers or budgets, or access ingest/worker routes. It is an application account, not an AWS identity. Never share it with visitors.
- The authenticated **personal master has no daily simulation cap**. Other callers, including the private console, retain **10 starts per UTC day**, including stopped, failed or interrupted starts. Newly admitted master runs are recorded as exempt and do not consume that allowance; historical runs without a recorded exemption remain counted. `/api/limits` returns `unlimited: true` with null limit/remaining/reset fields only for authenticated master admission. The Scenario tester explains this. Duration/traffic bounds, one running experiment, gateway throttling and all AI quotas/budgets still apply to master. Keep a single Monitor process; multiple replicas are unsupported.
- Shared `admin` mode is forbidden. Only the private SSM console can change AI settings. General tester identities and the requested one-simulation-per-person quota are not implemented; shared visitors remain read-only. A browser cookie or IP is not a reliable person identifier.
- The visitor password is shared, without individual identities or MFA. Share it only with trusted recipients. There is no individual guest accountability, account recovery or per-guest revocation.
- Passwords use PBKDF2-SHA256 with a random 128-bit salt and 600,000 iterations. AWS stores the hash, not the plaintext password. The generated password has 192 bits of randomness.
- Server-side sessions last **60 minutes from login**, with no sliding renewal. Cookies are `Secure`, `HttpOnly`, `SameSite=Strict`, host-only, with path `/`. Logout revokes the server-side ticket, including a copied cookie. Restarting the single gateway process revokes every session. The store is bounded to 1,024 tickets and is intentionally not distributed.
- Login and authenticated mutations require ASP.NET antiforgery tokens and an exact same-origin header. Only explicitly allowed application routes are proxied. Credentials, browser cookies and worker/ingest keys are not forwarded to the Monitor.
- Responses use `Referrer-Policy: same-origin`: HTML login/logout forms retain their real `Origin`, while external destinations receive no referrer. Do not change this to `no-referrer`: browsers send `Origin: null` on those form submissions and the gateway correctly rejects them with 403. Missing, opaque (`null`) and foreign origins remain forbidden. See the [Fetch standard](https://fetch.spec.whatwg.org/#append-a-request-origin-header).
- AWS WAF checks CAPTCHA on **both GET and POST `/login`**. The browser solves the interstitial on GET; POST requires a valid WAF token. CAPTCHA immunity is five minutes, independent of the one-hour login session. A gateway check also requires WAF's verified header. A preceding WAF rule blocks user-supplied `x-amzn-waf-*` headers, and CloudFront overrides the origin secret header. Never remove these checks or test public access using a bypass.
- WAF limits login traffic to 30 requests per five minutes per IP and total traffic to 3,000 requests per five minutes per IP. These AWS rate limits are approximate, not exact quotas. The gateway separately permits ten login submissions per minute across the shared service. Shared NAT users can affect one another.
- The gateway also admits at most 600 requests per fixed minute and eight concurrent requests across all clients, with no queue. Excess requests return 429 and `Retry-After: 60`. This includes anonymous requests after origin validation and is independent of IP/cookie headers. The counters are process-local and reset on restart; they protect origin capacity, not the AWS bill. Busy guests share the allowance. Monitor responses have a 15-second deadline and a 2 MiB ceiling, including chunked bodies; oversized responses fail with 502 rather than sending partial data. Large reports remain available privately.
- All CloudFront caching is disabled; authenticated responses use `no-store`. WAF request sampling is disabled to avoid capturing login bodies. Full request logging is not enabled. Operational gateway logs exclude passwords, cookies, CSRF tokens and request bodies.

## Install and enable

First complete the [private AWS installation](../infra/aws/README.md). Use the same approved account, source revision and `lab.config.json`. Required permissions now also include CloudFront/VPC origins, WAFv2 in `us-east-1`, their service-linked roles, the additional CloudFormation stacks, and the narrow EC2 security-group ingress rule. Review corporate policy before granting these permissions. Existing VPC egress and private-origin prerequisites must be satisfied; do not modify a shared subnet or routing table to bypass a failed prerequisite.

Review AWS's [VPC origin prerequisites](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-vpc-origins.html): an attached internet gateway is required by the service, even though it is not the route used to the private origin; the subnet needs an available private IPv4 address for the managed interface. Have the network owner verify routing and network ACL compatibility. The installer does not create this network infrastructure or edit AWS's managed security group. Milan is supported; the edge stack still uses `us-east-1` automatically. The commands below use a workstation profile; **omit `--profile YOUR_APPROVED_PROFILE` in CloudShell**, which uses its console identity.

```sh
npm --prefix tools ci
npm --prefix tools run build

# Creates a DISABLED distribution, WAF and private VPC origin.
bash scripts/lab.sh aws public-prepare --apply --access viewer --profile YOUR_APPROVED_PROFILE
bash scripts/lab.sh aws public-status --profile YOUR_APPROVED_PROFILE

# Wait for CREATE_COMPLETE, then install the gateway and updated Monitor UI.
# Commit reviewed source first: installation archives HEAD, not the working tree.
bash scripts/lab.sh aws public-install --apply --profile YOUR_APPROVED_PROFILE

# Only after successful gateway checks: make the protected URL reachable.
bash scripts/lab.sh aws public-enable --apply --profile YOUR_APPROVED_PROFILE
bash scripts/lab.sh aws public-status --profile YOUR_APPROVED_PROFILE
```

Only `--access viewer` is accepted. Preparation does not silently change an existing access mode, reset a password or replace a stack. Older shared-admin installations must first disable public access and have the owner change only `PUBLIC_ACCESS` to `viewer` in the public SecureString, preserving all other fields; otherwise the new installer refuses deployment. Preparation creates `.local/shared-login.json` once, with owner-only file permissions and Git exclusion. Retrieve the password from that local file and share it separately from the URL. **Do not paste it into GitHub, Medium, screenshots or command output.** AWS configuration is encrypted in `/incidentlab/STACK_NAME/public`. CloudFront configuration administrators can read the separate origin secret; they are inside the trust boundary.

The credentials file is on the machine that ran `public-prepare`. For CloudShell, preserve it from that checkout using an approved private transfer/password manager; it will not appear automatically on the Mac. A second checkout or a lost file cannot recover the plaintext password from AWS. Do not delete the SecureString to regenerate it. `public-status` returns `PublicUrl`, `PublicHost` and `DistributionId`; use that generated URL, not the example installation's hostname. No custom-domain registration, CloudFront alias or ACM certificate workflow is implemented.

Public creation may take 15 minutes or more. The `STACK_NAME-public-edge` stack lives in `us-east-1`; `STACK_NAME-public-access` lives in the application region. The latter permits only the AWS-created `CloudFront-VPCOrigins-Service-SG` to reach port 8020. The script checks the exact VPC, instance and security-group references. It does not edit the AWS-managed group. The application still uses the existing instance role and Docker private network, so host/IAM administrators and the two lab hosts remain trusted.

`public-install` builds the updated Monitor and gateway on the Monitor EC2, preserving database volumes. It does not rebuild the Target. The gateway has its own Compose project, memory/CPU limits, read-only filesystem and a root-only `.env.public` file. Its startup diagnostics assert that direct requests without an origin secret fail, unauthenticated API calls fail, login without CAPTCHA proof fails, and browser navigation redirects to login. They do not solve a CAPTCHA or create a public session. A human should perform the final browser CAPTCHA/login check.

For later application updates, deploy a reviewed clean commit with `public-install` again. It does **not** disable an already enabled distribution while installing; schedule a short access interruption or explicitly disable first. A restarted gateway invalidates existing sessions and form tokens, so reload `/login`. `public-prepare` leaves an existing edge stack unchanged: template/network changes require a separately reviewed CloudFormation update, not deletion and recreation.

## Personal master setup

After preparing shared access, optionally create one separate operator account:

```sh
bash scripts/lab.sh aws public-master-prepare --apply --profile YOUR_APPROVED_PROFILE
bash scripts/lab.sh aws public-install --apply --profile YOUR_APPROVED_PROFILE
```

Omit the profile in CloudShell. Preparation stores the generated `master` credentials in `.local/master-login.json` on the preparing machine, with owner-only permissions and Git exclusion. Preserve the file in your private password manager. The encrypted public configuration receives only the username, salt and password hash; the visitor password and origin secret are preserved. No IAM user, role or new cloud resource is created. Preparation alone does not update the running gateway: installation is required and revokes existing sessions.

Rerunning preparation does not rotate an existing master. If an AWS write was interrupted, it reuses the same local secret; a local file belonging to another account/stack is rejected. A lost plaintext password cannot be recovered from its hash. Credential rotation is a separate, deliberate operator procedure, not automatic recovery.

Use the same HTTPS URL and complete CAPTCHA normally, then sign in with the personal credentials. The top bar should show **Personal master**, and the Scenario tester should show **no daily simulation limit**. The master retains the same absolute one-hour session, CSRF/origin checks and request limits as visitors. AI admission and monetary limits are never bypassed. Changing AI configuration still requires the private SSM tunnel.

During `public-install`, the installer derives a domain-separated simulation-admission credential from the existing Monitor control key and stores it in the encrypted public configuration. The gateway issues this credential server-to-server only for an authenticated master's run admission and limit read; it never forwards browser-supplied admission headers or exposes the credential in session data. The Monitor validates it before applying the exemption. It grants no AI, ingestion, fault-control or settings privileges. Reinstall Monitor and gateway together through `public-install` when upgrading; no password rotation, IAM change or database migration is needed. Missing/invalid admission credentials fail closed to the ordinary daily limit.

## Browser acceptance and troubleshooting

After `public-status` reports the completed enable update, allow CloudFront propagation and perform these checks using the generated HTTPS hostname:

1. In a signed-out browser, open `/login`, complete AWS CAPTCHA and enter the generated credentials. Do not submit credentials from scripts or try to bypass CAPTCHA.
2. Confirm the real dashboard opens in **read-only** mode. Viewer controls for experiments and settings are disabled; the gateway also rejects mutation endpoints.
3. Log out and confirm that protected pages require login again. A fresh login expires 60 minutes after issuance, even during activity; use that separate timed check before claiming browser expiry acceptance.
4. With no cookies, `curl -i https://YOUR_CLOUDFRONT_HOST/api/overview` should return **401** and `X-IncidentLab-Login: required`. `curl -i -H 'Accept: text/html' https://YOUR_CLOUDFRONT_HOST/login` normally returns the WAF **405 CAPTCHA** interstitial, not an application error. These probes do not establish an authenticated session.
5. If master is configured, sign in separately and confirm the role, the **no daily simulation limit** label and disabled AI configuration controls. Run one bounded experiment when the Target is idle; verify real events and recovery. Master admission must still refuse concurrent or oversized experiments and retain AI budgets. Visitor sessions remain read-only; their limits response must not claim unlimited access. Signing out does not erase run history or reset any AI quota.

| Symptom | Check and safe next step |
|---|---|
| Login form returns 403 after an update | Reload `/login` before retrying, then complete CAPTCHA again if requested. Old pages have stale antiforgery tokens. Ensure the response policy is `same-origin`, not `no-referrer`; the latter caused the corrected native-form regression. |
| Invalid credentials message | Use the original preparing machine's `.local/shared-login.json`; do not reset unrelated AWS or database credentials. |
| CAPTCHA reappears or submission is rejected after a delay | Its five-minute immunity is separate from the login session. Reload and solve it before resubmitting. Allow required first-party cookies and scripts. |
| 429 or repeated WAF blocks | Stop repeated attempts, wait for rate-limit windows, then retry once. Inspect WAF metrics and sanitized gateway logs; do not remove security rules to make a test pass. |
| 502/503 or no dashboard | Check EC2/SSM state, gateway and Monitor containers, the VPC origin and its exact security-group rule. An enabled distribution alone does not mean the app is healthy. |
| Worker or smoke test fails through the public URL | Use the private SSM tunnel. Worker/ingest routes are always blocked publicly; viewer credentials do not authorize experiments. |

Through an authorized SSM session on the Monitor, inspect the gateway without printing secrets:

```sh
cd /opt/incidentlab/public-current
sudo docker compose --env-file .env.public -f compose.public.yml ps
sudo docker compose --env-file .env.public -f compose.public.yml logs --tail 100 gateway
```

Record results separately from automated checks in your private acceptance notes. Do not upload browser network captures containing passwords, cookies, WAF tokens or CSRF values. The [reference verification record](verification.md#browser-form-regression) documents the fixed 403 cause and the manual check still pending for that installation.

## Disable and revoke

```sh
# Disables the distribution without deleting the VMs or their data.
bash scripts/lab.sh aws public-disable --apply --profile YOUR_APPROVED_PROFILE
bash scripts/lab.sh aws public-status --profile YOUR_APPROVED_PROFILE
```

Distribution changes propagate; disabling is not an instantaneous kill switch. For immediate session revocation, use an authorized SSM session on the Monitor:

```sh
cd /opt/incidentlab/public-current
sudo docker compose --env-file .env.public -f compose.public.yml restart gateway
```

To remove access immediately rather than just revoke current sessions, **stop** that exact gateway service; the private lab remains available. For a password rotation, first disable/stop public access, generate a new strong password/salt/hash through an approved secret-handling tool, update only the public SecureString, and reinstall/restart the gateway before re-enabling. No self-service password recovery or automatic rotation command is implemented. Never print the decrypted parameter or unredacted Compose configuration.

Disabling does not delete the WAF or stop its fixed charges, and it does not stop the EC2 instances. Follow the [shutdown and removal guide](../infra/aws/README.md#8-operations-cost-control-and-removal) to pause compute or review deletion of all three stacks and retained resources.

## Costs and limits

**The public URL has no account-wide spending cap.** Authentication prevents unauthorized application work, but requests may incur CloudFront/WAF/CAPTCHA charges before the gateway rejects them. Distributed clients can exceed an IP-based allowance in aggregate. Application throttling, one-hour sessions and the separate Bedrock ledger do not cap those edge charges. AWS Budgets updates typically occur 8–12 hours apart, so alerts or budget actions cannot guarantee an immediate stop. See [AWS Budgets timing](https://docs.aws.amazon.com/cost-management/latest/userguide/budgets-managing-costs.html) and [WAF rate-limit caveats](https://docs.aws.amazon.com/waf/latest/developerguide/waf-rule-statement-type-rate-based-caveats.html).

For a low-risk laboratory, keep access private until the account owner has approved the recurring baseline, variable exposure and an incident shutdown procedure. Use a dedicated sandbox AWS account, an approved least-privilege operator role and MFA; never give the application root credentials or publish AWS keys. Budget alerts and cost-anomaly detection should cover the laboratory without altering unrelated company workloads. Choose recipients and thresholds with the account owner before configuring them; the scripts do not create these controls. An automated response must target only the lab's exact resources and IAM role, not the whole company account.

AWS also offers flat-rate CloudFront plans without overage charges for their covered services. They are not enabled by this template and do not include EC2/EBS or Bedrock. As checked October 10, 2026, the private VPC origin used here requires the Business plan or above (Business: USD 200/month), not the Free or Pro plan. Review feature compatibility and pricing before any migration; the sample does not subscribe or change plans. [CloudFront plan comparison](https://aws.amazon.com/cloudfront/pricing/).

The reference WAF configuration has one ACL and five rules: approximately **USD 10/month**, plus request charges and CAPTCHA attempts. The published reference rates on October 10, 2026 were USD 0.60/million WAF requests and USD 0.40/thousand CAPTCHA attempts. CloudFront requests/transfer and existing EC2/EBS/S3 charges are separate; free allowances depend on the account. The private lab's prior USD 101.22 base estimate therefore becomes approximately USD 111.22 before tax and variable usage. This is not a spending cap: unsolicited public traffic can incur costs. No budget alarm or automatic shutdown is created.

Sources: [WAF pricing](https://aws.amazon.com/waf/pricing/), [CloudFront pricing](https://aws.amazon.com/cloudfront/pricing/), [VPC origin prerequisites](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-vpc-origins.html), [CAPTCHA best practices](https://docs.aws.amazon.com/waf/latest/developerguide/waf-captcha-and-challenge-best-practices.html), [custom WAF headers](https://docs.aws.amazon.com/waf/latest/developerguide/customizing-the-incoming-request.html).

This layer does not fix the lab's existing non-durable Target telemetry queue, implement external notifications, enable AI, provide high availability, or make synthetic experiments safe for production targets. A broader deployment needs individual identity, proper RBAC, stronger host/secret isolation, dependency review, durable queues, backups and operational cost controls.
