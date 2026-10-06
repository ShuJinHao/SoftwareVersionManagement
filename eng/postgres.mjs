import { spawnSync, spawn } from 'node:child_process';
import { createHash, randomBytes } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, existsSync, chmodSync, openSync, closeSync, unlinkSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const baseline = JSON.parse(readFileSync(join(root, 'build/postgres.local.json'), 'utf8'));
const identity = createHash('sha256').update(root).digest('hex').slice(0, 12);
const directory = join(root, '.cache/postgres-local');
const statePath = join(directory, 'state.json');
const container = `svm-pg-${identity}`;
const volume = `${container}-data`;
const label = 'io.svm.local-project';
const command = process.argv[2];
const arguments_ = process.argv.slice(3);
const allowed = new Set(['up', 'status', 'stop', 'migrate', 'test']);
let state;
let lock;

function fail(message) { throw new Error(message); }
function privateFile(path, value) {
  writeFileSync(path, value, { mode: 0o600 });
  chmodSync(path, 0o600);
}
function docker(args, input, optional = false) {
  const result = spawnSync('docker', ['--context', baseline.dockerContext, ...args], {
    encoding: 'utf8', input, maxBuffer: 32 * 1024 * 1024, timeout: 180000
  });
  if (result.status !== 0 && !optional) {
    // psql error text may contain the submitted CREATE ROLE statement. Never echo it.
    const authentication = /password authentication failed/i.test(result.stderr ?? '');
    fail(authentication ? 'DATABASE_AUTHENTICATION_FAILED' : `Local Docker ${args[0]} failed; no credentials were printed.`);
  }
  return result;
}
function inspect(kind, name) {
  const result = docker([kind, 'inspect', name], undefined, true);
  if (result.status !== 0) {
    if (/No such (image|object|container|volume)/i.test(result.stderr ?? '')) return null;
    fail(`Cannot inspect local ${kind}; Docker must be running and accessible.`);
  }
  return JSON.parse(result.stdout)[0];
}
function assertOwned(resource, name) {
  const labels = resource?.Config?.Labels ?? resource?.Labels;
  if (!resource || labels?.[label] !== identity) fail(`Refusing to operate on unowned resource ${name}.`);
}
function checkedContainer() {
  const value = inspect('container', container);
  if (!value) return null;
  assertOwned(value, container);
  if (value.Config.Image !== baseline.image) fail('The existing SVM container uses another image; explicit reconciliation is required.');
  const data = value.Mounts.find(m => m.Destination === '/var/lib/postgresql/data');
  if (data?.Name !== volume) fail('The SVM database volume does not match its saved configuration.');
  const bindings = value.HostConfig.PortBindings['5432/tcp'];
  if (bindings?.length !== 1 || bindings[0].HostIp !== '127.0.0.1') fail('The SVM database must bind only to loopback.');
  return value;
}
function sql(text, database = 'postgres') {
  return docker(['exec', '-i', container, 'psql', '-X', '-q', '-t', '-A', '-v', 'ON_ERROR_STOP=1',
    '-U', 'svm_bootstrap', '-d', database], text).stdout.trim();
}
function literal(value) { return `'${String(value).replaceAll("'", "''")}'`; }
function connection(database, user, password) {
  return `Host=127.0.0.1;Port=${state.port};Database=${database};Username=${user};Password=${password};` +
    'SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=20;Include Error Detail=false;Log Parameters=false;';
}
function writeConnections() {
  const data = { database: 'svm_dev', roles: { writer: 'svm_app', reader: 'svm_read' } };
  privateFile(join(directory, 'runtime.json'), JSON.stringify({
    writerConnectionString: connection(data.database, data.roles.writer, state.secrets.writer),
    readerConnectionString: connection(data.database, data.roles.reader, state.secrets.reader)
  }, null, 2));
  privateFile(join(directory, 'migration.json'), JSON.stringify({
    connectionString: connection(data.database, 'svm_migrate', state.secrets.migration),
    writerRole: data.roles.writer, readerRole: data.roles.reader
  }, null, 2));
  privateFile(join(directory, 'test-admin.json'), JSON.stringify({
    connectionString: connection('postgres', 'svm_bootstrap', state.secrets.admin),
    projectIdentity: identity
  }, null, 2));
}
function provisionRolesAndDatabase() {
  for (const [role, secret] of [['svm_migrate', state.secrets.migration], ['svm_app', state.secrets.writer], ['svm_read', state.secrets.reader]]) {
    sql(`DO $svm$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname=${literal(role)}) THEN
      CREATE ROLE "${role}" LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD ${literal(secret)};
      END IF; END $svm$;`);
  }
  sql('ALTER ROLE svm_read SET default_transaction_read_only = on;');
  if (sql("SELECT count(*) FROM pg_database WHERE datname='svm_dev';") === '0')
    sql('CREATE DATABASE svm_dev OWNER svm_migrate;');
  sql('REVOKE ALL ON DATABASE svm_dev FROM PUBLIC; GRANT CONNECT ON DATABASE svm_dev TO svm_migrate, svm_app, svm_read;');
  sql('REVOKE CREATE ON SCHEMA public FROM PUBLIC; REVOKE ALL ON SCHEMA public FROM svm_app, svm_read;', 'svm_dev');
}
async function up() {
  let current = checkedContainer();
  const currentVolume = inspect('volume', volume);
  if (currentVolume) assertOwned(currentVolume, volume);
  if (!state && (current || currentVolume)) fail('SVM database exists without its saved secrets. Refusing to rotate or reset it.');
  if (!state) {
    state = { format: 1, identity, secrets: Object.fromEntries(['admin', 'migration', 'writer', 'reader'].map(k => [k, randomBytes(32).toString('base64url')])) };
    privateFile(statePath, JSON.stringify(state, null, 2));
  }
  const secretFile = join(directory, 'bootstrap-password');
  privateFile(secretFile, state.secrets.admin);
  let image = inspect('image', baseline.image);
  if (!image) {
    console.log('Pulling the pinned PostgreSQL image.');
    docker(['pull', '--platform', baseline.platform, baseline.image]);
    image = inspect('image', baseline.image);
  }
  if (!image || image.Os !== 'linux' || image.Architecture !== 'arm64' || !image.RepoDigests?.some(d => d.endsWith(baseline.image.split('@')[1])))
    fail('PostgreSQL image identity or platform does not match the baseline.');
  if (!currentVolume) docker(['volume', 'create', '--label', `${label}=${identity}`, volume]);
  if (!current) {
    docker(['run', '--detach', '--name', container, '--label', `${label}=${identity}`, '--platform', baseline.platform,
      '--publish', '127.0.0.1::5432', '--mount', `type=volume,src=${volume},dst=/var/lib/postgresql/data`,
      '--mount', `type=bind,src=${secretFile},dst=/run/secrets/svm-postgres-password,readonly`,
      '--env', 'POSTGRES_PASSWORD_FILE=/run/secrets/svm-postgres-password', '--env', 'POSTGRES_USER=svm_bootstrap',
      '--env', 'POSTGRES_DB=postgres', '--env', 'POSTGRES_INITDB_ARGS=--auth-host=scram-sha-256',
      baseline.image]);
  } else if (!current.State.Running) docker(['start', container]);
  let ready = false;
  for (let attempt = 0; attempt < 60; attempt++) {
    if (docker(['exec', container, 'pg_isready', '-U', 'svm_bootstrap', '-d', 'postgres'], undefined, true).status === 0) { ready = true; break; }
    await new Promise(r => setTimeout(r, 500));
  }
  if (!ready) fail('PostgreSQL did not become ready within 30 seconds.');
  current = checkedContainer();
  state.port = Number(current.NetworkSettings.Ports['5432/tcp'][0].HostPort);
  if (!Number.isInteger(state.port) || state.port < 1) fail('No valid loopback port was allocated.');
  const version = sql('SHOW server_version;');
  if (!version.startsWith(`${baseline.version} `) && version !== baseline.version) fail('Running PostgreSQL version differs from the pinned baseline.');
  provisionRolesAndDatabase();
  privateFile(statePath, JSON.stringify(state, null, 2));
  writeConnections();
  console.log(`PostgreSQL ${baseline.version} ready at 127.0.0.1:${state.port}; database svm_dev. Secrets remain in ignored local files.`);
}
async function child(args) {
  const exitCode = await new Promise((accept, reject) => {
    const process_ = spawn(join(root, 'eng/dotnet'), args, {
      cwd: root, stdio: 'inherit', env: { ...process.env,
        SVM_PERSISTENCE_CONFIG_FILE: join(directory, 'runtime.json'),
        SVM_TEST_DATABASE_CONFIG_FILE: join(directory, 'test-admin.json'),
        SVM_PERSONNEL_CONFIG_FILE: join(root, '.cache/personnel-local/personnel.json') }
    });
    process_.on('error', reject);
    process_.on('exit', (code, signal) => accept(code ?? (signal ? 130 : 1)));
  });
  process.exitCode = exitCode;
}

