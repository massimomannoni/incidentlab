# Amazon Bedrock setup and cost controls

IncidentLab can run its Investigator, Manager and optional Judge on Amazon Bedrock without a Mac worker or an OpenAI API key. The C# monitor uses the official AWS SDK and rotating EC2 instance-role credentials. Detection and dashboard polling still make no model calls. The provider is **disabled by default** and must be approved and enabled separately.

## Region and model

This bounded integration allows only `eu.amazon.nova-lite-v1:0` through the `eu-south-1` endpoint. The application remains in Milan, but the **EU inference profile may process evidence in Milan, Frankfurt, Ireland, Paris or Stockholm**. Approve this before enabling it; do not describe it as Milan-only inference. The allowlist deliberately excludes global profiles, other models and provisioned throughput. [AWS model availability](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-amazon-nova-lite.html).

Nova Lite has no configurable reasoning effort in this integration. Each role uses a separate request and the same model. The optional Judge is another model call, not a guarantee of independent judgment. No tools, browser, shell, external notifications, prompt caching or automatic retries are enabled. Only a bounded, redacted incident snapshot and prior validated reports reach the model.

## Prepare the AWS host

First complete the [private test deployment](test-deployment.md). Review a clean, committed source revision and use an approved operator role, not account root. Besides the base installer permissions, preparation needs `bedrock:GetInferenceProfile`, CloudFormation/IAM policy management and permission to change the exact Monitor's metadata options. Corporate service control policies must permit the EU destinations.

From CloudShell, with Node 22+ and your destination configuration in place:

```sh
npm --prefix tools ci
npm --prefix tools run build
bash scripts/lab.sh aws bedrock-prepare --apply --allow-eu-inference
# Wait for STACK_NAME-bedrock to reach CREATE_COMPLETE in CloudFormation.
bash scripts/lab.sh aws bedrock-prepare --apply --allow-eu-inference
# For a private installation:
bash scripts/lab.sh aws install --apply
# If the public gateway already exists, this updates Monitor and gateway instead:
# bash scripts/lab.sh aws public-install --apply
```

On a workstation append `--profile YOUR_APPROVED_PROFILE`. Run the preparation command twice for a new stack: the first creates the IAM policy; the second verifies its targets and configures the host. An existing mismatched stack is never overwritten. These steps perform no inference, but **existing enabled AI database settings survive reinstallation**. Disable AI privately before upgrading an already-active installation.

`infra/aws/bedrock-access.json` grants only `bedrock:InvokeModel` for the approved EU profile and its five foundation-model resources. Conditions restrict it to credentials issued to the exact Monitor instance and to the approved inference profile. The shared Target role gets no usable Bedrock permission from its own instance credentials. This is narrower than `AmazonBedrockFullAccess`; it does not replace the lab's existing shared-host trust boundary.

Preparation keeps IMDSv2 mandatory and changes the Monitor's metadata response hop limit from 1 to 2 so its Docker container can obtain rotating credentials. The Target stays unchanged. This intentional setting differs from the base template; review it during drift checks. Containers and administrators on the Monitor remain trusted. No AWS access keys, Codex login files or API keys are installed.

## Enable and verify privately

Open the owner GUI through the SSM tunnel, then **Settings → Amazon Bedrock — AWS IAM**. Check the model, budgets and output limit before enabling AI. Start with automatic admission off and the Judge off. Save, open a real incident and select **Analyze with AI**. This consumes AWS usage.

Open that incident's investigation workspace (also linked from **Agents**) to keep its identity, event date, type and measured chart visible. Check **AI result**, **Agent activity**, **Evidence**, **Notifications** and **AI usage**: verify both reports, policy outcome, request IDs, internal delivery and per-invocation accounting. The broader **AI usage** report groups sessions by agent and incident. Enable automatic admission only after that succeeds; it considers new incidents after activation. No model-access claim is implied merely by an ACTIVE inference profile or a green HTTP health check. See the [investigation walkthrough](investigation.md) and [configuration reference](configuration.md).

