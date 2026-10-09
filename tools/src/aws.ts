import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createHash, randomBytes } from 'node:crypto';
import { isIPv4 } from 'node:net';
import { ROOT, type Obj, type Execute, LabError, array, object, str, execute, interactive, requireThat, secureJson, shellQuote, sleep, temporary, only, type Options } from './common.js';

export interface Config extends Obj { account_id: string; region: string; stack_name: string; vpc_id: string; subnet_id: string }
export interface AwsClient {
  call(...args: string[]): Promise<Obj>;
  input(service: string, operation: string, payload: Obj): Promise<Obj>;
}
export class AwsError extends LabError {
  constructor(operation: string, readonly code: string) {
    super(`AWS ${operation} failed (${code}); check permissions/region in the AWS console. Values withheld.`);
  }
}
export class AWS implements AwsClient {
  readonly prefix: string[];
  constructor(c: Config, profile?: string, readonly run: Execute = execute) {
    this.prefix = ['--region', c.region, '--no-cli-pager', '--output', 'json'];
    if (profile) this.prefix.push('--profile', profile);
  }
  async call(...args: string[]): Promise<Obj> {
    const result = await this.run('aws', [...this.prefix, ...args], ROOT, { ...process.env, AWS_PAGER: '', AWS_CLI_AUTO_PROMPT: 'off' });
    if (result.code) throw new AwsError(args.slice(0, 2).join(' '), /\(([A-Za-z0-9]+)\)/.exec(result.stderr)?.[1] ?? 'CLIError');
    try { return result.stdout.trim() ? object(JSON.parse(result.stdout)) : {}; }
    catch { throw new LabError('Invalid AWS JSON response (values withheld).'); }
  }
  async input(service: string, operation: string, payload: Obj): Promise<Obj> {
    return temporary('incidentlab-aws-', async directory => {
      const path = join(directory, 'input.json');
      await secureJson(path, payload);
      return this.call(service, operation, '--cli-input-json', `file://${path}`);
    });
  }
}
export function validateConfig(value: unknown): Config {
  const c = object(value);
  const rules: Record<string, RegExp> = {
    account_id: /^\d{12}$/, region: /^[a-z]{2}(?:-[a-z]+)+-\d$/, stack_name: /^[a-z][a-z0-9-]{2,30}$/,
    vpc_id: /^vpc-[0-9a-f]{8,17}$/, subnet_id: /^subnet-[0-9a-f]{8,17}$/,
  };
  for (const [key, rule] of Object.entries(rules)) requireThat(typeof c[key] === 'string' && rule.test(c[key]), `Set a valid ${key}; placeholders are not accepted.`);
  return c as Config;
}
export function plugins(c: Config): void {
  for (const name of ['compose', 'buildx']) {
    requireThat(typeof c[`${name}_version`] === 'string' && /^v\d+\.\d+\.\d+$/.test(str(c[`${name}_version`])), `Pin ${name}_version to an official release.`);
    requireThat(typeof c[`${name}_sha256`] === 'string' && /^[a-f0-9]{64}$/.test(str(c[`${name}_sha256`])), `Copy ${name}_sha256 from official release checksums.`);
  }
}
export async function identity(aws: AwsClient, c: Config): Promise<void> {
  requireThat((await aws.call('sts', 'get-caller-identity')).Account === c.account_id, 'Account mismatch. No changes made.');
}
export async function preflight(aws: AwsClient, c: Config): Promise<void> {
  await identity(aws, c);
  const subnet = array((await aws.call('ec2', 'describe-subnets', '--subnet-ids', c.subnet_id)).Subnets)[0];
  requireThat(subnet?.VpcId === c.vpc_id && subnet.State === 'available', 'Subnet/VPC mismatch or subnet unavailable.');
  for (const [attribute, key] of [['enableDnsSupport', 'EnableDnsSupport'], ['enableDnsHostnames', 'EnableDnsHostnames']]) {
    const response = await aws.call('ec2', 'describe-vpc-attribute', '--vpc-id', c.vpc_id, '--attribute', attribute);
    requireThat(object(response[key]).Value === true, `VPC requires ${attribute}; ask its owner. No VPC settings changed.`);
  }
  await aws.call('cloudformation', 'validate-template', '--template-body', `file://${join(ROOT, 'infra/aws/lab-infrastructure.json')}`);
  console.log(`Account/VPC/template checks passed for ${c.stack_name} in ${c.region}.`);
  console.log('Egress, corporate approval, quotas and budget still need operator review. No network resources created.');
}
export function privateIPv4(value: string): boolean {
  if (!isIPv4(value)) return false;
  const [a, b] = value.split('.').map(Number);
  return a === 10 || a === 172 && b >= 16 && b <= 31 || a === 192 && b === 168;
}
export async function outputs(aws: AwsClient, c: Config): Promise<Record<string, string>> {
  const stack = array((await aws.call('cloudformation', 'describe-stacks', '--stack-name', c.stack_name)).Stacks)[0];
  requireThat(stack && ['CREATE_COMPLETE', 'UPDATE_COMPLETE'].includes(str(stack.StackStatus)), 'Stack is not ready.');
  const parameters = Object.fromEntries(array(stack.Parameters).map(p => [str(p.ParameterKey), str(p.ParameterValue)]));
  requireThat(parameters.VpcId === c.vpc_id && parameters.SubnetId === c.subnet_id, 'Existing stack network differs from this config.');
  const result = Object.fromEntries(array(stack.Outputs ?? []).map(o => [str(o.OutputKey), str(o.OutputValue)]));
  for (const key of ['MonitorInstanceId', 'TargetInstanceId']) requireThat(/^i-[a-f0-9]{8,17}$/.test(result[key] ?? ''), `Missing/invalid ${key} output.`);
  for (const key of ['MonitorPrivateIp', 'TargetPrivateIp']) requireThat(privateIPv4(result[key] ?? ''), `Invalid private IPv4 output: ${key}.`);
  requireThat(/^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$/.test(result.DeploymentBucket ?? ''), 'DeploymentBucket missing. Older stacks need an explicitly reviewed template update.');
  return result;
}
export async function create(aws: AwsClient, c: Config): Promise<void> {
  await preflight(aws, c);
  await aws.call('cloudformation', 'create-stack', '--stack-name', c.stack_name, '--template-body', `file://${join(ROOT, 'infra/aws/lab-infrastructure.json')}`,
    '--capabilities', 'CAPABILITY_IAM', '--enable-termination-protection', '--parameters', JSON.stringify([
      { ParameterKey: 'VpcId', ParameterValue: c.vpc_id }, { ParameterKey: 'SubnetId', ParameterValue: c.subnet_id }, { ParameterKey: 'ProjectTag', ParameterValue: c.stack_name },
    ]));
  console.log('Stack creation started: billable EC2/EBS/S3 resources and IAM permissions.');
  await aws.call('cloudformation', 'wait', 'stack-create-complete', '--stack-name', c.stack_name);
  console.log(JSON.stringify(await outputs(aws, c), null, 2));
}
export const parameterName = (c: Config, role: string) => `/incidentlab/${c.stack_name}/${role}`;
export async function getConfig(aws: AwsClient, c: Config, role: string): Promise<Obj | null> {
  let response: Obj;
  try { response = await aws.call('ssm', 'get-parameter', '--name', parameterName(c, role), '--with-decryption'); }
  catch (e) { if (e instanceof AwsError && e.code === 'ParameterNotFound') return null; throw e; }
  const parameter = object(response.Parameter);
  requireThat(parameter.Type === 'SecureString', 'Existing configuration parameter is not a SecureString.');
  try { return object(JSON.parse(str(parameter.Value))); }
  catch { throw new LabError('Invalid parameter JSON object (values withheld).'); }
}
export function validateSecrets(values: Obj, keys: string[]): void {
  requireThat(keys.every(key => typeof values[key] === 'string' && /^[a-f0-9]{64}$/.test(str(values[key]))), 'Invalid existing secrets; restore configuration instead of silently rotating it.');
}
export async function configure(aws: AwsClient, c: Config): Promise<void> {
  const o = await outputs(aws, c);
  let monitor = await getConfig(aws, c, 'monitor');
  const previousTarget = await getConfig(aws, c, 'target');
  const shared = ['LAB_INGEST_KEY', 'LAB_CONTROL_KEY'];
  const secretKeys = [...shared, 'POSTGRES_PASSWORD', 'LAB_WORKER_KEY'];
  requireThat(monitor !== null || previousTarget === null, 'Monitor configuration missing but Target exists. Restore it; do not reset the database password.');
  monitor ??= Object.fromEntries(secretKeys.map(k => [k, randomBytes(32).toString('hex')]));
  validateSecrets(monitor, secretKeys);
  requireThat(previousTarget === null || shared.every(k => previousTarget[k] === monitor![k]), 'Existing Monitor/Target keys disagree. No automatic key rotation.');
  monitor.TARGET_URL = `http://${o.TargetPrivateIp}:8080`; monitor.MONITOR_BIND_ADDRESS = o.MonitorPrivateIp;
  const target: Obj = Object.fromEntries(shared.map(k => [k, monitor![k]]));
  target.MONITOR_URL = `http://${o.MonitorPrivateIp}:8010`; target.TARGET_BIND_ADDRESS = o.TargetPrivateIp;
  for (const [role, values] of [['monitor', monitor], ['target', target]] as const) {
    await aws.input('ssm', 'put-parameter', { Name: parameterName(c, role), Type: 'SecureString', Value: JSON.stringify(values), Overwrite: true,
      Description: 'IncidentLab host configuration; never copy into article or logs' });
  }
  console.log('SecureString configuration saved. Existing secrets preserved; no values printed.');
}
export async function online(aws: AwsClient, o: Record<string, string>): Promise<void> {
  const ids = [o.MonitorInstanceId, o.TargetInstanceId];
  const rows = array((await aws.call('ssm', 'describe-instance-information', '--filters', JSON.stringify([{ Key: 'InstanceIds', Values: ids }]))).InstanceInformationList);
  requireThat(ids.every(id => rows.some(row => row.InstanceId === id && row.PingStatus === 'Online')), 'Both EC2 hosts must be Online in Systems Manager. Check IAM, cloud-init and subnet egress.');
}
export async function runCommand(aws: AwsClient, instance: string, script: string, timeout = 1800): Promise<void> {
  const id = str(object((await aws.input('ssm', 'send-command', { InstanceIds: [instance], DocumentName: 'AWS-RunShellScript', TimeoutSeconds: 300,
    Comment: 'IncidentLab reviewed installation/verification', Parameters: { commands: [script], executionTimeout: [String(timeout)] } })).Command).CommandId);
  console.log(`SSM command ${id} on ${instance}`);
  const deadline = performance.now() + (timeout + 330) * 1000;
  while (performance.now() < deadline) {
    try {
      const result = await aws.call('ssm', 'get-command-invocation', '--command-id', id, '--instance-id', instance);
      if (result.Status === 'Success') { console.log(result.StandardOutputContent ?? ''); return; }
      requireThat(['Pending', 'InProgress', 'Delayed'].includes(str(result.Status)), `SSM command ${id} failed. Inspect Run Command output; do not paste secrets.`);
    } catch (e) { if (!(e instanceof AwsError && e.code === 'InvocationDoesNotExist')) throw e; }
    await sleep(5000);
  }
  throw new LabError(`Timed out observing ${id}. It may still run; inspect SSM before retrying.`);
}
export async function archive(destination: string, root = ROOT, run = execute): Promise<string> {
  const git = async (...args: string[]) => {
    const result = await run('git', ['-C', root, ...args]);
    requireThat(result.code === 0, 'Git release operation failed. Check repository state.');
    return result.stdout.trim();
  };
  requireThat(!(await git('status', '--porcelain', '--untracked-files=no')), 'Commit reviewed tracked changes before deployment; archive deploys HEAD, never a dirty tree.');
  for (const path of ['infra/aws/install-node.sh', 'infra/aws/verify-node.sh', 'infra/aws/write-env.jq', 'backend/Dockerfile', 'target/Dockerfile', 'Directory.Build.props'])
    requireThat(await git('ls-tree', '--name-only', 'HEAD', '--', path), `Commit ${path} before deployment.`);
  const paths = ['backend', 'frontend', 'target', 'Directory.Build.props', 'compose.monitor.yml', 'compose.target.yml', 'compose.public.yml', '.dockerignore', 'infra/aws/install-node.sh', 'infra/aws/verify-node.sh', 'infra/aws/write-env.jq', 'infra/aws/install-public.sh', 'infra/aws/verify-public.sh'];
  const names = (await git('ls-tree', '-r', '--name-only', 'HEAD', '--', ...paths)).split('\n');
  for (const name of names) requireThat(!name.split('/').some(p => ['node_modules', '.venv', '.aws', '.git', '.codex'].includes(p) || p.startsWith('.env') || /\.(pem|key|db|sqlite3?|jsonl)$/.test(p)), `Unsafe tracked deployment file: ${name}. Remove it from the release first.`);
  await git('archive', '--format=tar.gz', `--output=${destination}`, 'HEAD', '--', ...paths);
  return createHash('sha256').update(await readFile(destination)).digest('hex');
}
export function installationScript(c: Config, o: Record<string, string>, role: string, digest: string): string {
  requireThat(['monitor', 'target'].includes(role) && /^[a-f0-9]{64}$/.test(digest), 'Invalid release identifier.');
  const release = `/opt/incidentlab/releases/${digest}`;
  const args = [role, c.region, parameterName(c, role), str(c.compose_version), str(c.compose_sha256), str(c.buildx_version), str(c.buildx_sha256)];
  return ['#!/bin/bash', 'set -eu', 'umask 077', 'cloud-init status --wait >/dev/null', 'command -v aws >/dev/null', 'mkdir -p /opt/incidentlab/releases',
    `if [ ! -d ${shellQuote(release)} ]; then`, '  lab_stage=$(mktemp -d /opt/incidentlab/releases/.staging.XXXXXX)',
    `  aws --region ${shellQuote(c.region)} s3 cp ${shellQuote(`s3://${o.DeploymentBucket}/releases/${digest}.tar.gz`)} "$lab_stage/bundle.tar.gz" --only-show-errors`,
    `  printf '%s  %s\\n' ${shellQuote(digest)} "$lab_stage/bundle.tar.gz" | sha256sum --check --status`,
    '  mkdir "$lab_stage/source"', '  tar --no-same-owner --same-permissions -xzf "$lab_stage/bundle.tar.gz" -C "$lab_stage/source"',
    `  mv "$lab_stage/source" ${shellQuote(release)}`, '  rm -- "$lab_stage/bundle.tar.gz"', '  rmdir -- "$lab_stage"', 'fi',
    `bash ${shellQuote(release + '/infra/aws/install-node.sh')} ${args.map(shellQuote).join(' ')}`].join('\n');
}
export async function install(aws: AwsClient, c: Config): Promise<void> {
  plugins(c);
  const o = await outputs(aws, c); await online(aws, o);
  requireThat(await getConfig(aws, c, 'monitor') && await getConfig(aws, c, 'target'), 'Run configure --apply first.');
  const digest = await temporary('incidentlab-release-', async directory => {
    const bundle = join(directory, 'source.tar.gz'); const hash = await archive(bundle);
    await aws.call('s3api', 'put-object', '--bucket', o.DeploymentBucket, '--key', `releases/${hash}.tar.gz`, '--body', bundle, '--server-side-encryption', 'AES256');
    return hash;
  });
  for (const role of ['monitor', 'target']) await runCommand(aws, o[role === 'monitor' ? 'MonitorInstanceId' : 'TargetInstanceId'], installationScript(c, o, role, digest));
  console.log('Both hosts installed. Run verify; stack completion is not an application health check.');
}
export async function dns(aws: AwsClient, c: Config): Promise<void> {
  const o = await outputs(aws, c);
  const zoneId = str(c.private_zone_id).replace(/^\/hostedzone\//, ''); const name = str(c.dns_name).toLowerCase().replace(/\.$/, '');
  requireThat(/^Z[A-Z0-9]+$/.test(zoneId) && name.length <= 253 && name.split('.').every(p => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(p)), 'Set a valid private_zone_id and dns_name.');
  const zone = await aws.call('route53', 'get-hosted-zone', '--id', zoneId); const hosted = object(zone.HostedZone);
  const suffix = str(hosted.Name).toLowerCase().replace(/\.$/, '');
  requireThat(object(hosted.Config).PrivateZone === true, 'Public hosted zones are refused: this POC has no public authentication.');
  requireThat(array(zone.VPCs).some(v => v.VPCId === c.vpc_id && v.VPCRegion === c.region), 'Private zone is not associated with this VPC/region.');
  requireThat(name === suffix || name.endsWith('.' + suffix), 'DNS name does not belong to the selected zone.');
  const record: Obj = { Name: name + '.', Type: 'A', TTL: 60, ResourceRecords: [{ Value: o.MonitorPrivateIp }] };
  const rows = array((await aws.call('route53', 'list-resource-record-sets', '--hosted-zone-id', zoneId)).ResourceRecordSets);
  const matches = rows.filter(r => str(r.Name).toLowerCase() === name + '.' && ['A', 'CNAME'].includes(str(r.Type)));
  if (matches.length) {
    const r = matches[0];
    const same = matches.length === 1 && Object.keys(r).length === 4 && r.Name === record.Name && r.Type === 'A' && r.TTL === 60 && JSON.stringify(r.ResourceRecords) === JSON.stringify(record.ResourceRecords);
    requireThat(same, 'A record/CNAME already exists. Refusing to overwrite it.');
    console.log('Private DNS record already matches; unchanged.'); return;
  }
  await aws.input('route53', 'change-resource-record-sets', { HostedZoneId: zoneId, ChangeBatch: { Comment: 'IncidentLab private monitor', Changes: [{ Action: 'CREATE', ResourceRecordSet: record }] } });
  console.log(`Private A record requested: ${name} -> ${o.MonitorPrivateIp}. No network access or HTTPS added.`);
}
export const commands = ['preflight', 'create', 'configure', 'install', 'verify', 'dns', 'tunnel', 'status', 'public-prepare', 'public-master-prepare', 'public-status', 'public-install', 'public-enable', 'public-disable', 'bedrock-prepare', 'bedrock-budget'];
export function authorizeCommand(command: string, apply: boolean): void {
  requireThat(commands.includes(command), 'Unknown AWS command.');
  requireThat(!['create', 'configure', 'install', 'verify', 'dns', 'public-prepare', 'public-master-prepare', 'public-install', 'public-enable', 'public-disable', 'bedrock-prepare', 'bedrock-budget'].includes(command) || apply, `${command} requires --apply. Review the runbook and costs first. Nothing executed.`);
}
export async function awsMain(command: string, options: Options['values'], signal?: AbortSignal): Promise<void> {
  only(options, command === 'public-prepare' ? ['config', 'profile', 'apply', 'access'] : command === 'bedrock-prepare' ? ['config', 'profile', 'apply', 'allow-eu-inference'] : command === 'bedrock-budget' ? ['config', 'profile', 'apply', 'monthly-usd'] : ['config', 'profile', 'apply']);
  authorizeCommand(command, options.apply === true); // Before config, client, identity or any AWS operation.
  const path = String(options.config ?? join(ROOT, 'infra/aws/lab.config.json'));
  let value: unknown;
  try { value = JSON.parse(await readFile(path, 'utf8')); }
  catch { throw new LabError('Cannot read configuration JSON; copy and edit infra/aws/lab.config.example.json.'); }
  const c = validateConfig(value);
  const aws = new AWS(c, options.profile ? String(options.profile) : undefined, (file, args, cwd, env) => execute(file, args, cwd, env, signal));
  await identity(aws, c);
  if (command === 'public-master-prepare') return (await import('./public.js')).prepareMaster(aws, c);
  if (command === 'bedrock-prepare') return (await import('./bedrock.js')).prepareBedrock(aws, c, options['allow-eu-inference'] === true);
  if (command === 'bedrock-budget') return (await import('./bedrock.js')).setBedrockBudget(aws, c, options['monthly-usd']);
  if (command.startsWith('public-')) return (await import('./public.js')).publicMain(command, aws, c,
    options.profile ? String(options.profile) : undefined, options.access ? String(options.access) : 'viewer');
  const actions: Record<string, (aws: AwsClient, c: Config) => Promise<void>> = { preflight, create, configure, install, dns };
  if (actions[command]) return actions[command](aws, c);
  const o = await outputs(aws, c);
  if (command === 'verify') { await online(aws, o); await runCommand(aws, o.MonitorInstanceId, 'bash /opt/incidentlab/current/infra/aws/verify-node.sh', 180); }
  else if (command === 'status') { console.log(JSON.stringify(o, null, 2)); await online(aws, o); console.log('Both SSM agents Online; this is not application verification.'); }
  else {
    console.log('Open http://localhost:8010 on THIS computer while the tunnel runs. Ctrl+C closes it.');
    await interactive('aws', [...aws.prefix, 'ssm', 'start-session', '--target', o.MonitorInstanceId, '--document-name', 'AWS-StartPortForwardingSessionToRemoteHost',
      '--parameters', JSON.stringify({ host: [o.MonitorPrivateIp], portNumber: ['8010'], localPortNumber: ['8010'] })], { ...process.env, AWS_PAGER: '', AWS_CLI_AUTO_PROMPT: 'off' }, signal);
  }
}
