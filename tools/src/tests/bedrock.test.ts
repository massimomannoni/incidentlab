import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { prepareBedrock, setBedrockBudget, bedrockModel, euRegions } from '../bedrock.js';
import { authorizeCommand, type AwsClient, type Config, AwsError } from '../aws.js';
import { ROOT, type Obj, object, array, str, temporary, execute, parse } from '../common.js';

const c: Config = { account_id: '123456789012', region: 'eu-south-1', stack_name: 'incidentlab-test', vpc_id: 'vpc-12345678', subnet_id: 'subnet-12345678' };
const outputs = { MonitorInstanceId: 'i-12345678', TargetInstanceId: 'i-abcdef12', MonitorPrivateIp: '10.0.1.10', TargetPrivateIp: '10.0.1.11', DeploymentBucket: 'incidentlab-test-bucket' };
class FakeAWS implements AwsClient {
  writes: Obj[] = []; calls: string[][] = []; exists = false; foreign = false; wrongTarget = false;
  monitor: Obj = { LAB_WORKER_KEY: 'preserve-test-value', POSTGRES_PASSWORD: 'preserve-db-test-value' };
  async call(...args: string[]): Promise<Obj> {
    this.calls.push(args);
    switch (args.slice(0, 2).join(' ')) {
      case 'sts get-caller-identity': return { Account: c.account_id };
      case 'cloudformation describe-stacks':
        if (args.at(-1) === c.stack_name) return { Stacks: [{ StackStatus: 'CREATE_COMPLETE', Parameters: [{ ParameterKey: 'VpcId', ParameterValue: c.vpc_id }, { ParameterKey: 'SubnetId', ParameterValue: c.subnet_id }], Outputs: Object.entries(outputs).map(([OutputKey, OutputValue]) => ({ OutputKey, OutputValue })) }] };
        if (!this.exists) throw new AwsError('describe-stacks', 'ValidationError');
        return { Stacks: [{ StackStatus: 'CREATE_COMPLETE', Parameters: [{ ParameterKey: 'InstanceRoleName', ParameterValue: 'test-role' }, { ParameterKey: 'MonitorInstanceId', ParameterValue: this.wrongTarget ? outputs.TargetInstanceId : outputs.MonitorInstanceId }] }] };
      case 'ssm describe-instance-information': return { InstanceInformationList: [outputs.MonitorInstanceId, outputs.TargetInstanceId].map(InstanceId => ({ InstanceId, PingStatus: 'Online' })) };
      case 'bedrock get-inference-profile': return { status: 'ACTIVE', inferenceProfileId: bedrockModel, models: (this.foreign ? ['us-east-1'] : euRegions).map(r => ({ modelArn: `arn:aws:bedrock:${r}::foundation-model/amazon.nova-lite-v1:0` })) };
      case 'cloudformation list-stack-resources': return { StackResourceSummaries: [{ LogicalResourceId: 'InstanceRole', PhysicalResourceId: 'test-role' }] };
      case 'cloudformation validate-template': return {};
      case 'ec2 describe-instances': return { Reservations: [{ Instances: [{ InstanceId: outputs.MonitorInstanceId, VpcId: c.vpc_id, SubnetId: c.subnet_id, MetadataOptions: { HttpTokens: 'required', HttpEndpoint: 'enabled', HttpPutResponseHopLimit: 1 } }] }] };
      case 'ec2 modify-instance-metadata-options': this.writes.push({ operation: 'metadata', instance: args[args.indexOf('--instance-id') + 1] }); return {};
      case 'ssm get-parameter': return { Parameter: { Type: 'SecureString', Value: JSON.stringify(this.monitor) } };
      default: throw Error('Unexpected test operation');
    }
  }
  async input(service: string, operation: string, payload: Obj): Promise<Obj> { this.writes.push({ service, operation, payload }); return {}; }
}
test('Bedrock preparation requires explicit writes and EU consent', async () => {
  assert.throws(() => authorizeCommand('bedrock-prepare', false));
  assert.equal(parse(['bedrock-prepare', '--apply', '--allow-eu-inference']).values['allow-eu-inference'], true);
  const aws = new FakeAWS(); await assert.rejects(prepareBedrock(aws, c, false)); assert.equal(aws.calls.length, 0);
});
test('Bedrock refuses an unapproved region or changed profile destinations', async () => {
  const aws = new FakeAWS(); await assert.rejects(prepareBedrock(aws, { ...c, region: 'us-east-1' }, true));
  aws.foreign = true; await assert.rejects(prepareBedrock(aws, c, true)); assert.equal(aws.writes.length, 0);
});
test('first preparation creates policy but never enables inference or changes hosts', async t => {
  t.mock.method(console, 'log', () => {}); const aws = new FakeAWS(); await prepareBedrock(aws, c, true);
  assert.equal(aws.writes.length, 1); assert.equal(aws.writes[0].operation, 'create-stack');
  assert.ok(!aws.calls.some(a => a[0] === 'bedrock-runtime'));
});
test('existing preparation checks targets and preserves secrets', async t => {
  const log = t.mock.method(console, 'log', () => {}); const aws = new FakeAWS(); aws.exists = true;
  aws.wrongTarget = true; await assert.rejects(prepareBedrock(aws, c, true)); assert.equal(aws.writes.length, 0);
  aws.wrongTarget = false; await prepareBedrock(aws, c, true);
  assert.equal(aws.writes[0].instance, outputs.MonitorInstanceId);
  const config = object(JSON.parse(str(object(aws.writes[1].payload).Value)));
  assert.equal(config.POSTGRES_PASSWORD, 'preserve-db-test-value'); assert.equal(config.LAB_BEDROCK_MONTHLY_CAP_USD, '5');
  assert.ok(!JSON.stringify(log.mock.calls).includes('preserve-db-test-value'));
});
test('Bedrock policy limits model, EU regions, profile and source instance', async () => {
  const template = JSON.parse(await readFile(join(ROOT, 'infra/aws/bedrock-access.json'), 'utf8'));
  const statements = array(template.Resources.NovaLiteAccess.Properties.PolicyDocument.Statement);
  assert.equal(statements.length, 2);
  for (const s of statements) {
    assert.equal(s.Action, 'bedrock:InvokeModel'); assert.ok(object(object(s.Condition).ArnEquals)['ec2:SourceInstanceARN']);
    assert.ok(!JSON.stringify(s.Resource).includes('*'));
  }
  assert.ok(object(object(statements[1].Condition).StringEquals)['bedrock:InferenceProfileArn']);
});
test('repeated preparation preserves approved monthly and zero daily ceilings', async t => {
  t.mock.method(console, 'log', () => {}); const aws = new FakeAWS(); aws.exists = true;
  aws.monitor.LAB_BEDROCK_MONTHLY_CAP_USD = '100'; aws.monitor.LAB_BEDROCK_DAILY_CAP_USD = '0';
  await prepareBedrock(aws, c, true);
  const saved = object(JSON.parse(str(object(aws.writes[1].payload).Value)));
  assert.equal(saved.LAB_BEDROCK_MONTHLY_CAP_USD, '100'); assert.equal(saved.LAB_BEDROCK_DAILY_CAP_USD, '0');
});
test('invalid stored budget fails preparation before metadata or config writes', async () => {
  const aws = new FakeAWS(); aws.exists = true; aws.monitor.LAB_BEDROCK_MONTHLY_CAP_USD = '101';
  await assert.rejects(prepareBedrock(aws, c, true)); assert.equal(aws.writes.length, 0);
});
test('monthly budget command requires apply and rejects invalid amounts before AWS access', async () => {
  assert.throws(() => authorizeCommand('bedrock-budget', false));
  assert.equal(parse(['bedrock-budget', '--apply', '--monthly-usd', '100']).values['monthly-usd'], '100');
  for (const amount of [undefined, true, 100, '-1', '100.01', 'NaN', 'Infinity', '1e2', '0.001', '']) {
    const aws = new FakeAWS(); await assert.rejects(setBedrockBudget(aws, c, amount)); assert.equal(aws.calls.length, 0);
  }
});
test('monthly budget command preserves secrets, zero daily cap and disabled inference', async t => {
  const log = t.mock.method(console, 'log', () => {}); const aws = new FakeAWS();
  Object.assign(aws.monitor, { LAB_BEDROCK_ENABLED: 'false', LAB_BEDROCK_DAILY_CAP_USD: '0', LAB_BEDROCK_MONTHLY_CAP_USD: '5' });
  await setBedrockBudget(aws, c, '100');
  assert.equal(aws.writes.length, 1); const change = aws.writes[0]; assert.equal(change.service, 'ssm'); assert.equal(change.operation, 'put-parameter');
  const payload = object(change.payload); assert.equal(payload.Name, `/incidentlab/${c.stack_name}/monitor`); assert.equal(payload.Type, 'SecureString');
  assert.deepEqual(JSON.parse(str(payload.Value)), { ...aws.monitor, LAB_BEDROCK_MONTHLY_CAP_USD: '100' });
  assert.ok(!aws.calls.some(a => ['bedrock', 'bedrock-runtime', 'iam', 'ec2'].includes(a[0])));
  assert.ok(!JSON.stringify(log.mock.calls).includes('preserve-db-test-value'));
});
test('monthly budget command rejects a foreign account or invalid daily cap without writes', async () => {
  const aws = new FakeAWS(); await assert.rejects(setBedrockBudget(aws, { ...c, account_id: '999999999999' }, '100'));
  aws.monitor.LAB_BEDROCK_DAILY_CAP_USD = '0.51'; await assert.rejects(setBedrockBudget(aws, c, '100')); assert.equal(aws.writes.length, 0);
});
test('Monitor dotenv adds safe defaults and rejects excess Bedrock budgets', async () => {
  await temporary('incidentlab-bedrock-jq-', async dir => {
    const file = join(dir, 'input.json'); const base: Obj = { POSTGRES_PASSWORD: 'test', LAB_WORKER_KEY: 'test', LAB_INGEST_KEY: 'test', LAB_CONTROL_KEY: 'test', TARGET_URL: 'http://10.0.1.11:8080', MONITOR_BIND_ADDRESS: '10.0.1.10' };
    for (const overrides of [{}, { LAB_BEDROCK_MONTHLY_CAP_USD: '100' }, { LAB_BEDROCK_MONTHLY_CAP_USD: '101' }, { LAB_BEDROCK_MONTHLY_CAP_USD: '-1' }, { LAB_BEDROCK_MONTHLY_CAP_USD: 'NaN' }, { LAB_BEDROCK_DAILY_CAP_USD: '0.51' }]) {
      const bad = Object.values(overrides).some(v => ['101', '-1', 'NaN', '0.51'].includes(v));
      const value = { ...base, ...overrides };
      await writeFile(file, JSON.stringify({ Parameter: { Type: 'SecureString', Value: JSON.stringify(value) } }));
      const result = await execute('jq', ['-er', '--arg', 'role', 'monitor', '-f', join(ROOT, 'infra/aws/write-env.jq'), file]);
      if (bad) { assert.notEqual(result.code, 0); assert.equal(result.stdout, ''); }
      else { assert.equal(result.code, 0); assert.match(result.stdout, /LAB_BEDROCK_ENABLED=false/); assert.ok(result.stdout.includes(`LAB_BEDROCK_MONTHLY_CAP_USD=${overrides.LAB_BEDROCK_MONTHLY_CAP_USD ?? '5'}`)); }
    }
  });
});
