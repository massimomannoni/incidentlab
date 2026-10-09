import { mkdir, readFile, writeFile, lstat } from 'node:fs/promises';
import { join } from 'node:path';
import { createHmac, pbkdf2Sync, randomBytes } from 'node:crypto';
import { AWS, AwsError, type AwsClient, type Config, outputs, getConfig, parameterName, identity, online, plugins, archive, installationScript, runCommand } from './aws.js';
import { ROOT, type Obj, array, object, str, requireThat, secureJson, temporary, shellQuote } from './common.js';

export const publicCommands = ['public-prepare', 'public-master-prepare', 'public-status', 'public-install', 'public-enable', 'public-disable'];
const edgeName = (c: Config) => `${c.stack_name}-public-edge`;
async function stack(aws: AwsClient, name: string): Promise<Obj | null> {
  try { return array((await aws.call('cloudformation', 'describe-stacks', '--stack-name', name)).Stacks)[0] ?? null; }
  catch (e) { if (e instanceof AwsError && e.code === 'ValidationError') return null; throw e; }
}
const stackOutputs = (s: Obj) => Object.fromEntries(array(s.Outputs ?? []).map(o => [str(o.OutputKey), str(o.OutputValue)]));
function ready(s: Obj | null): asserts s is Obj { requireThat(s && ['CREATE_COMPLETE', 'UPDATE_COMPLETE'].includes(str(s.StackStatus)), 'Public stack not ready; inspect status before retrying.'); }
export function validatePublic(p: Obj): void {
  if ('PUBLIC_MASTER_ADMISSION_KEY' in p) requireThat(!!p.PUBLIC_MASTER_USERNAME && /^[a-f0-9]{64}$/.test(str(p.PUBLIC_MASTER_ADMISSION_KEY)), 'Invalid master admission configuration (values withheld).');
  requireThat(/^[a-zA-Z0-9_-]{3,64}$/.test(str(p.PUBLIC_USERNAME)) && /^[a-f0-9]{64}$/.test(str(p.PUBLIC_PASSWORD_HASH)) &&
    /^[a-f0-9]{32}$/.test(str(p.PUBLIC_PASSWORD_SALT)) && /^[a-f0-9]{64}$/.test(str(p.PUBLIC_ORIGIN_KEY)) &&
    p.PUBLIC_ACCESS === 'viewer', 'Invalid public configuration; shared access must be viewer. Review privately; do not reset credentials.');
  const masterKeys = ['PUBLIC_MASTER_USERNAME', 'PUBLIC_MASTER_PASSWORD_SALT', 'PUBLIC_MASTER_PASSWORD_HASH'];
  if (masterKeys.some(k => k in p)) requireThat(masterKeys.every(k => typeof p[k] === 'string') &&
    /^[a-zA-Z0-9_-]{3,64}$/.test(String(p.PUBLIC_MASTER_USERNAME)) && /^[a-f0-9]{32}$/.test(String(p.PUBLIC_MASTER_PASSWORD_SALT)) &&
    /^[a-f0-9]{64}$/.test(String(p.PUBLIC_MASTER_PASSWORD_HASH)) && String(p.PUBLIC_MASTER_USERNAME).toLowerCase() !== String(p.PUBLIC_USERNAME).toLowerCase(),
    'Invalid personal master configuration (values withheld).');
}
export function masterAdmissionKey(controlKey: string): string {
  requireThat(/^[a-f0-9]{64}$/.test(controlKey), 'Invalid Monitor control key (value withheld).');
  return createHmac('sha256', controlKey).update('incidentlab:master-simulation-admission:v1').digest('hex');
}
export async function prepareMaster(aws: AwsClient, c: Config, directory = join(ROOT, '.local')): Promise<void> {
  const p = await publicConfig(aws, c);
  if (p.PUBLIC_MASTER_USERNAME) { console.log('Personal master already configured; credentials unchanged. Run public-install to deploy.'); return; }
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const path = join(directory, 'master-login.json');
  const generated = { account_id: c.account_id, stack_name: c.stack_name, username: 'master', password: randomBytes(24).toString('hex'),
    salt: randomBytes(16).toString('hex'), access: 'master', session_minutes: 60 };
  try { await writeFile(path, JSON.stringify(generated, null, 2) + '\n', { mode: 0o600, flag: 'wx' }); }
  catch (e) { if ((e as NodeJS.ErrnoException).code !== 'EEXIST') throw e; }
  const metadata = await lstat(path);
  requireThat(metadata.isFile() && !metadata.isSymbolicLink() && (metadata.mode & 0o077) === 0, 'Master credential file must be a private regular file; not overwritten.');
  // Reuse the same local secret after an uncertain SSM outcome; never silently rotate.
  let saved: Obj;
  try { saved = object(JSON.parse(await readFile(path, 'utf8'))); }
  catch { throw new Error('Cannot read local master credentials (values withheld); file not overwritten.'); }
  requireThat(saved.account_id === c.account_id && saved.stack_name === c.stack_name && saved.username === 'master' &&
    /^[a-f0-9]{48}$/.test(String(saved.password)) && /^[a-f0-9]{32}$/.test(String(saved.salt)), 'Local master credentials do not match this deployment (values withheld).');
  p.PUBLIC_MASTER_USERNAME = saved.username; p.PUBLIC_MASTER_PASSWORD_SALT = saved.salt;
  p.PUBLIC_MASTER_PASSWORD_HASH = pbkdf2Sync(String(saved.password), Buffer.from(String(saved.salt), 'hex'), 600_000, 32, 'sha256').toString('hex');
  delete p.PUBLIC_READY_DIGEST;
  await saveConfig(aws, c, p);
  console.log('Personal master prepared. Credentials: .local/master-login.json (owner-only, ignored). AWS stores hashes only. Run public-install to deploy.');
}
async function saveConfig(aws: AwsClient, c: Config, p: Obj): Promise<void> {
  validatePublic(p);
  await aws.input('ssm', 'put-parameter', { Name: parameterName(c, 'public'), Type: 'SecureString', Value: JSON.stringify(p), Overwrite: true,
    Description: 'IncidentLab public gateway; password hash only; never print values' });
}
async function publicConfig(aws: AwsClient, c: Config): Promise<Obj> {
  const p = await getConfig(aws, c, 'public'); requireThat(p, 'Run public-prepare first.'); validatePublic(p); return p;
}
async function prepare(aws: AwsClient, edge: AwsClient, c: Config, access: string): Promise<void> {
  requireThat(access === 'viewer', 'Shared public access must be viewer; administration requires the private SSM tunnel.');
  const o = await outputs(aws, c); await online(aws, o);
  let p = await getConfig(aws, c, 'public');
  if (!p) {
    // Plaintext is written ONCE to an ignored, owner-only operator file, never to AWS or console output.
    const password = randomBytes(24).toString('hex'); const salt = randomBytes(16).toString('hex');
    p = { PUBLIC_USERNAME: 'visitor', PUBLIC_PASSWORD_SALT: salt,
      PUBLIC_PASSWORD_HASH: pbkdf2Sync(password, Buffer.from(salt, 'hex'), 600_000, 32, 'sha256').toString('hex'),
      PUBLIC_ORIGIN_KEY: randomBytes(32).toString('hex'), PUBLIC_HOST: 'pending.invalid', PUBLIC_ACCESS: access,
      MONITOR_BIND_ADDRESS: o.MonitorPrivateIp };
    await mkdir(join(ROOT, '.local'), { recursive: true, mode: 0o700 });
    await secureJson(join(ROOT, '.local/shared-login.json'), { username: p.PUBLIC_USERNAME, password, session_minutes: 60, access });
    await saveConfig(aws, c, p);
    console.log('Credentials created in .local/shared-login.json (owner-only, ignored). No plaintext password stored in AWS.');
  }
  validatePublic(p); requireThat(p.PUBLIC_ACCESS === access, 'Existing access differs; explicit credential/configuration review required.');
  const existing = await stack(edge, edgeName(c));
  if (existing) { console.log(`Existing public stack: ${existing.StackStatus}. Not replaced or updated.`); return; }
  const rows = array((await aws.call('ec2', 'describe-instances', '--instance-ids', o.MonitorInstanceId)).Reservations);
  const instance = array(rows[0].Instances)[0];
  requireThat(instance.VpcId === c.vpc_id && instance.SubnetId === c.subnet_id && object(instance.State).Name === 'running', 'Monitor mismatch/unavailable.');
  await edge.call('cloudformation', 'validate-template', '--template-body', `file://${join(ROOT, 'infra/aws/public-edge.json')}`);
  await edge.input('cloudformation', 'create-stack', { StackName: edgeName(c), EnableTerminationProtection: true,
    TemplateBody: await readFile(join(ROOT, 'infra/aws/public-edge.json'), 'utf8'),
    Tags: [{ Key: 'Project', Value: c.stack_name }], Parameters: [
      { ParameterKey: 'MonitorArn', ParameterValue: `arn:aws:ec2:${c.region}:${c.account_id}:instance/${o.MonitorInstanceId}` },
      { ParameterKey: 'MonitorPrivateDns', ParameterValue: instance.PrivateDnsName },
      { ParameterKey: 'ProjectName', ParameterValue: c.stack_name }, { ParameterKey: 'OriginKey', ParameterValue: p.PUBLIC_ORIGIN_KEY },
      { ParameterKey: 'PublicEnabled', ParameterValue: 'false' },
    ] });
  console.log('Public edge creation started DISABLED. WAF is billable; VPC origin can take 15 minutes. Use public-status.');
}
export function publicInstallScript(c: Config, digest: string): string {
  requireThat(/^[a-f0-9]{64}$/.test(digest), 'Invalid release digest.');
  return `bash ${shellQuote(`/opt/incidentlab/releases/${digest}/infra/aws/install-public.sh`)} ${shellQuote(c.region)} ${shellQuote(parameterName(c, 'public'))}`;
}
async function installPublic(aws: AwsClient, edge: AwsClient, c: Config): Promise<void> {
  plugins(c); const s = await stack(edge, edgeName(c)); ready(s); const e = stackOutputs(s);
  requireThat(/^d[a-z0-9]+\.cloudfront\.net$/.test(e.PublicHost), 'Unexpected public hostname.');
  const o = await outputs(aws, c); await online(aws, o); const p = await publicConfig(aws, c);
  p.PUBLIC_HOST = e.PublicHost; p.MONITOR_BIND_ADDRESS = o.MonitorPrivateIp;
  if (p.PUBLIC_MASTER_USERNAME) {
    const monitor = await getConfig(aws, c, 'monitor');
    p.PUBLIC_MASTER_ADMISSION_KEY = masterAdmissionKey(str(monitor?.LAB_CONTROL_KEY));
  }
  delete p.PUBLIC_READY_DIGEST; await saveConfig(aws, c, p);
  const groups = array((await aws.call('ec2', 'describe-security-groups', '--filters', JSON.stringify([
    { Name: 'vpc-id', Values: [c.vpc_id] }, { Name: 'group-name', Values: ['CloudFront-VPCOrigins-Service-SG*'] }]))).SecurityGroups);
  requireThat(groups.length === 1 && groups[0].GroupName === 'CloudFront-VPCOrigins-Service-SG', 'Expected exactly one AWS-managed CloudFront VPC origin group; inspect manually.');
  const resources = array((await aws.call('cloudformation', 'list-stack-resources', '--stack-name', c.stack_name)).StackResourceSummaries);
  const monitorGroup = resources.find(r => r.LogicalResourceId === 'MonitorSecurityGroup')?.PhysicalResourceId;
  requireThat(typeof monitorGroup === 'string' && /^sg-[a-f0-9]+$/.test(monitorGroup), 'Monitor security group missing.');
  const accessName = `${c.stack_name}-public-access`; const accessStack = await stack(aws, accessName);
  if (!accessStack) {
    await aws.input('cloudformation', 'create-stack', { StackName: accessName, EnableTerminationProtection: true,
      TemplateBody: await readFile(join(ROOT, 'infra/aws/public-access.json'), 'utf8'), Tags: [{ Key: 'Project', Value: c.stack_name }],
      Parameters: [{ ParameterKey: 'MonitorGroup', ParameterValue: monitorGroup }, { ParameterKey: 'CloudFrontGroup', ParameterValue: groups[0].GroupId }] });
    await aws.call('cloudformation', 'wait', 'stack-create-complete', '--stack-name', accessName);
  } else {
    ready(accessStack);
    const parameters = Object.fromEntries(array(accessStack.Parameters).map(v => [str(v.ParameterKey), str(v.ParameterValue)]));
    requireThat(parameters.MonitorGroup === monitorGroup && parameters.CloudFrontGroup === groups[0].GroupId, 'Public access stack points to different groups; refusing.');
  }
  const digest = await temporary('incidentlab-public-release-', async directory => {
    const bundle = join(directory, 'source.tar.gz'); const hash = await archive(bundle);
    await aws.call('s3api', 'put-object', '--bucket', o.DeploymentBucket, '--key', `releases/${hash}.tar.gz`, '--body', bundle, '--server-side-encryption', 'AES256');
    return hash;
  });
  // Refresh the Monitor's UI, not the Target or databases. No volumes are deleted.
  await runCommand(aws, o.MonitorInstanceId, installationScript(c, o, 'monitor', digest));
  await runCommand(aws, o.MonitorInstanceId, publicInstallScript(c, digest));
  p.PUBLIC_READY_DIGEST = digest; await saveConfig(aws, c, p);
  console.log('Gateway installed and anonymous-origin checks passed. Public edge is not automatically enabled.');
}
async function toggle(aws: AwsClient, edge: AwsClient, c: Config, enabled: boolean): Promise<void> {
  const s = await stack(edge, edgeName(c)); ready(s); const e = stackOutputs(s);
  if (enabled) {
    const p = await publicConfig(aws, c); requireThat(/^[a-f0-9]{64}$/.test(str(p.PUBLIC_READY_DIGEST ?? '')) && p.PUBLIC_HOST === e.PublicHost, 'Install/verify gateway before enabling.');
    const o = await outputs(aws, c); await online(aws, o);
    await runCommand(aws, o.MonitorInstanceId, 'bash /opt/incidentlab/public-current/infra/aws/verify-public.sh', 90);
  }
  const parameters = array(s.Parameters).map((p): Obj => {
    if (p.ParameterKey === 'PublicEnabled') return { ParameterKey: 'PublicEnabled', ParameterValue: String(enabled) };
    return { ParameterKey: p.ParameterKey, UsePreviousValue: true };
  });
  const current = array(s.Parameters).find(p => p.ParameterKey === 'PublicEnabled')?.ParameterValue;
  if (current === String(enabled)) { console.log(`Public edge already ${enabled ? 'enabled' : 'disabled'}: https://${e.PublicHost}`); return; }
  await edge.input('cloudformation', 'update-stack', { StackName: edgeName(c), UsePreviousTemplate: true, Parameters: parameters });
  console.log(`Public edge ${enabled ? 'enable' : 'disable'} requested. Wait for public-status to show UPDATE_COMPLETE: https://${e.PublicHost}`);
}
export async function publicMain(command: string, aws: AwsClient, c: Config, profile?: string, access = 'viewer'): Promise<void> {
  const edge = new AWS({ ...c, region: 'us-east-1' }, profile); await identity(edge, c);
  if (command === 'public-prepare') return prepare(aws, edge, c, access);
  if (command === 'public-install') return installPublic(aws, edge, c);
  if (command === 'public-enable' || command === 'public-disable') return toggle(aws, edge, c, command === 'public-enable');
  const s = await stack(edge, edgeName(c));
  console.log(JSON.stringify(s ? { status: s.StackStatus, enabled: array(s.Parameters).find(p => p.ParameterKey === 'PublicEnabled')?.ParameterValue, ...stackOutputs(s) } : { status: 'not-created' }, null, 2));
}