The reference deployment completed this two-agent check on October 10, 2026 and then enabled automatic admission with the Judge off. The [live acceptance record](verification.md#bedrock-live-acceptance-october-10-2026) includes successful usage and retained failures. This does not activate or certify another installation. The public master can inspect the result in **Agents** and **AI usage**; private settings remain accessible only through SSM.

The shared `viewer` account cannot start experiments, change these settings or manually queue jobs. The optional personal master can request analysis only after private activation and remains subject to the same AI limits; it cannot change provider settings or budgets. General public tester identities and their one-per-day quota are a separate, pending feature; this integration does not turn viewers into administrators.

## Cost controls

The server's default deployment ceilings are **USD 5 per UTC calendar month and USD 0.50 per UTC day** for its Bedrock budget ledger. An operator may explicitly approve a monthly ceiling up to **USD 100**; the daily maximum stays USD 0.50. Private settings may lower either limit to zero, but cannot raise it above the deployment ceiling. Re-running Bedrock preparation preserves existing ceilings, including zero. There are at most 10 admitted AI jobs per UTC day and one running job. Two calls are normal; the Investigator may make one additional call using a selected frozen diagnostic check, and the optional Judge adds another (four maximum). Every call, including follow-up, reserves budget independently and stops if it does not fit. A simulation can open more than one incident; the AI job quota is separate from the simulation quota.

The reference installation uses the owner-approved **USD 100/month**, retaining **USD 0.50/day, 10 jobs/day and Judge off**. With that daily limit, a 31-day month allows at most USD 15.50 of ledger-accounted daily spend; the monthly ceiling is an additional guard, not a target. All amounts are USD, with no automatic currency conversion: this is not a guaranteed EUR 100 invoice cap.

### Change the monthly ceiling

Use a clean, committed revision containing the USD 100 validation support. Pause AI in private Settings and wait for running jobs to finish before upgrading. From your configured operator checkout:

```sh
npm --prefix tools ci
npm --prefix tools run build
bash scripts/lab.sh aws bedrock-budget --apply --monthly-usd 100
bash scripts/lab.sh aws install --apply
bash scripts/lab.sh aws verify --apply
```

Append `--profile YOUR_APPROVED_PROFILE` on a workstation. `bedrock-budget` changes only `LAB_BEDROCK_MONTHLY_CAP_USD` in the Monitor SecureString; it preserves secrets, the daily ceiling and provider activation. It creates no resources or IAM policies and performs no inference. `install` updates both application hosts, preserving their data volumes; it does not reinstall the separately managed public gateway. Schedule this application interruption. The new ceiling takes effect only after Monitor reinstallation.

Through the SSM tunnel, set **Settings → AI monthly budget (USD, UTC)** to the approved amount and save. Restore the previous AI/automatic-admission state if appropriate; re-enabling automatic admission considers new incidents only. Verify the **Agents** budget strip shows the effective monthly limit and the unchanged daily limit. Both the environment ceiling and private setting must be updated; changing only one leaves the smaller limit in force. Never clear usage or unresolved reservations to apply a new limit. Zero stops subsequent paid calls, not an already in-flight request.

Before each network call, the monitor commits a reservation for **the model's full 300,000-token input context plus the configured maximum output**, using conservative rates of USD 0.10 and USD 0.40 per million tokens. It refuses the call if that reservation does not fit both budgets. A complete token report settles the reservation to the corresponding conservative cost. A timeout, restart, cancellation or missing counters leaves the amount reserved, including across day/month rollover. Do not delete these rows to reset a quota: an unknown outcome may have been billed.

At 1,000 maximum output tokens a call reserves USD 0.0304 before dispatch. The actual complete prompt is additionally limited to 16,000 UTF-8 bytes by default, at most 24,000, and output is at most 2,000 tokens. Input is limited by **bytes, not an invented token count**; the older `max_input_tokens` field is not used by this provider. Dollar reservations use the full context precisely to avoid relying on tokenizer estimates. Lowering a live budget applies to subsequent calls even when their job settings were frozen earlier.

AWS Price List API values checked on October 10, 2026 for Milan were USD **0.096 input / 0.384 output per million tokens**, below the conservative ledger rates. Recheck prices and model limits before deployment and after AWS changes. As an illustration, 300 investigations with two calls each, each call using 4,000 input and 1,000 output tokens, would cost about **USD 0.46** at those rates before tax. This is an assumption-based example, not a measured bill or a spending promise. [AWS pricing](https://aws.amazon.com/bedrock/pricing/).

The ledger covers only this monitor and database, not all Bedrock use in the account. It is not an AWS billing hard cap: rate changes, taxes and calls outside IncidentLab are outside its calculation. AWS Budgets alerts can be delayed and do not replace pre-call admission. A budget alarm is not created by these scripts. [AWS Budgets limitations](https://docs.aws.amazon.com/cost-management/latest/userguide/budgets-managing-costs.html).

**EC2, EBS, public IPv4, WAF and CloudFront costs are separate.** Limiting simulations or AI does not stop their fixed charges; follow the [shutdown instructions](test-deployment.md#6-update-stop-and-resume).

## Failure handling and implementation

The SDK has zero inference retries. Nova Lite returns text, not schema-constrained output: the server validates the JSON and evidence references before advancing a stage. Invalid or truncated output fails the job without a fabricated replacement; reported usage is still accounted for. A failed Judge blocks AI delivery, while deterministic rule notifications continue. Disabling AI prevents further stages but cannot undo an in-flight paid request.

One complete `json` Markdown fence is accepted around a JSON object; prose, multiple blocks, unknown fields and invalid evidence references are not repaired or trusted. Known AWS failures appear as safe categories such as `bedrock_access_denied`, `bedrock_validation_error` and `bedrock_throttled`, with a correlation ID when available. Do not respond by granting full access or deleting ledger rows. Check the exact role/profile, account access, EU policies and quota in AWS; only run another bounded acceptance investigation after diagnosing the issue. Raw provider errors and rejected model text are not published.

Prompts live in `backend/IncidentLab.Core/AgentPrompts.cs`, shared with Codex. `BedrockExecutor.cs` contains the SDK adapter and background executor; `BedrockBudget.cs` contains admission and settlement. Accounting uses the existing SQL session JSON fields, so this update requires no new table or destructive migration. Only one Monitor process may own the database; distributed execution is not supported.

Local provider tests use controlled responses to exercise budgets and failure cases; they are not runtime dashboard data or evidence of a live Bedrock call. See [verification](verification.md) for the current acceptance status.