try {
  if (!allowed.has(command)) fail('Usage: eng/postgres up|status|stop|migrate status|script|apply|test architecture|security|framework');
  if (['up', 'status', 'stop'].includes(command) && arguments_.length) fail('Lifecycle commands do not accept extra arguments.');
  const endpoint = docker(['context', 'inspect', baseline.dockerContext, '--format', '{{.Endpoints.docker.Host}}']).stdout.trim();
  if (!endpoint.startsWith('unix://')) fail('This helper only manages the existing local Unix-socket Docker context.');
  const engine = docker(['version', '--format', '{{.Server.Os}}/{{.Server.Arch}}']).stdout.trim();
  if (engine !== baseline.platform) fail('Local Docker platform does not match the pinned PostgreSQL image.');
  mkdirSync(directory, { recursive: true, mode: 0o700 }); chmodSync(directory, 0o700);
  if (existsSync(statePath)) {
    state = JSON.parse(readFileSync(statePath, 'utf8'));
    if (state.identity !== identity || state.format !== 1) fail('Saved database state belongs to another workspace or format.');
    if (!['admin', 'migration', 'writer', 'reader'].every(k => typeof state.secrets?.[k] === 'string' && /^[A-Za-z0-9_-]{43}$/.test(state.secrets[k])))
      fail('Saved local credentials are invalid; refusing automatic reset or rotation.');
  }
  // Only lifecycle commands hold the local file lock; PostgreSQL serializes migrations separately.
  if (command === 'up' || command === 'stop') {
    try { lock = openSync(join(directory, 'lifecycle.lock'), 'wx', 0o600); }
    catch { fail('Another local database lifecycle operation is active.'); }
  }
  if (command === 'up') await up();
  if (command === 'status') {
    const current = checkedContainer();
    console.log(JSON.stringify({ container, state: current?.State.Status ?? 'absent', database: 'svm_dev',
      address: current?.State.Running ? `127.0.0.1:${current.NetworkSettings.Ports['5432/tcp'][0].HostPort}` : null,
      volumeRetained: !!inspect('volume', volume) }));
  }
  if (command === 'stop') {
    const current = checkedContainer();
    if (current?.State.Running) docker(['stop', '--time', '20', container]);
    console.log('SVM PostgreSQL stopped; the container, volume and credentials are retained.');
  }
  if (command === 'migrate' || command === 'test') {
    if (!state || !checkedContainer()?.State.Running) fail('Run eng/postgres up first.');
    if (command === 'migrate') {
      if (arguments_.length !== 1 || !['status', 'script', 'apply'].includes(arguments_[0])) fail('Migration requires exactly status, script or apply.');
      await child(['src/hosts/Svm.Migration/bin/Debug/net8.0/Svm.Migration.dll', arguments_[0], '--config', join(directory, 'migration.json')]);
    } else {
      const projects = { architecture: ['Svm.ArchitectureTests', 'Category=Architecture'], security: ['Svm.SecurityTests', 'Category=Security'],
        framework: ['Svm.FrameworkTests', 'Category=Business&(FullyQualifiedName~Persistence|FullyQualifiedName~Composition|FullyQualifiedName~HostRuntime|FullyQualifiedName~Personnel|FullyQualifiedName~Idempotency)'] };
      const choice = projects[arguments_[0]];
      if (!choice || arguments_.length !== 1) fail('Choose the affected architecture, security or framework test group.');
      await child(['test', `src/tests/${choice[0]}/${choice[0]}.csproj`, '--no-build', '--no-restore', '--filter', choice[1],
        '--logger', 'trx', '--results-directory', `artifacts/test-results/personnel/${arguments_[0]}`, '--verbosity', 'minimal']);
    }
  }
} catch (error) {
  // Error messages emitted by this helper are deliberately fixed and contain no SQL or secrets.
  console.error(error instanceof SyntaxError ? 'Invalid local database state/configuration.' : error.message);
  process.exitCode = 1;
} finally {
  if (lock !== undefined) { closeSync(lock); unlinkSync(join(directory, 'lifecycle.lock')); }
}
