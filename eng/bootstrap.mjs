import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream } from 'node:fs';
import { access, mkdir, readFile, rename, rm, writeFile, stat } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { pipeline } from 'node:stream/promises';
import { Readable } from 'node:stream';
import { root, manifest, platform, paths, environment } from './toolchain.mjs';

const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--sdk' || !manifest.compatibilitySdks?.[args[1]]))
  throw new Error('Usage: eng/bootstrap [--sdk 8.0.400]');
const sdk = args[1] ?? manifest.dotnetSdk;
const archives = manifest.platforms[platform];
if (!archives) throw new Error('No reviewed toolchain for ' + platform);
const p = paths(root, platform, sdk);
async function exists(file) { try { await access(file); return true; } catch { return false; } }
async function hash(file, algorithm) {
  const digest = createHash(algorithm);
  for await (const data of createReadStream(file)) digest.update(data);
  return digest.digest('hex');
}
async function archiveFile(artifact) {
  const file = join(root, '.cache/tool-downloads', basename(new URL(artifact.url).pathname));
  await mkdir(join(root, '.cache/tool-downloads'), { recursive: true });
  if (!await exists(file)) {
    let completed = false;
    for (let attempt = 0; attempt < 4; attempt++) {
      try {
        const size = await stat(file + '.partial').then(s => s.size).catch(() => 0);
        const response = await fetch(artifact.url, { headers: size ? { Range: 'bytes=' + size + '-' } : {}, signal: AbortSignal.timeout(180000) });
        if (!response.ok) throw new Error('Official tool archive download failed.');
        if (response.status === 206 && !response.headers.get('content-range')?.startsWith('bytes ' + size + '-'))
          throw new Error('Unexpected archive range response.');
        await pipeline(Readable.fromWeb(response.body), createWriteStream(file + '.partial', { mode: 0o600, flags: response.status === 206 ? 'a' : 'w' }));
        completed = true; break;
      } catch (error) {
        if (attempt === 3 || !['TimeoutError', 'AbortError', 'TypeError'].includes(error.name)) throw error;
        console.error('Tool download interrupted; resuming the same reviewed archive.');
      }
    }
    if (!completed) throw new Error('Tool download did not complete.');
    if (await hash(file + '.partial', artifact.algorithm) !== artifact.hash) throw new Error('Downloaded tool archive checksum mismatch.');
    await rename(file + '.partial', file);
  }
  if (await hash(file, artifact.algorithm) !== artifact.hash) throw new Error('Cached tool archive checksum mismatch; no automatic replacement.');
  return file;
}
function extract(file, target, node = false) {
  if (platform.startsWith('win-')) {
    execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
      'Expand-Archive -LiteralPath $env:SVM_TOOL_ARCHIVE -DestinationPath $env:SVM_TOOL_STAGE -Force'],
      { stdio: 'inherit', env: { ...process.env, SVM_TOOL_ARCHIVE: file, SVM_TOOL_STAGE: target } });
  } else execFileSync('tar', ['-xzf', file, '-C', target, ...(node ? ['--strip-components=1'] : [])], { stdio: 'inherit' });
}
for (const name of ['node', 'dotnet']) {
  const target = name === 'node' ? p.nodeRoot : p.dotnetRoot;
  const artifact = name === 'node' ? archives.node : sdk === manifest.dotnetSdk ? archives.dotnet : manifest.compatibilitySdks[sdk][platform];
  const version = name === 'node' ? manifest.node : sdk;
  const stamp = { platform, version, archiveHash: artifact.hash };
  if (await exists(target)) {
    const existing = JSON.parse(await readFile(join(target, '.svm-tool.json'), 'utf8'));
    if (JSON.stringify(existing) !== JSON.stringify(stamp)) throw new Error('Existing ' + name + ' cache has another identity; no overwrite.');
    continue;
  }
  await mkdir(join(target, '..'), { recursive: true });
  const staging = target + '.staging-' + process.pid;
  await mkdir(staging);
  try {
    extract(await archiveFile(artifact), staging, name === 'node');
    let contents = staging;
    if (name === 'node' && platform.startsWith('win-')) contents = join(staging, 'node-v' + manifest.node + '-win-x64');
    if (name === 'dotnet' && sdk !== manifest.dotnetSdk)
      for (const runtime of manifest.compatibilityRuntimes[platform]) extract(await archiveFile(runtime), contents);
    await writeFile(join(contents, '.svm-tool.json'), JSON.stringify(stamp) + '\n');
    await rename(contents, target);
  } finally { await rm(staging, { recursive: true, force: true }); }
}
const env = environment(p.dotnetRoot);
await mkdir(join(root, '.cache'), { recursive: true });
await writeFile(env.npm_config_userconfig, '');
const version = (command, args) => execFileSync(command, args, { cwd: root, env, encoding: 'utf8' }).trim();
if (version(p.dotnet, ['--version']) !== sdk) throw new Error('SDK version mismatch.');
for (const name of ['Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'])
  if (!version(p.dotnet, ['--list-runtimes']).includes(name + ' ' + manifest.runtime + ' [')) throw new Error(name + ': runtime mismatch.');
if (version(p.node, ['--version']) !== 'v' + manifest.node || version(p.node, [p.npm, '--version']) !== manifest.npm)
  throw new Error('Node/npm version mismatch.');
console.log('Verified ' + platform + ': SDK ' + sdk + ', runtime ' + manifest.runtime + ', Node ' + manifest.node + ', npm ' + manifest.npm);
