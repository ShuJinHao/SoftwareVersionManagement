import { spawnSync, spawn } from 'node:child_process';
import { randomBytes, createHash } from 'node:crypto';
import { readFileSync, writeFileSync, mkdirSync, existsSync, chmodSync, unlinkSync } from 'node:fs';
import { dirname, resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const directory = join(root, '.cache/personnel-local');
const config = join(directory, 'personnel.json');
const action = process.argv[2];
function save(path, value) { writeFileSync(path, value, { mode: 0o600 }); chmodSync(path, 0o600); }
function openssl(args) {
  const result = spawnSync('openssl', args, { encoding: 'utf8', timeout: 30000 });
  if (result.status !== 0) throw new Error('Local certificate preparation failed (diagnostics redacted).');
}
try {
  if (process.argv.length !== 3 || !['prepare', 'seed', 'serve'].includes(action)) throw new Error('Usage: eng/personnel prepare|seed|serve');
  if (action === 'prepare') {
    const runtime = JSON.parse(readFileSync(join(root, '.cache/postgres-local/runtime.json'), 'utf8'));
    mkdirSync(directory, { recursive: true, mode: 0o700 }); chmodSync(directory, 0o700);
    if (existsSync(config)) {
      if (!['seed.json', 'protection.pfx', 'https.pfx', 'certificate-password'].every(name => existsSync(join(directory, name))))
        throw new Error('Incomplete private configuration; explicit repair is required.');
      console.log('Personnel configuration exists; certificates and initialization secrets were preserved.'); process.exit(0);
    }
    // No system trust-store changes. Independent key-protection and HTTPS certificates.
    const password = randomBytes(32).toString('base64url');
    const passwordFile = join(directory, 'certificate-password'); save(passwordFile, password);
    for (const name of ['protection', 'https']) {
      const key = join(directory, `${name}.key`), pem = join(directory, `${name}.pem`), pfx = join(directory, `${name}.pfx`);
      if (existsSync(pfx)) throw new Error('Partial certificate preparation exists; inspect the private directory before retrying.');
      openssl(['req', '-x509', '-newkey', 'rsa:3072', '-nodes', '-keyout', key, '-out', pem, '-days', '365',
        '-subj', `/CN=SVM local ${name}`, ...(name === 'https' ? ['-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1'] : [])]);
      chmodSync(key, 0o600); chmodSync(pem, 0o600);
      openssl(['pkcs12', '-export', '-out', pfx, '-inkey', key, '-in', pem, '-passout', `file:${passwordFile}`]);
      chmodSync(pfx, 0o600); unlinkSync(key);
    }
    const personnel = { applicationName: `svm/local-${createHash('sha256').update(root).digest('hex').slice(0,12)}`,
      certificatePath: join(directory, 'protection.pfx'), certificatePassword: password,
      policy: { sessionHours: 8, minimumPasswordLength: 15, maximumPasswordLength: 128, hashIterations: 600000,
        accountFailures: 5, accountWindowSeconds: 900, addressFailures: 30, addressWindowSeconds: 300 } };
    save(join(directory, 'seed.json'), JSON.stringify({ writerConnectionString: runtime.writerConnectionString,
      personnelConfigurationFile: config, employeeNo: 'LOCAL-ADMIN', displayName: '本机开发管理员', temporaryPassword: randomBytes(32).toString('base64url') }, null, 2));
    save(config, JSON.stringify(personnel, null, 2));
    console.log('Prepared ignored local certificates and seed configuration. No account has been created. Edit private seed.json before explicit seed if needed.');
  } else {
    if (!existsSync(config)) throw new Error('Run eng/personnel prepare first.');
    const env = { ...process.env, SVM_PERSONNEL_CONFIG_FILE: config, SVM_PERSISTENCE_CONFIG_FILE: join(root, '.cache/postgres-local/runtime.json') };
    let args;
    if (action === 'seed') {
      const seedPath = join(directory, 'seed.json');
      const seed = JSON.parse(readFileSync(seedPath, 'utf8'));
      const runtime = JSON.parse(readFileSync(env.SVM_PERSISTENCE_CONFIG_FILE, 'utf8'));
      // PostgreSQL can receive a new loopback port after restart. Keep account inputs unchanged.
      save(seedPath, JSON.stringify({ ...seed, writerConnectionString: runtime.writerConnectionString }, null, 2));
      args = ['src/hosts/Svm.Migration/bin/Debug/net8.0/Svm.Migration.dll', 'seed', '--config', seedPath];
    }
    else {
      env.ASPNETCORE_URLS = 'https://127.0.0.1:7443';
      env.Kestrel__Certificates__Default__Path = join(directory, 'https.pfx');
      env.Kestrel__Certificates__Default__Password = readFileSync(join(directory, 'certificate-password'), 'utf8');
      args = ['src/hosts/Svm.HttpApi/bin/Debug/net8.0/Svm.HttpApi.dll'];
    }
    const process_ = spawn(join(root, 'eng/dotnet'), args, { cwd: root, env, stdio: 'inherit' });
    process_.on('error', () => { console.error('Local personnel process could not start.'); process.exitCode = 1; });
    process_.on('exit', code => { process.exitCode = code ?? 1; });
  }
} catch { console.error('Personnel command failed; inspect local configuration paths and permissions. Secrets were not printed.'); process.exitCode = 1; }
