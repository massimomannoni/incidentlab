import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile, mkdir, symlink, stat, writeFile } from 'node:fs/promises';
import { pbkdf2Sync } from 'node:crypto';
import { join } from 'node:path';
import { ROOT, temporary, execute, type Obj } from '../common.js';
import { authorizeCommand, type AwsClient, type Config } from '../aws.js';
import { validatePublic, publicInstallScript, prepareMaster, masterAdmissionKey } from '../public.js';

test('public mutations require explicit apply; status remains read-only', () => {
  for (const command of ['public-prepare', 'public-master-prepare', 'public-install', 'public-enable', 'public-disable']) {
    assert.throws(() => authorizeCommand(command, false)); authorizeCommand(command, true);
  }
  authorizeCommand('public-status', false);
});
test('master admission credential is domain-separated and matches the C# monitor', () => {
  assert.equal(masterAdmissionKey('a'.repeat(64)), 'dca78f03502a729fd146ba57e6880a141e5f27cd319c2f141b2a2747afde3695');
  assert.throws(()=>masterAdmissionKey('invalid'), error=>!String(error).includes('invalid'));
});
test('public credentials validate without leaking rejected values', () => {
  const value = { PUBLIC_USERNAME: 'visitor', PUBLIC_ORIGIN_KEY: 'a'.repeat(64), PUBLIC_PASSWORD_HASH: 'b'.repeat(64), PUBLIC_PASSWORD_SALT: 'c'.repeat(32), PUBLIC_ACCESS: 'viewer' };
  validatePublic(value);
  assert.throws(() => validatePublic({ ...value, PUBLIC_ACCESS: 'admin' }));
  assert.throws(() => validatePublic({ ...value, PUBLIC_ACCESS: 'owner' }));
  assert.throws(() => validatePublic({ ...value, PUBLIC_ACCESS: 'master' }));
  assert.throws(() => validatePublic({ ...value, PUBLIC_MASTER_USERNAME: 'master' }));
  const master = { PUBLIC_MASTER_USERNAME: 'master', PUBLIC_MASTER_PASSWORD_HASH: 'd'.repeat(64), PUBLIC_MASTER_PASSWORD_SALT: 'e'.repeat(32) };
  validatePublic({ ...value, ...master });
  validatePublic({ ...value, ...master, PUBLIC_MASTER_ADMISSION_KEY:'f'.repeat(64) });
  assert.throws(()=>validatePublic({ ...value, PUBLIC_MASTER_ADMISSION_KEY:'f'.repeat(64) }));
  assert.throws(()=>validatePublic({ ...value, ...master, PUBLIC_MASTER_ADMISSION_KEY:'invalid' }));
  assert.throws(() => validatePublic({ ...value, ...master, PUBLIC_MASTER_USERNAME: 'VISITOR' }));
  assert.throws(() => validatePublic({ ...value, PUBLIC_PASSWORD_HASH: 'test-invalid-private-value' }), error => !String(error).includes('test-invalid-private-value'));
});
test('host installer and browser never turn a shared account into an administrator', async () => {
  const installer = await readFile(join(ROOT, 'infra/aws/install-public.sh'), 'utf8');
  assert.match(installer, /PUBLIC_ACCESS != "viewer"/);
  const frontend = await readFile(join(ROOT, 'frontend/src/main.tsx'), 'utf8');
  assert.match(frontend, /let privateConsole = false/);
  assert.match(frontend, /const canOperate = privateConsole/);
  assert.match(frontend, /sharedSession\?\.access === "master"/);
  assert.match(frontend, /const canConfigureAI = privateConsole;/);
  assert.match(frontend, /response.status === 404\) privateConsole = true/);
  assert.ok(!frontend.includes('Shared operator'));
});
const config: Config = { account_id: '123456789012', region: 'eu-south-1', stack_name: 'incidentlab-test', vpc_id: 'vpc-12345678', subnet_id: 'subnet-12345678' };
class PublicAWS implements AwsClient {
  value: Obj = { PUBLIC_USERNAME: 'visitor', PUBLIC_ORIGIN_KEY: 'a'.repeat(64), PUBLIC_PASSWORD_HASH: 'b'.repeat(64),
    PUBLIC_PASSWORD_SALT: 'c'.repeat(32), PUBLIC_ACCESS: 'viewer', PUBLIC_READY_DIGEST: 'd'.repeat(64) };
  writes = 0; fail = false;
  async call(...args: string[]): Promise<Obj> { assert.equal(args[0], 'ssm'); assert.equal(args[1], 'get-parameter'); return { Parameter: { Type: 'SecureString', Value: JSON.stringify(this.value) } }; }
  async input(service: string, operation: string, payload: Obj): Promise<Obj> {
    assert.equal(service, 'ssm'); assert.equal(operation, 'put-parameter');
    assert.equal(payload.Name, '/incidentlab/incidentlab-test/public'); assert.equal(payload.Type, 'SecureString');
    this.writes++; if (this.fail) throw new Error('Test interrupted write');
    this.value = JSON.parse(String(payload.Value)); return {};
  }
}
test('master preparation preserves visitor, stores hashes only, and never rotates on retry', async () => {
  await temporary('incidentlab-master-test-', async directory => {
    const aws = new PublicAWS(); const old = structuredClone(aws.value);
    await prepareMaster(aws, config, directory);
    const path = join(directory, 'master-login.json'); const file = await readFile(path, 'utf8'); const saved = JSON.parse(file);
    assert.equal((await stat(path)).mode & 0o777, 0o600);
    assert.equal(saved.username, 'master'); assert.equal(saved.password.length, 48);
    assert.equal(aws.value.PUBLIC_MASTER_PASSWORD_HASH, pbkdf2Sync(saved.password, Buffer.from(saved.salt, 'hex'), 600_000, 32, 'sha256').toString('hex'));
    for (const k of ['PUBLIC_USERNAME', 'PUBLIC_PASSWORD_HASH', 'PUBLIC_PASSWORD_SALT', 'PUBLIC_ORIGIN_KEY', 'PUBLIC_ACCESS']) assert.equal(aws.value[k], old[k]);
    assert.equal(aws.value.PUBLIC_READY_DIGEST, undefined); assert.ok(!JSON.stringify(aws.value).includes(saved.password));
    await prepareMaster(aws, config, directory); assert.equal(aws.writes, 1); assert.equal(await readFile(path, 'utf8'), file);
  });
});
test('master resumes interrupted preparation with the same local secret and refuses a different destination', async () => {
  await temporary('incidentlab-master-retry-', async directory => {
    const aws = new PublicAWS(); aws.fail = true;
    await assert.rejects(prepareMaster(aws, config, directory));
    const before = await readFile(join(directory, 'master-login.json'), 'utf8'); aws.fail = false;
    await assert.rejects(prepareMaster(aws, { ...config, account_id: '999999999999' }, directory)); assert.equal(aws.writes, 1);
    await prepareMaster(aws, config, directory); assert.equal(await readFile(join(directory, 'master-login.json'), 'utf8'), before);
  });
});
test('public env filter accepts complete master credentials only and rejects injected keys', async () => {
  const script = await readFile(join(ROOT, 'infra/aws/install-public.sh'), 'utf8');
  const filter = /jq -er '([\s\S]*?)' "\$lab_secret_file"/.exec(script)![1];
  await temporary('incidentlab-public-env-', async directory => {
    const base = { ...new PublicAWS().value, PUBLIC_HOST: 'demo.example.test', MONITOR_BIND_ADDRESS: '10.0.0.1' };
    for (const [extra, success] of [[{}, true], [{ PUBLIC_MASTER_USERNAME: 'master' }, false],
      [{ PUBLIC_MASTER_USERNAME: 'master', PUBLIC_MASTER_PASSWORD_SALT: 'c'.repeat(32), PUBLIC_MASTER_PASSWORD_HASH: 'b'.repeat(64) }, true],
      [{ PUBLIC_MASTER_USERNAME: 'master', PUBLIC_MASTER_PASSWORD_SALT: 'c'.repeat(32), PUBLIC_MASTER_PASSWORD_HASH: 'b'.repeat(64), PUBLIC_MASTER_ADMISSION_KEY:'d'.repeat(64) }, true],
      [{ PUBLIC_MASTER_ADMISSION_KEY:'d'.repeat(64) }, false],
      [{ PUBLIC_ACCESS: 'master' }, false], [{ AWS_ACCESS_KEY_ID: 'not-allowed' }, false]] as const) {
      const path = join(directory, 'input.json'); await writeFile(path, JSON.stringify({ Parameter: { Type: 'SecureString', Value: JSON.stringify({ ...base, ...extra }) } }));
      const result = await execute('jq', ['-er', filter, path]); assert.equal(result.code === 0, success);
    }
  });
});
test('edge is HTTPS-only, non-caching, disabled by default, private and CAPTCHA-protected', async () => {
  const template = JSON.parse(await readFile(join(ROOT, 'infra/aws/public-edge.json'), 'utf8'));
  assert.equal(template.Parameters.PublicEnabled.Default, 'false');
  assert.equal(template.Parameters.OriginKey.NoEcho, true);
  const distribution = template.Resources.Distribution.Properties.DistributionConfig;
  assert.equal(distribution.DefaultCacheBehavior.ViewerProtocolPolicy, 'https-only');
  assert.equal(distribution.DefaultCacheBehavior.CachePolicyId, '4135ea2d-6df8-44a3-9df3-4b5a84be39ad');
  assert.ok(distribution.Origins[0].VpcOriginConfig); assert.ok(distribution.WebACLId);
  assert.equal(template.Resources.PrivateOrigin.Properties.VpcOriginEndpointConfig.HTTPPort, 8020);
  const waf = template.Resources.Firewall.Properties;
  const captcha = waf.Rules.find((r: { Name: string }) => r.Name === 'CaptchaForLogin');
  assert.equal(captcha.Statement.ByteMatchStatement.SearchString, '/login');
  assert.equal(captcha.Action.Captcha.CustomRequestHandling.InsertHeaders[0].Name, 'captcha-verified');
  assert.equal(waf.Rules[0].Name, 'RejectForgedCaptchaProof');
  assert.equal(waf.VisibilityConfig.SampledRequestsEnabled, false);
  assert.ok(waf.Rules.every((r: { VisibilityConfig: { SampledRequestsEnabled: boolean } }) => !r.VisibilityConfig.SampledRequestsEnabled));
});
test('ingress allows only the private CloudFront group to the gateway, not monitor or data stores', async () => {
  const template = JSON.parse(await readFile(join(ROOT, 'infra/aws/public-access.json'), 'utf8'));
  const ingress = template.Resources.GatewayIngress.Properties;
  assert.equal(ingress.FromPort, 8020); assert.equal(ingress.ToPort, 8020);
  assert.ok(ingress.SourceSecurityGroupId); assert.equal(ingress.CidrIp, undefined); assert.equal(ingress.CidrIpv6, undefined);
});
test('public installer receives only region and SSM parameter name, never plaintext credentials', () => {
  const script = publicInstallScript({ account_id: '123456789012', region: 'eu-south-1', stack_name: 'incidentlab-test', vpc_id: 'vpc-12345678', subnet_id: 'subnet-12345678' }, 'a'.repeat(64));
  assert.ok(script.includes('/incidentlab/incidentlab-test/public')); assert.ok(!script.includes('PASSWORD'));
});
test('public scripts resolve release symlinks physically before checking the release boundary', async () => {
  for (const script of ['install-public.sh', 'verify-public.sh']) assert.match(await readFile(join(ROOT, 'infra/aws', script), 'utf8'), /&& pwd -P\)/);
  await temporary('incidentlab-public-link-', async directory => {
    const release = join(directory, 'releases', 'sample'); const current = join(directory, 'public-current');
    await mkdir(join(release, 'infra/aws'), { recursive: true }); await symlink(release, current);
    const result = await execute('bash', ['-c', 'cd -- "$1/infra/aws/../.." && pwd -P', 'check', current]);
    assert.equal(result.code, 0); assert.equal(result.stdout.trim(), release.replace(/^\/var\//, '/private/var/'));
  });
});
