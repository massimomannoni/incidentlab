# Verification record

This record separates the original local checks from the subsequent AWS deployment. The October 9 limitations below describe that test environment; see the October 10 record for real AWS results.

To repeat the deployment in your own approved account, follow the [test-deployment walkthrough](test-deployment.md), [detailed AWS guide](../infra/aws/README.md) and optional [public browser acceptance checklist](public-access.md#browser-acceptance-and-troubleshooting). Results below describe the reference environment, not acceptance of a new installation. Document-only updates do not rerun cloud experiments or AI calls.

## Diagnostic investigation — October 11, 2026

**159 C# tests and 56 TypeScript tests passed**, plus the frontend production build. New checks cover scoped before/during aggregates, sparse telemetry, redaction, capacity limits, immutable evidence, one optional diagnostic follow-up, three/four-call accounting with Judge off/on, live budget reduction, unseen/invented references, final no-more-check semantics, sanitized validation errors and legacy report compatibility. Provider outputs in these tests are controlled fixtures: these results establish orchestration and safety behavior, not real model diagnostic quality.

New investigations receive measured context and must distinguish likely causes from insufficient evidence, explain impact, compare hypotheses and propose actions with success checks. They can request at most one bounded server-owned read of the captured telemetry. No arbitrary tool execution, live queries, additional IAM authority or automated remediation is introduced. Existing reports and usage are retained unchanged.

Monitor and gateway were deployed on the existing Milan hosts from bundle `a56085a677845b860b6535c6418d67f0a7bd35e9540ff2b22efc6d03331adf18`, following a private PostgreSQL backup and archive-list check. Target and database volumes were retained. The real ingest → PostgreSQL outbox → OpenSearch → analyzer check passed. CloudFront remained enabled with its existing WAF; anonymous API access returned 401 and trusted-origin checks passed. Browser review used the private owner tunnel, not a fresh public human CAPTCHA/login.

Four normal/CPU pairs completed **2,017 real HTTP requests and responses**, with no HTTP/transport errors and confirmed recovery. This repeated those two scenarios, not the entire eight-scenario suite. Four separate incident investigations used **10 calls / 31,600 reported tokens**, totaling **USD 0.0048088** in conservative application accounting. The first completed structurally but remained too generic. The second selected an endpoint check but its final report failed validation; no Manager delivery followed. These records remain in the audit. Trusted instructions were separated from the untrusted evidence envelope, numeric comparison requirements strengthened, and final “no further check” semantics corrected. No failed provider response text was retained, so that second failure's precise validation cause is not retrospectively established.

The last two investigations completed three calls each (Investigator, one follow-up, Manager). In the final CPU case, measured mean process CPU rose **14.14% → 99.96%**, traffic **3 → 7.5 requests/s**, and response p95 **206.27 → 240.87 ms**, with no HTTP 5xx. The selected check showed **145 of 150** incident-window requests were successful `POST /compute` requests, versus 64 of 180 in the preceding 60 seconds. That comparison period already included some compute traffic; it is not a guaranteed healthy baseline. The report identified CPU-intensive compute traffic as a likely contributor and proposed concurrency/admission limits. Three sessions reported **8,462 input + 1,788 output = 10,250 tokens**, with **USD 0.0015614** accounting. The Manager requested a high-priority internal notification and human review; policy delivered it to `lab-inbox`. No external message or remediation occurred.

This is a working diagnostic workflow, not proof of reliable root-cause attribution. The last report still suggested repeating an endpoint breakdown already supplied, and its urgency for a resolved warning warrants human review. The preceding successful run chose observation instead. Profiling, causal confirmation and systematic model-quality evaluation remain outside this acceptance check; schema/reference validity alone does not settle them.

The exact original settings were restored and read back: automatic admission on, Judge off, USD 0.50/day, USD 100/month and 10 jobs/day. The laboratory was healthy and idle. Total history contained **18 attempts / 43,010 reported tokens**, including the unchanged earlier unresolved USD 0.0304 reservation. No budget, history or quota was reset; the personal master simulation exemption remains unchanged.

## Personal master simulation exemption — October 11, 2026

The owner requested removal of the personal master's daily simulation cap. Only server-verified master admission is exempt; non-master/private-console starts remain limited to 10 per UTC day. New exempt runs retain their history without consuming that allowance. Existing historical runs are not relabeled. AI budgets and daily job limits, one-at-a-time experiments, traffic/duration bounds, CAPTCHA, one-hour sessions and CSRF enforcement remain unchanged.

**147 C# tests and 56 TypeScript tests passed**, plus the frontend production build and installer syntax check. Tests verify beyond-quota master starts, durable exemption accounting, concurrent/oversized-run rejection, forged payload/header rejection, viewer isolation, narrowly scoped gateway credentials and matching installer/Monitor key derivation. The first sandboxed TypeScript run could not bind local HTTP test ports; rerunning with local socket access passed all 56 tests. No live model was used by these tests.

Monitor and gateway were deployed in Milan from bundle `5ece743828e6eec46bd11d3541cc4d3adaafb86e1a9d1c9caa9d124194fbadc5`, after a private PostgreSQL backup. Live private diagnostics using the installed server-to-server credential confirmed unlimited master admission and a 10-start non-master cap. One normal-traffic master run completed **30 real requests / 30 responses**, with confirmed recovery and a persisted exemption; the ordinary allowance was unchanged. AI was paused for the rollout/test, then its exact previous settings were restored. Counters remained **6 attempts / 8,764 reported tokens**. The real OpenSearch pipeline check passed. Anonymous public limits returned 401 and login returned the 405 CAPTCHA gate; trusted-origin checks passed without issuing a session. Fresh public human CAPTCHA/login remains a manual check after the gateway restart.

## Incident-first investigation workspace — October 11, 2026

The updated UI keeps the incident identity, full event date/time and timezone, type, service and current status above a real telemetry chart. Five tabs expose the AI report, recorded activity, frozen evidence, notification delivery and usage for the same incident. Historical packets are preserved; absent exact window bounds are labeled estimated. Newly tagged requests can link runs to detector evidence without inferring correlation from time overlap.

**143 C# tests and 55 TypeScript tool tests passed**, with successful frontend production and Target builds. Added checks cover frozen windows across incident updates, measured counts, missing buckets, resource instance isolation, unchanged legacy packets, explicit run correlation, availability events, invalid run IDs and fail-closed query limits.

A fresh local SQLite laboratory with AI disabled completed a real HTTP probing experiment: **178 requests, 178 responses, 119 HTTP errors, zero transport errors**, the expected `http-probing` incident and confirmed recovery. Sampled response IDs matched server telemetry; no model calls or tokens were added. Browser inspection confirmed the new incident header, chart, run link and distinct opening/recovery notices. This local check is not a new live Bedrock acceptance test.

Monitor, Target and the public gateway were deployed on the existing Milan hosts from bundle `5e719648609e75311b958a6f49d2e463bfc77fcf60b838e1d3595fb80866585e`, after a private PostgreSQL dump whose archive list was verified. Existing volumes and credentials were retained; this is not a restore rehearsal. The real Target → authenticated ingest → PostgreSQL outbox → OpenSearch → analyzer check passed, with a green search cluster and live telemetry.

The AWS-backed browser workspace displayed the existing completed HTTP-probing report, its estimated legacy window, eight frozen evidence events, recorded agent timeline, actual internal escalation/recovery notices and two sessions totaling **4,221 reported tokens**. The incident-filtered usage report matched. No new cloud simulation or inference was started. Global counts remained **6 attempts / 8,764 reported tokens**, and the exact previous AI settings were restored after the rollout pause, including automatic admission, USD 100/month, USD 0.50/day, 10 jobs/day and Judge off. Re-enabling automatic admission starts from the new activation time; it does not backfill restart incidents.

CloudFront remained enabled/deployed with WAF attached. Anonymous incident API access returned 401, and `/login` returned the WAF 405 CAPTCHA gate. Trusted-origin checks confirmed secret/proof enforcement without issuing a session. UI validation used the private owner tunnel; a new public human CAPTCHA/login was not completed in this rollout.

## Approved monthly AI ceiling — October 11, 2026

The owner approved **USD 100/month**, not an EUR-denominated billing guarantee. The Monitor SecureString ceiling and private application setting were both updated; live API and GUI readback confirmed **USD 100/month, USD 0.50/day, 10 AI jobs/day, automatic admission enabled and Judge off**. The daily limit remains more restrictive. New installations retain the USD 5/month default with AI off.

**134 C# tests and 55 TypeScript tool tests passed**, and the frontend production build passed. Added cases cover the USD 100 ceiling, both budget-exhaustion boundaries, retained unresolved reservations, unchanged daily limits, explicit CLI write authorization and preservation of secrets/zero budgets. Local test fixtures made no provider calls. Two initial TypeScript tests could not bind loopback ports under the sandbox; all 55 passed when rerun with local socket permission.

Only Monitor was rebuilt, from source bundle `5914c63d2c3ae9b6c8e19388a3cc64affaf84d428dfcb8ae779a4c7f9b76f7df`. AI was paused during deployment and restored after verification; automatic admission resumes for new incidents. Target, gateway, IAM, credentials and data volumes were unchanged. Real telemetry passed the Target → ingest → PostgreSQL outbox → OpenSearch → analyzer check, with a green search cluster. Anonymous public AI settings still returned HTTP 401.

Usage remained at four invocation attempts and 4,543 reported tokens: USD 0.0007345 usage-based conservative accounting plus USD 0.0304 unresolved reservation. The effective monthly remainder was USD 99.9688655. No new simulation or inference call was made for this update, and no history or quota was reset. These are application ledger values, not an account-wide AWS cap; taxes, currency conversion and infrastructure are outside them. Earlier dated records preserve their original settings.

## Bedrock live acceptance October 10 2026

After explicit approval for EU inference and model charges, the optional IAM stack was created and the Monitor configured for Nova Lite. Permissions remain limited to the exact Monitor instance credentials, EU inference profile and five allowed model destinations. No full-access policy or operator credentials were installed. IMDSv2 remains required; only the Monitor's response hop limit changed to 2. The gateway and Target were not restarted during this activation.

The real acceptance investigation used three retained process-CPU measurements from an already-resolved incident. The Investigator returned validated evidence references and a resource-pressure report; the Manager proposed a warning notification. Server policy accepted it, required human review and delivered to `lab-inbox`. No new simulation was started, no daily allowance was reset and no external message was sent.

| Stage | Reported input tokens | Reported output tokens | Result |
|---|---:|---:|---|
| Investigator | 888 | 293 | Evidence-backed report accepted |
| Manager | 1,131 | 162 | Notification accepted and delivered internally |
| Successful job total | 2,019 | 455 | 2,474 tokens; USD 0.0003839 conservative accounting |

Two preceding attempts failed and remain visible. The first returned a generic Bedrock request error without usage counters; USD 0.0304 remains conservatively reserved. The second reported 1,590 input and 479 output tokens, but its output was rejected before publication or Manager execution. Neither was automatically retried. The adapter now categorizes known AWS errors without publishing raw provider messages and accepts one complete JSON Markdown fence while retaining strict schema and evidence checks. The failed response was not retained, so its exact formatting cause is not established.

At the acceptance checkpoint the ledger contained four invocation attempts, 4,543 reported tokens, USD 0.0007345 in usage-based conservative accounting and USD 0.0304 unresolved reservation. These values are not an AWS invoice and do not include infrastructure or tax. **130 C# tests passed**, including safe error categorization, strict fenced-report parsing, budget accounting and existing access controls. The updated Monitor built and started on EC2 from source bundle `05e8597a7d51d649d329a498b3ce4d73b9de8c3063b923fe9a2a88338d97afee`.

The live GUI showed the completed investigation, both agent sessions, token counters and delivered inbox notification. Automatic admission was then enabled for **new incidents only**, with **10 jobs per UTC day, USD 0.50/day, USD 5/month, 1,000 output tokens per call and Judge off**. Its settings were read back; a fresh automatically admitted incident was not deliberately generated in this rollout. The user confirmed master sign-in; full expiry/logout acceptance remains separate. Slack, Telegram, email and general guest tester identities remain unimplemented. Earlier dated checkpoints below preserve their original scope.

## Personal master and simulation limits — October 10, 2026

The separate personal master role permits simulation start/stop, incident acknowledgement and already-enabled AI analysis, while retaining the ban on public AI settings, ingestion and worker access. Shared visitor credentials remain read-only. All callers share 10 simulation starts per UTC day; persisted run history includes stopped, failed and interrupted starts. The Scenario tester reads the remaining allowance from `/api/limits`.

**121 C# tests passed**, including credential-role separation, master restrictions, CAPTCHA/CSRF/origin enforcement, expiry, durable simulation counts and concurrent admission of the last slot. **50 TypeScript tests passed**, including private master credential creation, interrupted preparation recovery, visitor preservation and the actual installer dotenv filter. The frontend production build and installer Bash syntax check passed. These checks use isolated local fixtures, not fabricated dashboard data or live paid inference.

The updated Monitor and gateway were built and installed on the existing Milan Monitor after a private PostgreSQL backup. Persistent volumes, visitor credentials and network rules were preserved. The personal master was prepared with an owner-only local credential file; its hash matches the encrypted AWS configuration. No AWS identity or new infrastructure stack was created. The deployed source bundle is `d7fb37c73bd1a148378f72860a699a2944d7a9892bfc4bb3bade048662c2a328`.

Post-deployment verification passed the real Target → authenticated ingest → PostgreSQL outbox → OpenSearch → analyzer path, with live telemetry and a green search cluster. Anonymous public `/api/limits` returned 401 and `/login` returned the WAF 405 CAPTCHA interstitial. Trusted-origin diagnostics rejected requests without the origin secret or CAPTCHA proof and issued no session. Read-only AWS checks confirmed no public IPv4/IPv6 ingress on either host's security group, CloudFront deployed/enabled with WAF attached, and unchanged metadata hop limits.

The live private browser showed the real run history and **0 of 10** starts remaining: 14 runs already existed for that UTC day before the new limit was deployed. Historical starts were not erased or refunded. The next reset is midnight UTC. No new experiment or model call was made during this rollout; AI remained disabled with zero recorded invocation attempts. Human CAPTCHA/master-login acceptance is still pending. Shared guest experimentation and its per-person identity/quota remain unimplemented. Earlier records below describe their own revisions.

## Public cost-risk hardening — October 10, 2026

The gateway now rejects public administrator configuration and all shared write routes, including settings and manual AI admission. It adds global origin-work limits (600 requests per fixed minute, eight concurrent requests, no queue), a 15-second upstream/response deadline and a 2 MiB response ceiling. The browser enables owner controls only after the private endpoint's expected 404; failed session discovery does not grant operator controls. Preparation and host installation also refuse shared-admin mode.

**112 C# tests passed**, including 20 gateway cases and the existing Bedrock budget tests. Gateway cases cover global anonymous throttling, concurrent admission, known/unknown-length oversized responses, privileged-route rejection and session protections. **47 TypeScript tool tests passed**, and the frontend production build and public installer Bash syntax check passed. These are local tests, not proof of a cloud rollout or a billing cap.

Two anonymous requests to the reference public URL returned HTTP 401 for overview and AI settings. AWS CLI inspection required reauthentication, so current IAM, WAF, security groups and billing controls were not re-audited. This hardening has not been deployed to AWS at this checkpoint; the public distribution was not disabled, and no paid inference or new cloud resource was created. The source remains viewer-only, with public tester quotas and billing actions still pending. Earlier records below describe their own revisions.

## Bedrock integration verification — October 10, 2026

The optional C# Bedrock provider, pre-call budget reservations and English settings/usage UI were added after the reference infrastructure deployment. **108 C# tests passed**, including ten Bedrock-specific checks for deployment/model/region gates, reservation-before-dispatch, concurrent budget admission, durable unknown usage, live budget reductions, provider isolation, strict reports and Judge policy. **46 TypeScript tool tests passed**, including six Bedrock preparation/IAM/dotenv checks. The React/TypeScript production build passed. Provider test responses are isolated test fixtures, never dashboard data.

Read-only checks in the approved AWS account found `eu.amazon.nova-lite-v1:0` ACTIVE from Milan with the five documented EU destinations. AWS Price List API returned USD 0.096 input and USD 0.384 output per million tokens for Milan; the application's conservative ledger uses 0.10 and 0.40. AWS `validate-template` accepted the optional IAM template. These checks do **not** prove invocation permission or successful inference.

The local browser check confirmed that the Bedrock mode exposes the Nova Lite EU model, disabled reasoning-effort control, EU processing notice and USD 0.50/day / 5/month budgets. It used a new empty SQLite database with AI off, no target traffic and no model calls; it does not validate live OpenSearch or Bedrock.

At this checkpoint no Bedrock IAM stack, metadata change, application redeployment or paid inference has been performed. Approval for EU processing and a live end-to-end investigation remain pending. The existing public role remains viewer; personal tester identities and daily simulation quotas are not installed. See the [Bedrock runbook](bedrock.md) for activation and budget scope.

## Protected public access — October 10, 2026

The optional public gateway was checked separately from the earlier laboratory acceptance suite. **12 focused C# checks passed** for origin/CAPTCHA requirements, correct and incorrect credentials, antiforgery and same-origin protection, viewer/operator route restrictions, rejection of public worker/ingest routes, secure cookie attributes, absolute one-hour expiry, logout revocation and login throttling. These use an in-process ASP.NET host, controlled WAF proof headers and a stub Monitor response; they do not solve a real CAPTCHA or prove a production browser login.

**Six focused TypeScript checks passed** for explicit write authorization, credential validation, disabled-by-default HTTPS/non-caching CloudFront configuration, the private security-group boundary, secret-free installer arguments and physical resolution of release symlinks. The first activation check correctly refused to continue because the verifier resolved `public-current` logically instead of resolving the release directory; `pwd -P` and a regression check correct this. The gateway and frontend compiled. Hosted GitHub CI remains deferred at the owner's request; the new code does not depend on that service to deploy. The [public-access guide](public-access.md) records installation, session semantics, costs and limitations.

The gateway image subsequently built and started on the real Monitor EC2. Private-origin checks passed through the installed release symlink. After CloudFront activation, an external HTTPS request to `/api/overview` returned **401**, `X-IncidentLab-Login: required` and `Cache-Control: no-store, private`; `/login` returned the real AWS WAF **405 CAPTCHA interstitial**. The browser showed Human Verification. The Monitor security group contained only Target-to-8010 and CloudFront-service-group-to-8020 ingress, with no public IPv4/IPv6 sources. The private analyzer remained healthy with live OpenSearch data and AI disabled.

A human-completed CAPTCHA and browser login still require final confirmation. The in-process session tests above are not a claim that this end-to-end human step has been completed. No CAPTCHA bypass was used.

### Browser form regression

A subsequent Safari report showed 403 responses on `/login` after the CAPTCHA. The gateway's `Referrer-Policy: no-referrer` conflicted with its exact-origin validation: native HTML form POSTs can carry `Origin: null`. A loopback browser reproduction, without credentials or AWS access, confirmed rejection with `no-referrer` and acceptance with `same-origin`. The gateway now uses `same-origin`, retaining the ban on external referrers and all CAPTCHA/CSRF/origin checks. **16 focused gateway checks passed**, including the response policy and rejection of missing, opaque and foreign origins. The earlier in-process tests supplied the Origin header explicitly, so they missed browser-generated provenance. This local reproduction is not a substitute for the final public CAPTCHA/login check.

The corrected gateway was then rebuilt and deployed on the same Monitor EC2. Its private-origin checks passed, and an external CloudFront request returned the new `Referrer-Policy: same-origin` while the anonymous API remained 401. Existing passwords, CAPTCHA rules and access mode were unchanged. Users must reload a previously opened login form before retrying: that document still carries the old policy and an antiforgery token from the restarted process.

## Scope — October 9, 2026

These results cover the C# / ASP.NET Core POC, React/TypeScript console and TypeScript/Bash tools. They distinguish offline tests, real HTTP traffic and integrations that still need validation. They do not demonstrate a working AWS deployment.

## Actual test environment

- Mac, with the .NET monitor and target API running as separate processes on loopback.
- Explicit SQLite storage, labeled `DEV · SQLite` in the GUI: no hidden fallback from OpenSearch and no invented telemetry.
- Node 24.19.0, .NET SDK 8.0.404 and runtime 8.0.11. Use an up-to-date .NET 8 patch for new installations; this workstation does not certify dependency currency.
- Docker was unavailable: container images and real PostgreSQL/OpenSearch services were not run here.
- No AWS resources were created or modified, no external notifications were sent, and no AI inference occurred during these checks.

## Tests and builds

| Check | Result |
|---|---|
| C# suite | **82 tests passed** |
| TypeScript tools suite | **33 tests passed** |
| Locked NuGet restore; .NET solution Debug and Release builds | Passed; Release build without warnings or errors |
| Locked npm install and TypeScript CLI build | Passed |
| TypeScript and Vite console build | Passed |
| Launcher and EC2 script Bash syntax | Checked with `bash -n` |
| CloudFormation | Focused offline checks, not full resource-schema validation |
| GitHub Actions workflow | The initial hosted run was blocked before job execution by an account billing restriction; not a test failure |

C# tests cover contracts/APIs, thresholds, time windows, deduplication and recovery, authenticated/idempotent ingestion, partial outbox delivery, data quality, tester concurrency, the AI queue, policy, Judge behavior, token counters, worker isolation and cancellation. A SQLite fixture verifies reopening data and reports without recreating the database.

TypeScript tests cover CLI parsing, occupied ports, stopping only owned processes, account/VPC/DNS checks, the `--apply` gate, secret preservation, private temporary files, cleanup including cancellation, Git archives, templates and dotenv conversion with jq. The AI test runner's flow and settings restoration are checked using offline responses, not real inference.

AWS and OpenSearch interactions use controlled responses in these tests. Template checks validate references and security invariants, not the complete resource schema. Preflight additionally requires AWS `cloudformation validate-template` before creation.

## Eight real HTTP scenarios

The TypeScript launcher started the C# monitor and target with a new SQLite store and AI disabled. The smoke test ran all eight scenarios, matching sampled response IDs against target telemetry.

| Scenario | Requests / responses | HTTP errors | Expected and detected rule |
|---|---:|---:|---|
| Normal | 177 / 177 | 0 | None |
| Retry | 178 / 178 | 0 | `repeat-command` |
| HTTP probing | 178 / 178 | 118 | `http-probing` |
| Errors | 178 / 178 | 70 | `server-errors` |
| Latency | 147 / 147 | 0 | `slow-responses` |
| Memory | 178 / 178 | 0 | `process-memory` |
| Telemetry gap | 177 / 177 | 0 | `source-stale` |
| CPU | 178 / 178 | 0 | `process-cpu` |

All scenarios confirmed the expected rules and final recovery, without transport errors or cancelled requests. **Zero AI calls and zero tokens.** During the telemetry gap, missing data remained missing: it was neither reconstructed nor displayed as a healthy zero.

Parameters: 30 seconds of traffic, at most 6 requests/s, 3 clients, 5 seconds of warmup and approximately 30 seconds of final settling time. Rate and concurrency are upper bounds: the runner does not catch up with bursts or promise 180 sends. CPU and memory are process measurements, not host-wide measurements.

A second launch on occupied ports was refused without disrupting the laboratory. At completion, the launcher stopped its own processes and retained the database.

## GUI and AI: what has been verified

The GUI was inspected against the C# backend: live data, resolved incidents, settings and zero AI usage. This was a manual desktop check, not an automated browser suite or a full accessibility audit.

C# worker tests use controlled processes. They verify contracts, JSONL parsing, reported usage, errors, timeouts and stop handling. They do not demonstrate current model access, diagnostic quality or a real end-to-end investigation. AI is not required for rule-based detection and remains disabled by default.

A live test requires an authenticated worker and explicit consent through `bash scripts/lab.sh smoke-ai --confirm-ai`; it consumes account usage. It has not been run for this version. Slack, Telegram and email are not implemented: `lab-inbox` is an internal notification channel.

## AWS reference deployment — October 10, 2026

The authorized reference installation ran in **Europe (Milan), `eu-south-1`**, on separate `t3.large` Monitor and `t3.small` Target instances. The existing VPC/subnet was not modified. CloudFormation completed, both SSM nodes came online, Docker images built on EC2, and the actual React/C# GUI was accessed from the Mac through an SSM tunnel. No public application ingress, SSH rule or public DNS was added.

PostgreSQL 17.6 and OpenSearch 3.3.0 ran as real containers. The verification command confirmed target measurements reaching authenticated ingestion, the SQL outbox, the search index and the analyzer. This is containerized OpenSearch, not Amazon OpenSearch Service.

The **first run found a real Linux-container defect**: the 128 MiB mapped memory fault exceeded Docker's default 64 MiB shared-memory filesystem. The Target logged a fatal `AccessViolationException` during page writes and restarted; the monitor correctly recorded missing telemetry, and the experiment failed. The failed record was retained. `compose.target.yml` now sets `shm_size: 256m`, retaining the 512 MiB total container limit, with a regression assertion in the tools suite.

After redeployment, **all eight scenarios passed** against real PostgreSQL/OpenSearch, including response-ID matching, expected rules and final recovery:

| Scenario | Requests / responses | HTTP errors | Expected and detected rule |
|---|---:|---:|---|
| Memory | 156 / 156 | 0 | `process-memory` |
| Normal | 139 / 139 | 0 | None |
| Retry | 168 / 168 | 0 | `repeat-command` |
| HTTP probing | 174 / 174 | 116 | `http-probing` |
| Errors | 159 / 159 | 60 | `server-errors` |
| Latency | 146 / 146 | 0 | `slow-responses` |
| Telemetry gap | 175 / 175 | 0 | `source-stale` |
| CPU | 163 / 163 | 0 | `process-cpu` |

The profile was 30 seconds, at most 6 requests/s, 3 clients and 5 seconds of warmup. All final runs had zero transport errors and cancelled requests. The gap test verified null current metrics and an empty live chart while samples were stale, followed by real recovery. **No AI calls or tokens were consumed.**

The GUI was inspected against AWS: actual backend/telemetry status, active experiment parameters (including runs started by scripts), incident evidence with HTTP response IDs, internal opened/resolved notifications, the offline agent state and zero AI usage. This is manual verification, not a browser regression suite. Two misleading UI details were corrected: the static undeployed footer and idle form values being displayed during externally started experiments.

Local regression checks on this revision: **82 C# tests and 34 TypeScript tests passed**, with a successful TypeScript/Vite build and Bash syntax checks. The additional tools test protects the Target shared-memory configuration.

### Storage interruption and persistence

With no experiment or AI job running, OpenSearch was stopped for 25 seconds, then started with a bounded recovery command. The GUI/API reported unavailable telemetry, null current measurements and a `storage-unavailable` incident. The outbox subsequently drained and the incident resolved with fresh data.

The monitor was then stopped, PostgreSQL and OpenSearch restarted, and Compose brought all services back to healthy without deleting volumes. The pipeline check passed again. Comparison with a pre-restart snapshot confirmed all **14 experiment records** (including the retained failed attempt), **13 historical incidents**, **26 historical notices** and AI settings were preserved. OpenSearch documents increased from **3,546 before restart to 3,553 after recovery**, rather than being recreated from an empty index. AI remained at zero invocations and zero tokens.

These checks do **not** prove lossless telemetry delivery: during the monitor interruption, the Target's non-durable retry queue reported **20 dropped events**. Historical persisted data survived; events not yet accepted by the monitor can still be lost. Backups, durable target spooling and disaster recovery are not implemented.

## Remaining acceptance beyond the reference deployment

1. Verify private DNS if configured; it was not configured in the reference deployment.
2. Separately validate the optional AI worker with an admitted incident and explicit inference consent.
3. Complete human acceptance of the optional CloudFront/WAF login, logout and one-hour session; no bypass should be used. Shared credentials are not production identity/RBAC. External notifications remain unimplemented.
4. Exercise long-running load, retention, backup/restore and disaster recovery. A short reference test does not prove production availability.
5. Measure storage growth and actual billing; the deployment estimate is not a budget cap. Stop the laboratory when unused.

Reproducible commands are in the [README](../README.md#code-verification) and [AWS guide](../infra/aws/README.md). Passing this laboratory's checks does not establish production security or diagnostic accuracy of an LLM.
