import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { ROOT, type Obj, object, array, str, temporary, execute } from '../common.js';
import { AWS, AwsError, type AwsClient, type Config, validateConfig, plugins, authorizeCommand, preflight, outputs, configure, getConfig, parameterName,
  validateSecrets, dns, online, installationScript, archive, privateIPv4 } from '../aws.js';

const C: Config = { account_id: '123456789012', region: 'eu-west-1', stack_name: 'incidentlab-test', vpc_id: 'vpc-12345678', subnet_id: 'subnet-12345678',
  compose_version: 'v2.39.4', compose_sha256: 'a'.repeat(64), buildx_version: 'v0.28.0', buildx_sha256: 'b'.repeat(64), private_zone_id: 'Z123TEST', dns_name: 'monitor.lab.example.com' };
const O = { MonitorInstanceId: 'i-12345678', TargetInstanceId: 'i-abcdef12', MonitorPrivateIp: '10.0.1.10', TargetPrivateIp: '10.0.1.11', DeploymentBucket: 'incidentlab-test-bucket' };
test('target shared memory accommodates the bounded memory fault below its cgroup cap', async () => {
  const compose = await readFile(join(ROOT, 'compose.target.yml'), 'utf8');
  const sharedMiB = Number(compose.match(/^\s+shm_size: (\d+)m$/m)?.[1]);
  const limitMiB = Number(compose.match(/^\s+mem_limit: (\d+)m$/m)?.[1]);
  assert.ok(sharedMiB > 128 && sharedMiB < limitMiB, '128 MiB mapped fault requires /dev/shm headroom');
});
class FakeAWS implements AwsClient {
  calls: string[][] = []; writes: { service: string; operation: string; payload: Obj }[] = []; parameters: Record<string, Obj> = {};
  account = C.account_id; subnet = { VpcId: C.vpc_id, State: 'available' }; dnsEnabled = true;
  stack: Obj = { StackStatus: 'CREATE_COMPLETE', Parameters: [{ ParameterKey: 'VpcId', ParameterValue: C.vpc_id }, { ParameterKey: 'SubnetId', ParameterValue: C.subnet_id }],
    Outputs: Object.entries(O).map(([OutputKey, OutputValue]) => ({ OutputKey, OutputValue })) };
  zone: Obj = { HostedZone: { Name: 'lab.example.com.', Config: { PrivateZone: true } }, VPCs: [{ VPCId: C.vpc_id, VPCRegion: C.region }] };
  records: Obj[] = []; instances: Obj[] = [O.MonitorInstanceId, O.TargetInstanceId].map(InstanceId => ({ InstanceId, PingStatus: 'Online' }));
  async call(...args: string[]): Promise<Obj> {
    this.calls.push(args);
    switch (args.slice(0, 2).join(' ')) {
      case 'sts get-caller-identity': return { Account: this.account };
      case 'ec2 describe-subnets': return { Subnets: [this.subnet] };
      case 'ec2 describe-vpc-attribute': { const key = args.at(-1)!; return { [key[0].toUpperCase() + key.slice(1)]: { Value: this.dnsEnabled } }; }
      case 'cloudformation validate-template': return {};
      case 'cloudformation describe-stacks': return { Stacks: [this.stack] };
      case 'ssm get-parameter': {
        const value = this.parameters[args[args.indexOf('--name') + 1]];
        if (!value) throw new AwsError('get-parameter', 'ParameterNotFound'); return { Parameter: value };
      }
      case 'ssm describe-instance-information': return { InstanceInformationList: this.instances };
      case 'route53 get-hosted-zone': return this.zone;
      case 'route53 list-resource-record-sets': return { ResourceRecordSets: this.records };
      default: throw new Error(`Unexpected offline operation: ${args.slice(0, 2).join(' ')}`);
    }
  }
  async input(service: string, operation: string, payload: Obj): Promise<Obj> {
    this.writes.push({ service, operation, payload: structuredClone(payload) });
    if (service === 'ssm' && operation === 'put-parameter') this.parameters[str(payload.Name)] = { Type: payload.Type, Value: payload.Value };
    return {};
  }
}

