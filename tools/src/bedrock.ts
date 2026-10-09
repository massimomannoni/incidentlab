import { readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { AwsError, type AwsClient, type Config, getConfig, identity, online, outputs, parameterName } from './aws.js';
import { ROOT, array, object, requireThat, str } from './common.js';

export const bedrockModel = 'eu.amazon.nova-lite-v1:0';
export const euRegions = ['eu-south-1', 'eu-central-1', 'eu-west-1', 'eu-west-3', 'eu-north-1'];
function budget(value: unknown, maximum: number): string {
  requireThat(typeof value === 'string' && /^\d+(\.\d{1,2})?$/.test(value) && Number(value) <= maximum,
    `Bedrock budget must be a USD amount between 0 and ${maximum}, with at most two decimal places.`);
  return value as string;
}
// Changes only the operator ceiling; deployment and private runtime settings remain explicit steps.
export async function setBedrockBudget(aws: AwsClient, c: Config, monthly: unknown): Promise<void> {
  const amount = budget(monthly, 100);
  requireThat(c.region === 'eu-south-1', 'This bounded Bedrock deployment supports the Milan endpoint only.');
  await identity(aws, c); await outputs(aws, c);
  const monitor = await getConfig(aws, c, 'monitor'); requireThat(monitor, 'Configure the laboratory first.');
  budget(monitor.LAB_BEDROCK_DAILY_CAP_USD ?? '0.50', .50);
  monitor.LAB_BEDROCK_MONTHLY_CAP_USD = amount;
  await aws.input('ssm', 'put-parameter', { Name: parameterName(c, 'monitor'), Type: 'SecureString', Value: JSON.stringify(monitor), Overwrite: true });
  console.log(`Monitor deployment ceiling saved: USD ${amount}/UTC month. Daily cap and all other values preserved. Reinstall Monitor, then set the private AI monthly budget. No inference performed.`);
}
export async function prepareBedrock(aws: AwsClient, c: Config, approved: boolean): Promise<void> {
  requireThat(approved, 'Bedrock preparation requires --allow-eu-inference after approving EU cross-region processing and AWS charges.');
  requireThat(c.region === 'eu-south-1', 'This bounded Bedrock deployment supports the Milan endpoint only.');
  await identity(aws, c); const o = await outputs(aws, c); await online(aws, o);
  const profile = await aws.call('bedrock', 'get-inference-profile', '--inference-profile-identifier', bedrockModel);
  requireThat(profile.status === 'ACTIVE' && profile.inferenceProfileId === bedrockModel, 'Bedrock profile is not active.');
  const allowed = euRegions.map(r => `arn:aws:bedrock:${r}::foundation-model/amazon.nova-lite-v1:0`);
  const modelArns = array(profile.models).map(m => str(m.modelArn));
  requireThat(modelArns.length > 0 && modelArns.every(a => allowed.includes(a)), 'AWS profile destinations changed. Review IAM and data residency before proceeding.');
  const resources = array((await aws.call('cloudformation', 'list-stack-resources', '--stack-name', c.stack_name)).StackResourceSummaries);
  const role = str(resources.find(r => r.LogicalResourceId === 'InstanceRole')?.PhysicalResourceId);
  requireThat(/^[A-Za-z0-9+=,.@_-]{1,64}$/.test(role), 'Invalid instance role.');
  const name = `${c.stack_name}-bedrock`; let existing;
  try { existing = array((await aws.call('cloudformation', 'describe-stacks', '--stack-name', name)).Stacks)[0]; }
  catch (e) { if (!(e instanceof AwsError && e.code === 'ValidationError')) throw e; }
  if (existing) {
    requireThat(['CREATE_COMPLETE', 'UPDATE_COMPLETE'].includes(str(existing.StackStatus)), 'Bedrock stack is not ready; inspect it before retrying.');
    const params = Object.fromEntries(array(existing.Parameters).map(p => [str(p.ParameterKey), str(p.ParameterValue)]));
    requireThat(params.InstanceRoleName === role && params.MonitorInstanceId === o.MonitorInstanceId, 'Existing Bedrock stack has different targets; refusing to replace it.');
  } else {
    await aws.call('cloudformation', 'validate-template', '--template-body', `file://${join(ROOT, 'infra/aws/bedrock-access.json')}`);
    await aws.input('cloudformation', 'create-stack', { StackName: name, EnableTerminationProtection: true, Capabilities: ['CAPABILITY_NAMED_IAM'],
      TemplateBody: await readFile(join(ROOT, 'infra/aws/bedrock-access.json'), 'utf8'), Tags: [{ Key: 'Project', Value: c.stack_name }],
      Parameters: [{ ParameterKey: 'InstanceRoleName', ParameterValue: role }, { ParameterKey: 'MonitorInstanceId', ParameterValue: o.MonitorInstanceId }] });
    console.log('Bedrock IAM stack creation started. Run the same command again after CREATE_COMPLETE; no inference has been enabled.');
    return;
  }
  const reservations = array((await aws.call('ec2', 'describe-instances', '--instance-ids', o.MonitorInstanceId)).Reservations);
  const instance = array(reservations[0].Instances)[0];
  requireThat(instance.InstanceId === o.MonitorInstanceId && instance.VpcId === c.vpc_id && instance.SubnetId === c.subnet_id, 'Monitor instance identity mismatch.');
  const metadata = object(instance.MetadataOptions);
  requireThat(metadata.HttpTokens === 'required' && metadata.HttpEndpoint === 'enabled', 'IMDSv2 must already be required.');
  const monitor = await getConfig(aws, c, 'monitor'); requireThat(monitor, 'Configure the laboratory first.');
  const monthly = budget(monitor.LAB_BEDROCK_MONTHLY_CAP_USD ?? '5', 100);
  const daily = budget(monitor.LAB_BEDROCK_DAILY_CAP_USD ?? '0.50', .50);
  if (metadata.HttpPutResponseHopLimit !== 2) await aws.call('ec2', 'modify-instance-metadata-options', '--instance-id', o.MonitorInstanceId, '--http-tokens', 'required', '--http-put-response-hop-limit', '2');
  // Only the Monitor container needs IAM credentials. Target keeps its original hop limit.
  monitor.LAB_BEDROCK_ENABLED = 'true'; monitor.LAB_BEDROCK_REGION = 'eu-south-1';
  monitor.LAB_BEDROCK_MONTHLY_CAP_USD = monthly; monitor.LAB_BEDROCK_DAILY_CAP_USD = daily;
  await aws.input('ssm', 'put-parameter', { Name: parameterName(c, 'monitor'), Type: 'SecureString', Value: JSON.stringify(monitor), Overwrite: true });
  console.log('Bedrock prerequisites prepared. Reinstall the reviewed application, then enable Bedrock privately in Settings. Existing AI settings are preserved; this command performs no inference.');
}
