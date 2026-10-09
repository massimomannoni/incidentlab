# Article illustrations

The current GUI screenshots were captured from the deployed C# laboratory on October 11, 2026 through private SSM forwarding. They show real saved Bedrock investigations and measured telemetry, not mocks. No experiment or AI call was started to create these images. The diagrams illustrate implementation and configuration, not measured performance. These assets are documentation only; they are not required to run the application.

## Current figures

- [AWS infrastructure](incidentlab-infrastructure-light.png): Monitor and Target in Milan, self-hosted PostgreSQL/OpenSearch, optional CloudFront/WAF and the gateway on the Monitor, private SSM administration, and IAM-authenticated Amazon Bedrock inference. The EU inference profile can process outside Milan. Codex on a Mac is an alternative, not a requirement for Bedrock. The public private-origin hop is HTTP; HTTPS applies to viewers and Bedrock calls.
- [Investigation workflow](incidentlab-workflow-light.png): zero model calls while idle; two base calls, one optional frozen-data diagnostic follow-up and one optional Judge, at most four calls. All proposed remediation remains unexecuted; notifications reach the internal inbox only.
- [C# contracts and endpoints](incidentlab-contracts-light.png): shared prompts in `AgentPrompts.cs`, provider-specific executors, schemas and server policy. `LabApplication.cs` defines Monitor HTTP routes; `target/Program.cs` defines Target routes. Both use Minimal APIs, not MVC controllers.
- [Incident workspace](gui-incident-investigation.jpg): CPU incident `INC-6731F187`, event time, measured timeline and captured Bedrock context.
- [Recorded Bedrock diagnosis](gui-bedrock-diagnosis.jpg): the report for that incident, including a likely cause, alternatives and proposed actions. This is a model hypothesis, not confirmed causation or proof that proposed fixes were executed.
- [AWS Scenario tester](gui-aws-scenario-tester.jpg): eight scenarios and real request settings. The pictured private non-master session has exhausted its 10 daily starts; Start is correctly disabled. The personal master bypasses that daily simulation quota, not load bounds or AI limits.
- [Bedrock usage for one incident](gui-bedrock-usage.jpg): three calls, 10,250 reported tokens, approximately USD 0.00156 in conservative application accounting. The sidebar is laboratory-wide; the filtered panel is incident-specific. These values are not an AWS invoice or whole-account usage.

The application is viewed privately in these screenshots: the public login/CAPTCHA page is not part of the captured viewport. See the [test deployment](../test-deployment.md), [public access](../public-access.md), [Bedrock](../bedrock.md) and [verification record](../verification.md) for installation and limits.

## Historical assets

`gui-agent-control-room.jpg`, `gui-ai-usage.jpg` and `gui-scenario-tester.jpg` show the earlier local Codex/SQLite prototype. `incidentlab-architecture.*`, `incidentlab-cover.png` and `cover-prompt.txt` are earlier editorial alternatives. They are retained for reference, not used as current AWS/Bedrock illustrations. Do not present these legacy files as screenshots of the deployed release.

To regenerate the three light-style SVG/PNG diagrams with Node.js 22 or later:

```sh
cd docs/article-assets
npm install
npm run render
```

The generator uses the pinned `sharp` dependency from this directory, without machine-specific paths. It rewrites only `incidentlab-workflow-light`, `incidentlab-infrastructure-light` and `incidentlab-contracts-light` (`.svg` and `.png`). It does not replace the GUI screenshots or invoke an image-generation model.

The unpublished Medium article text is intentionally excluded from Git. The application code, installation runbook and MIT license can be shared independently.