test('configuration rejects placeholders, injection and non-objects', () => {
  for (const c of [[], { ...C, account_id: 'REPLACE' }, { ...C, stack_name: 'x; touch file' }, { ...C, subnet_id: 123 }]) assert.throws(() => validateConfig(c));
  assert.deepEqual(validateConfig(C), C);
});
test('plugins require pinned versions and checksums', () => {
  plugins(C);
  const changes: Obj[] = [{ compose_sha256: 'REPLACE' }, { buildx_version: 'latest' }, { compose_sha256: null }];
  for (const change of changes) assert.throws(() => plugins({ ...C, ...change }));
});
test('write commands require apply before any AWS client or call', () => {
  for (const command of ['create', 'configure', 'install', 'verify', 'dns']) {
    assert.throws(() => authorizeCommand(command, false), /requires --apply/); authorizeCommand(command, true);
  }
  for (const command of ['status', 'preflight', 'tunnel']) authorizeCommand(command, false);
  assert.throws(() => authorizeCommand('destroy', true));
});
test('account mismatch stops preflight immediately', async () => {
  const aws = new FakeAWS(); aws.account = '999999999999';
  await assert.rejects(preflight(aws, C), /Account mismatch/); assert.equal(aws.calls.length, 1); assert.equal(aws.writes.length, 0);
});
test('preflight checks subnet and DNS without changing them', async () => {
  const aws = new FakeAWS(); await preflight(aws, C);
  aws.subnet.VpcId = 'vpc-abcdef12'; await assert.rejects(preflight(aws, C));
  aws.subnet.VpcId = C.vpc_id; aws.dnsEnabled = false; await assert.rejects(preflight(aws, C)); assert.equal(aws.writes.length, 0);
});
test('stack outputs reject different networks, unfinished or old stacks', async () => {
  const aws = new FakeAWS(); assert.deepEqual(await outputs(aws, C), O);
  array(aws.stack.Parameters)[0].ParameterValue = 'vpc-abcdef12'; await assert.rejects(outputs(aws, C));
  array(aws.stack.Parameters)[0].ParameterValue = C.vpc_id; (aws.stack.Outputs as Obj[]).pop(); await assert.rejects(outputs(aws, C));
  aws.stack.StackStatus = 'CREATE_IN_PROGRESS'; await assert.rejects(outputs(aws, C));
});
test('private endpoints must be RFC1918 IPv4, never loopback, public or link-local', () => {
  for (const ip of ['10.0.1.2', '172.16.1.2', '172.31.1.2', '192.168.0.1']) assert.equal(privateIPv4(ip), true);
  for (const ip of ['127.0.0.1', '169.254.169.254', '8.8.8.8', '172.32.0.1', '::1', '10.0.0.999']) assert.equal(privateIPv4(ip), false);
});
test('configuration preserves secrets without putting values in CLI arguments or logs', async t => {
  const log = t.mock.method(console, 'log', () => {}); const aws = new FakeAWS(); await configure(aws, C);
  const first = object(await getConfig(aws, C, 'monitor')); const target = object(await getConfig(aws, C, 'target'));
  assert.ok(!Object.hasOwn(target, 'POSTGRES_PASSWORD')); assert.equal(target.LAB_INGEST_KEY, first.LAB_INGEST_KEY);
  await configure(aws, C); assert.deepEqual(await getConfig(aws, C, 'monitor'), first);
  for (const key of ['LAB_INGEST_KEY', 'LAB_CONTROL_KEY', 'LAB_WORKER_KEY', 'POSTGRES_PASSWORD']) {
    assert.equal(str(first[key]).length, 64); assert.ok(!JSON.stringify(aws.calls).includes(str(first[key])));
    assert.ok(!JSON.stringify(log.mock.calls.map(c => c.arguments)).includes(str(first[key])));
  }
});
test('mismatched keys or missing monitor configuration never rotate secrets', async () => {
  const aws = new FakeAWS(); await configure(aws, C);
  const target = object(await getConfig(aws, C, 'target')); target.LAB_INGEST_KEY = 'f'.repeat(64);
  aws.parameters[parameterName(C, 'target')].Value = JSON.stringify(target); const before = aws.writes.length;
  await assert.rejects(configure(aws, C)); delete aws.parameters[parameterName(C, 'monitor')];
  await assert.rejects(configure(aws, C)); assert.equal(aws.writes.length, before);
});
test('partial monitor-only configuration is recoverable without rotation', async () => {
  const aws = new FakeAWS(); await configure(aws, C); const original = await getConfig(aws, C, 'monitor');
  delete aws.parameters[parameterName(C, 'target')]; await configure(aws, C); assert.deepEqual(await getConfig(aws, C, 'monitor'), original);
});
test('plaintext parameters and invalid secret JSON are refused without disclosure', async () => {
  const aws = new FakeAWS();
  for (const parameter of [{ Type: 'String', Value: '{}' }, { Type: 'SecureString', Value: '[]' }, { Type: 'SecureString', Value: 'secret-invalid-json' }]) {
    aws.parameters[parameterName(C, 'monitor')] = parameter;
    await assert.rejects(getConfig(aws, C, 'monitor'), error => !String(error).includes('secret-invalid-json'));
  }
  assert.throws(() => validateSecrets({ key: null }, ['key']));
});
test('private DNS creates once and identical records are no-ops', async () => {
  const aws = new FakeAWS(); await dns(aws, C);
  const change = array(object(aws.writes[0].payload.ChangeBatch).Changes)[0];
  assert.equal(change.Action, 'CREATE'); assert.deepEqual(object(change.ResourceRecordSet).ResourceRecords, [{ Value: O.MonitorPrivateIp }]);
  aws.records = [object(change.ResourceRecordSet)]; await dns(aws, C); assert.equal(aws.writes.length, 1);
});
test('public, unrelated or conflicting DNS is never modified', async () => {
  for (const mutation of ['public', 'vpc', 'domain', 'record', 'label']) {
    const aws = new FakeAWS(); const c = { ...C };
    if (mutation === 'public') object(object(aws.zone.HostedZone).Config).PrivateZone = false;
    if (mutation === 'vpc') aws.zone.VPCs = [];
    if (mutation === 'domain') c.dns_name = 'monitor.other.com';
    if (mutation === 'label') c.dns_name = 'invalid..lab.example.com';
    if (mutation === 'record') aws.records = [{ Name: C.dns_name + '.', Type: 'CNAME' }];
    await assert.rejects(dns(aws, c)); assert.equal(aws.writes.length, 0);
  }
});
test('both exact hosts must be online', async () => {
  const aws = new FakeAWS(); await online(aws, O); aws.instances.pop(); await assert.rejects(online(aws, O));
});
test('installation script checks the release and passes parameter names, never secrets', async () => {
  const script = installationScript(C, O, 'monitor', 'c'.repeat(64));
  assert.match(script, /sha256sum --check --status/); assert.match(script, /tar --no-same-owner --same-permissions/);
  assert.ok(script.includes(parameterName(C, 'monitor'))); assert.doesNotMatch(script, /POSTGRES_PASSWORD|LAB_WORKER_KEY/);
  await temporary('incidentlab-script-test-', async directory => {
    const path = join(directory, 'install.sh'); await writeFile(path, script); assert.equal((await execute('bash', ['-n', path])).code, 0);
  });
});
test('AWS secret input file is private and removed on success and failure', async () => {
  for (const fail of [false, true]) {
    let seen = '';
    const aws = new AWS(C, undefined, async (_file, args) => {
      seen = args.at(-1)!.replace(/^file:\/\//, '');
      assert.equal((await stat(seen)).mode & 0o777, 0o600); assert.equal((await stat(dirname(seen))).mode & 0o777, 0o700);
      assert.equal(JSON.parse(await readFile(seen, 'utf8')).Value, 'test-secret'); assert.ok(!args.join(' ').includes('test-secret'));
      return { code: fail ? 1 : 0, stdout: '{}', stderr: '(AccessDeniedException) test-secret' };
    });
    if (fail) await assert.rejects(aws.input('ssm', 'put-parameter', { Value: 'test-secret' }), error => !String(error).includes('test-secret'));
    else await aws.input('ssm', 'put-parameter', { Value: 'test-secret' });
    await assert.rejects(stat(seen));
  }
});
test('release archive includes committed allowlist and jq filter, rejects dirty or secret files', async () => {
  await temporary('incidentlab-git-test-', async root => {
    const git = async (...args: string[]) => { const result = await execute('git', ['-C', root, ...args]); assert.equal(result.code, 0); };
    const commit = async () => { await git('add', '.'); await git('-c', 'user.name=Offline Test', '-c', 'user.email=test@example.invalid', 'commit', '-m', 'fixture'); };
    await git('init');
    for (const name of ['backend/Dockerfile', 'frontend/package.json', 'target/Dockerfile', '.dockerignore', 'Directory.Build.props',
      'compose.monitor.yml', 'compose.target.yml', 'compose.public.yml', 'infra/aws/install-node.sh', 'infra/aws/verify-node.sh', 'infra/aws/write-env.jq', 'infra/aws/install-public.sh', 'infra/aws/verify-public.sh']) {
      const path = join(root, name); await mkdir(dirname(path), { recursive: true }); await writeFile(path, 'test\n');
    }
    await commit(); await writeFile(join(root, '.env'), 'untracked-secret'); const destination = join(root, 'bundle.tar.gz');
    assert.match(await archive(destination, root), /^[a-f0-9]{64}$/);
    const listing = (await execute('tar', ['-tzf', destination])).stdout.split('\n');
    assert.ok(!listing.includes('.env')); assert.ok(listing.includes('Directory.Build.props')); assert.ok(listing.includes('infra/aws/write-env.jq'));
    await writeFile(join(root, 'backend/Dockerfile'), 'changed'); await assert.rejects(archive(destination, root), /Commit reviewed/);
    await git('add', 'backend/Dockerfile'); await git('-c', 'user.name=Offline Test', '-c', 'user.email=test@example.invalid', 'commit', '-m', 'change');
    await writeFile(join(root, 'backend/.env'), 'tracked-secret'); await git('add', 'backend/.env');
    await git('-c', 'user.name=Offline Test', '-c', 'user.email=test@example.invalid', 'commit', '-m', 'unsafe fixture');
    await assert.rejects(archive(destination, root), /Unsafe tracked/);
  });
});
test('cancelling an AWS CLI subprocess still removes its private input file', async () => {
  const controller = new AbortController(); let seen = '';
  const aws = new AWS(C, undefined, async (_file, args, cwd, env) => {
    seen = args.at(-1)!.replace(/^file:\/\//, '');
    assert.equal((await stat(seen)).mode & 0o777, 0o600);
    const timer = setTimeout(() => controller.abort(), 100);
    try { return await execute(process.execPath, ['-e', 'setInterval(()=>{},1000)'], cwd, env, controller.signal); }
    finally { clearTimeout(timer); }
  });
  await assert.rejects(aws.input('ssm', 'put-parameter', { Value: 'test-secret' }));
  await assert.rejects(stat(seen));
});
test('CloudFormation template invariants: private ingress, retained private bucket and valid references', async () => {
  const text = await readFile(join(ROOT, 'infra/aws/lab-infrastructure.json'), 'utf8'); const template = object(JSON.parse(text));
  assert.equal(template.AWSTemplateFormatVersion, '2010-09-09'); const resources = object(template.Resources); const parameters = object(template.Parameters);
  const bucket = object(resources.DeploymentBucket); assert.equal(bucket.DeletionPolicy, 'Retain');
  assert.ok(Object.values(object(object(bucket.Properties).PublicAccessBlockConfiguration)).every(v => v === true));
  for (const value of Object.values(resources)) {
    const r = object(value); const p = object(r.Properties);
    if (r.Type === 'AWS::EC2::SecurityGroup') for (const ingress of array(p.SecurityGroupIngress ?? [])) assert.ok(!('CidrIp' in ingress) && !('CidrIpv6' in ingress));
    if (r.Type === 'AWS::EC2::SecurityGroupIngress') assert.ok(!('CidrIp' in p) && !('CidrIpv6' in p));
    if (r.Type === 'AWS::EC2::Instance') {
      assert.equal(object(p.MetadataOptions).HttpTokens, 'required'); assert.equal(object(p.CreditSpecification).CPUCredits, 'standard');
      assert.ok(array(p.BlockDeviceMappings).every(d => object(d.Ebs).Encrypted === true));
    }
  }
  const ref = (name: string) => assert.ok(name.startsWith('AWS::') || name in resources || name in parameters, `Unknown reference ${name}`);
  const visit = (value: unknown) => {
    if (Array.isArray(value)) return value.forEach(visit);
    if (value === null || typeof value !== 'object') return;
    const o = value as Obj;
    if (o.Ref) ref(str(o.Ref));
    if (o['Fn::GetAtt']) { const v = o['Fn::GetAtt']; ref(Array.isArray(v) ? str(v[0]) : str(v).split('.')[0]); }
    if (typeof o['Fn::Sub'] === 'string') for (const m of o['Fn::Sub'].matchAll(/\$\{([^}]+)\}/g)) ref(m[1].split('.')[0]);
    Object.values(o).forEach(visit);
  };
  visit(template); assert.doesNotMatch(text, /python|cloudshell\.py/i);
});
test('jq dotenv conversion rejects wrong type, extra fields and interpolation without leaking values', async () => {
  await temporary('incidentlab-jq-test-', async directory => {
    const values: Obj = { LAB_INGEST_KEY: 'a'.repeat(64), LAB_CONTROL_KEY: 'b'.repeat(64), MONITOR_URL: 'http://10.0.1.10:8010', TARGET_BIND_ADDRESS: '10.0.1.11' };
    const path = join(directory, 'parameter.json');
    const convert = async (v: Obj, type = 'SecureString') => {
      await writeFile(path, JSON.stringify({ Parameter: { Type: type, Value: JSON.stringify(v) } }));
      return execute('jq', ['-er', '--arg', 'role', 'target', '-f', join(ROOT, 'infra/aws/write-env.jq'), path]);
    };
    const good = await convert(values); assert.equal(good.code, 0); assert.ok(good.stdout.includes('MONITOR_URL=http://10.0.1.10:8010\n'));
    for (const bad of [{ ...values, EXTRA: 'x' }, { ...values, LAB_CONTROL_KEY: 'secret\nINJECT=x' }, { ...values, LAB_CONTROL_KEY: '${SECRET}' }, { ...values, TARGET_BIND_ADDRESS: null }]) {
      const result = await convert(bad); assert.notEqual(result.code, 0); assert.equal(result.stdout, ''); assert.doesNotMatch(result.stderr, /secret\n|INJECT|SECRET/);
    }
    assert.notEqual((await convert(values, 'String')).code, 0);
  });
});
