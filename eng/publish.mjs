import { createHash } from 'node:crypto';
import { readFile, readdir, mkdir, cp, writeFile, access } from 'node:fs/promises';
import { join, resolve, relative, dirname, extname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { root, paths, environment, dotnetCommand, run } from './toolchain.mjs';

export async function verifyWeb(folder) {
  const index = await readFile(join(folder, 'index.html'), 'utf8');
  const assets = [...index.matchAll(/(?:src|href)="([^"]+)"/g)].map(m => m[1]).filter(p => p.startsWith('/'));
  if (!assets.some(p => p.includes('/assets/'))) throw new Error('The published webpage has no built assets.');
  for (const asset of assets) await access(join(folder, asset.replace(/^\//, '').split('?')[0]));
  await access(join(folder, 'licenses/hash-wasm-4.12.0-MIT.txt'));
  await access(join(folder, 'licenses/hash-wasm-4.12.0-SHA256.txt'));
}
async function main() {
  const args = process.argv.slice(2);
  const allowDirty = args.includes('--allow-dirty');
  if (allowDirty) args.splice(args.indexOf('--allow-dirty'), 1);
  const dirty = Boolean(execFileSync('git',['status','--porcelain'],{cwd:root,encoding:'utf8'}).trim());
  if (dirty && !allowDirty) throw new Error('Formal publish requires a clean workspace; --allow-dirty is only for explicitly marked development verification.');
  if (args.length && (args.length !== 2 || args[0] !== '--output')) throw new Error('Usage: eng/publish [--output artifacts/releases/name] [--allow-dirty]');
  const output = resolve(root, args[1] ?? 'artifacts/releases/current');
  const rel = relative(join(root, 'artifacts/releases'), output);
  if (rel === '..' || rel.startsWith('../') || rel.startsWith('..\\') || !rel) throw new Error('Output must be a new folder under artifacts/releases.');
  try { await access(output); throw new Error('Publish output already exists; select a new folder.'); }
  catch (e) { if (e.code !== 'ENOENT') throw e; }
  const dotnet = dotnetCommand(true);
  const env = { ...dotnet.env, CI: 'true' };
  async function checked(command, args, options = {}) {
    if (await run(command, args, { env, ...options }) !== 0) throw new Error('Release stage failed; incomplete output is not a release.');
  }
  const web = join(root, 'src/ui/svm-web');
  await checked(paths().node, [paths().npm, 'ci'], { cwd: web, env: { ...environment(), CI: 'true' } });
  await checked(paths().node, [paths().npm, 'run', 'typecheck'], { cwd: web });
  await checked(paths().node, [paths().npm, 'run', 'build'], { cwd: web });
  // MSBuild evaluates the HttpApi dist glob only after the completed web build.
  await checked(dotnet.command, ['restore', 'SoftwareVersionManagement.sln', '--locked-mode', '-p:SvmReproducibleBuild=true']);
  await mkdir(output, { recursive: true });
  const hosts = ['Svm.HttpApi', 'Svm.Worker', 'Svm.Migration'];
  for (const host of hosts) await checked(dotnet.command, ['publish', 'src/hosts/' + host + '/' + host + '.csproj',
    '-c', 'Release', '--no-restore', '--self-contained', 'false', '-p:UseAppHost=false', '-p:SvmReproducibleBuild=true', '-o', join(output, host)]);
  await verifyWeb(join(output, 'Svm.HttpApi/wwwroot'));
  await checked(paths().node, [join(root, 'eng/verify-dependencies.mjs')]);
  await cp(join(root, 'artifacts/licenses'), join(output, 'licenses'), { recursive: true });
  const files = [];
  async function walk(folder) {
    for (const entry of await readdir(folder, { withFileTypes: true })) {
      const file = join(folder, entry.name);
      if (entry.isDirectory()) await walk(file);
      else {
        const name = relative(output, file).replaceAll('\\', '/');
        if (/\.(pfx|p12|key|pem)$/i.test(name) || name.includes('appsettings.') && name.includes('.local.')) throw new Error('Private deployment material in release.');
        files.push({ path: name, size: (await readFile(file)).length, sha256: createHash('sha256').update(await readFile(file)).digest('hex') });
      }
    }
  }
  for (const host of hosts) {
    const runtime = JSON.parse(await readFile(join(output, host, host + '.runtimeconfig.json'), 'utf8')).runtimeOptions;
    if (runtime.rollForward !== 'LatestPatch' || (runtime.frameworks ?? [runtime.framework]).some(f => f.version !== '8.0.31'))
      throw new Error('Published runtime policy differs from the reviewed baseline.');
  }
  await walk(output);
  const locks = [];
  async function lockFiles(folder) {
    for (const e of await readdir(folder, { withFileTypes: true })) {
      if (['bin','obj','node_modules','dist'].includes(e.name)) continue;
      const file = join(folder,e.name);
      if (e.isDirectory()) await lockFiles(file);
      else if (['packages.lock.json','package-lock.json','toolchain.lock.json'].includes(e.name))
        locks.push({ path: relative(root,file).replaceAll('\\','/'), sha256: createHash('sha256').update(await readFile(file)).digest('hex') });
    }
  }
  await lockFiles(join(root,'src')); await lockFiles(join(root,'build'));
  await writeFile(join(output, 'release.json'), JSON.stringify({ format: 1,
    commit: execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),
    dirty,
    sdk: dotnet.version, runtimeMinimum: '8.0.31', runtimeRollForward: 'LatestPatch', target: 'portable-net8.0',
    node: process.version, locks: locks.sort((a,b)=>a.path.localeCompare(b.path)), files: files.sort((a,b)=>a.path.localeCompare(b.path)) }, null, 2) + '\n');
  console.log('Complete Release published at ' + relative(root, output));
}
if (process.argv[1] && resolve(process.argv[1]) === resolve(root,'eng/publish.mjs'))
  await main().catch(error => { console.error(error.message); process.exitCode = 1; });
