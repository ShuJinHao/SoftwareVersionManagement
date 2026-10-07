import { spawnSync } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, existsSync, chmodSync, rmSync, openSync, closeSync, unlinkSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'node:net';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const baseline = JSON.parse(readFileSync(join(root, 'build/rabbitmq.local.json'), 'utf8'));
const identity = createHash('sha256').update(root).digest('hex').slice(0, 12);
const directory = join(root, '.cache/rabbitmq-local');
const name = `svm-rabbit-${identity}`, volume = `${name}-data`, label = 'io.svm.local-project';
const command = process.argv[2];
let lock;
function fail(message) { throw new Error(message); }
function docker(args, optional = false) {
  const result = spawnSync('docker', ['--context', baseline.dockerContext, ...args], { encoding: 'utf8', timeout: 180000 });
  if (result.status !== 0 && !optional) fail(`Local RabbitMQ ${args[0]} failed; credentials and raw output were suppressed.`);
  return result;
}
function inspect(kind, resource) {
  const result = docker([kind, 'inspect', resource], true);
  if (result.status !== 0) {
    if (/No such (image|object|container|volume)/i.test(result.stderr ?? '')) return null;
    fail('Cannot inspect the project RabbitMQ resource.');
  }
  return JSON.parse(result.stdout)[0];
}
function owned(resource) {
  if ((resource?.Config?.Labels ?? resource?.Labels)?.[label] !== identity) fail('Refusing to operate on an unowned RabbitMQ resource.');
}
function checked() {
  const container = inspect('container', name);
  if (!container) return null;
  owned(container);
  if (container.Config.Image !== baseline.image || container.Mounts.find(m => m.Destination === '/var/lib/rabbitmq')?.Name !== volume)
    fail('RabbitMQ image or volume differs from the pinned project configuration.');
  for (const port of ['5672/tcp', '15672/tcp'])
    if (container.HostConfig.PortBindings[port]?.[0]?.HostIp !== '127.0.0.1') fail('RabbitMQ must bind only to loopback.');
  return container;
}
function privateFile(path, value) { writeFileSync(path, value, { mode: 0o600 }); chmodSync(path, 0o600); }
async function freePort() {
  const server = createServer();
  await new Promise((accept, reject) => { server.once('error', reject); server.listen({ host: '127.0.0.1', port: 0 }, accept); });
  const port = server.address().port;
  await new Promise((accept, reject) => server.close(error => error ? reject(error) : accept()));
  return port;
}
async function main() {
  if (!['up', 'status', 'stop', 'start', 'down'].includes(command) || process.argv.length !== 3)
    fail('Usage: eng/rabbitmq up|status|stop|start|down');
  if (!docker(['context', 'inspect', baseline.dockerContext, '--format', '{{.Endpoints.docker.Host}}']).stdout.trim().startsWith('unix://'))
    fail('Only the existing local Unix-socket Docker context is allowed.');
  if (docker(['version', '--format', '{{.Server.Os}}/{{.Server.Arch}}']).stdout.trim() !== baseline.platform) fail('Docker platform differs from the RabbitMQ baseline.');
  mkdirSync(directory, { recursive: true, mode: 0o700 }); chmodSync(directory, 0o700);
  lock = openSync(join(directory, 'lifecycle.lock'), 'wx', 0o600);
  let current = checked();
  const existingVolume = inspect('volume', volume); if (existingVolume) owned(existingVolume);
  const statePath = join(directory, 'test-admin.json');
  let state = existsSync(statePath) ? JSON.parse(readFileSync(statePath, 'utf8')) : null;
  if (state && (state.projectIdentity !== identity || state.host !== '127.0.0.1' || state.username !== 'svm_bootstrap' ||
      typeof state.password !== 'string' || !/^[A-Za-z0-9_-]{43}$/.test(state.password))) fail('Invalid RabbitMQ private state; no automatic credential reset.');
  if (!state && (current || existingVolume)) fail('RabbitMQ exists without saved secrets; no automatic reset.');
  if (command === 'down') {
    if (current) docker(['rm', '--force', name]);
    if (existingVolume) docker(['volume', 'rm', volume]);
    console.log('Removed this project test RabbitMQ container, volume and private configuration.');
    return;
  }
  if (command === 'status') {
    console.log(JSON.stringify({ container: name, state: current?.State.Status ?? 'absent', owned: !!current })); return;
  }
  if (command === 'stop') { if (current?.State.Running) docker(['stop', '--time', '10', name]); return; }
  if (command === 'start' && !current) fail('Run eng/rabbitmq up first.');
  if (!current) {
    state ??= { projectIdentity: identity, host: '127.0.0.1', username: 'svm_bootstrap', password: randomBytes(32).toString('base64url'), container: name };
    privateFile(statePath, JSON.stringify(state));
    const salt = randomBytes(4);
    const hash = Buffer.concat([salt, createHash('sha256').update(Buffer.concat([salt, Buffer.from(state.password)])).digest()]).toString('base64');
    privateFile(join(directory, 'definitions.json'), JSON.stringify({ users: [{ name: state.username, password_hash: hash, hashing_algorithm: 'rabbit_password_hashing_sha256', tags: 'administrator' }], vhosts: [{ name: '/', default_queue_type: 'quorum' }],
      permissions: [{ user: state.username, vhost: '/', configure: '.*', write: '.*', read: '.*' }] }));
    privateFile(join(directory, 'rabbitmq.conf'), 'load_definitions = /run/secrets/definitions.json\n');
    if (!inspect('image', baseline.image)) docker(['pull', '--platform', baseline.platform, baseline.image]);
    const image = inspect('image', baseline.image);
    if (image.Os !== 'linux' || image.Architecture !== 'arm64' || !image.RepoDigests?.some(d => d.endsWith(baseline.image.split('@')[1]))) fail('RabbitMQ image identity mismatch.');
    if (!existingVolume) docker(['volume', 'create', '--label', `${label}=${identity}`, volume]);
    const amqpPort = await freePort(); let managementPort = await freePort();
    while (managementPort === amqpPort) managementPort = await freePort();
    docker(['run', '--detach', '--name', name, '--hostname', name, '--label', `${label}=${identity}`, '--platform', baseline.platform,
      '--publish', `127.0.0.1:${amqpPort}:5672`, '--publish', `127.0.0.1:${managementPort}:15672`, '--mount', `type=volume,src=${volume},dst=/var/lib/rabbitmq`,
      '--mount', `type=bind,src=${directory},dst=/run/secrets,readonly`, '--env', 'RABBITMQ_CONFIG_FILE=/run/secrets/rabbitmq', baseline.image]);
  } else if (!current.State.Running) docker(['start', name]);
  current = checked();
  state.amqpPort = Number(current.NetworkSettings.Ports['5672/tcp'][0].HostPort);
  state.managementPort = Number(current.NetworkSettings.Ports['15672/tcp'][0].HostPort);
  let ready = false;
  for (let i = 0; i < 80; i++) {
    try {
      const response = await fetch(`http://127.0.0.1:${state.managementPort}/api/overview`, {
        headers: { Authorization: 'Basic ' + Buffer.from(`${state.username}:${state.password}`).toString('base64') }, signal: AbortSignal.timeout(1000) });
      if (response.ok && (await response.json()).rabbitmq_version === baseline.version) { ready = true; break; }
    } catch { }
    await new Promise(r => setTimeout(r, 500));
  }
  if (!ready) fail('Pinned RabbitMQ did not become ready in the expected time.');
  privateFile(statePath, JSON.stringify(state));
  console.log(`RabbitMQ ${baseline.version} ready on project loopback ports; private test configuration is ignored.`);
}
try { await main(); }
catch (error) { console.error(error?.message?.startsWith('Local RabbitMQ') ? error.message : 'Project RabbitMQ lifecycle/configuration failed; raw diagnostics suppressed.'); process.exitCode = 1; }
finally {
  if (lock !== undefined) { closeSync(lock); unlinkSync(join(directory, 'lifecycle.lock')); }
  if (command === 'down' && process.exitCode !== 1) rmSync(directory, { recursive: true, force: true });
}
