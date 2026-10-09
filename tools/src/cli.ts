import { LabError, parse } from './common.js';
import { awsMain } from './aws.js';
import { local } from './local.js';
import { smoke, smokeAi } from './smoke.js';

const help = `IncidentLab tools — TypeScript / Node 22+, no Python required.
Run from the repository root after: npm --prefix tools ci && npm --prefix tools run build

  bash scripts/lab.sh local [--database PATH] [--monitor-port 8010] [--target-port 8081] [--with-codex]
  bash scripts/lab.sh smoke [--url URL] [--scenarios normal retry ...] [--manual-ai]
  bash scripts/lab.sh smoke-ai --confirm-ai [--url URL] [--judge] [--model ID] [--effort low]
  bash scripts/lab.sh aws preflight|status|tunnel [--config PATH] [--profile PROFILE]
  bash scripts/lab.sh aws create|configure|install|verify|dns --apply [--config PATH] [--profile PROFILE]
  bash scripts/lab.sh aws public-prepare --apply [--access viewer] [--profile PROFILE]
  bash scripts/lab.sh aws public-master-prepare --apply [--profile PROFILE]
  bash scripts/lab.sh aws public-status [--profile PROFILE]
  bash scripts/lab.sh aws public-install|public-enable|public-disable --apply [--profile PROFILE]
  bash scripts/lab.sh aws bedrock-prepare --apply --allow-eu-inference [--profile PROFILE]
  bash scripts/lab.sh aws bedrock-budget --apply --monthly-usd 100 [--profile PROFILE]

local: macOS/Linux (WSL2 on Windows), Debug .NET binaries and built frontend required.
smoke: generates bounded traffic/faults on the monitor's configured laboratory target.
smoke-ai: consumes real AI account usage; 2 calls, plus at most 1 diagnostic follow-up and 1 optional judge.
AWS writes require --apply. Public access is optional, authenticated and enabled in a separate step.
Read infra/aws/README.md before using a corporate account.`;

async function main(): Promise<void> {
  if (Number(process.versions.node.split('.')[0]) < 22) throw new LabError('Node 22 or later is required.');
  const args = process.argv.slice(2);
  if (!args.length || ['help', '--help', '-h'].some(v => args.includes(v))) { console.log(help); return; }
  const isAws = args[0] === 'aws';
  const options = parse(isAws ? args.slice(1) : args); const controller = new AbortController();
  const stop = () => controller.abort(); process.once('SIGINT', stop); process.once('SIGTERM', stop);
  try {
    if (isAws) await awsMain(options.command, options.values, controller.signal);
    else if (options.command === 'local') await local(options.values, controller.signal);
    else if (options.command === 'smoke') await smoke(options.values, controller.signal);
    else if (options.command === 'smoke-ai') await smokeAi(options.values, controller.signal);
    else throw new LabError('Unknown command. Run bash scripts/lab.sh --help.');
  } catch (e) {
    if (!controller.signal.aborted) throw e;
    console.error(isAws ? 'Stopped observing AWS. An admitted SSM command or stack may still run; inspect AWS before retrying.'
      : 'Stopped. Owned local processes are closed; inspect any remote run if cleanup could not complete.');
    process.exitCode = 130;
  } finally { process.removeListener('SIGINT', stop); process.removeListener('SIGTERM', stop); }
}
main().catch(error => {
  // Never dump subprocess environments, AWS CLI stderr, tokens or raw provider responses.
  console.error(`ERROR: ${error instanceof LabError ? error.message : 'Operation failed; check the runbook and local prerequisites (details withheld).'}`);
  process.exitCode = 1;
});
