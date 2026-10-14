import { readFileSync, existsSync } from 'node:fs';
import { dirname, resolve, join, delimiter, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync, spawn } from 'node:child_process';

export const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const manifest = JSON.parse(readFileSync(join(root, 'build/toolchain.lock.json'), 'utf8'));
export const platformId = (os, arch) => (os === 'win32' ? 'win' : os) + '-' + arch;
export const platform = platformId(process.platform, process.arch);
export function paths(base = root, system = platform, sdk = manifest.dotnetSdk) {
  if (!manifest.platforms[system]) throw new Error('No reviewed toolchain for ' + system);
  const windows = system.startsWith('win-');
  const nodeRoot = join(base, '.tools', system, 'node', manifest.node);
  const dotnetRoot = join(base, '.tools', system, 'dotnet', sdk);
  return { nodeRoot, dotnetRoot, node: join(nodeRoot, windows ? 'node.exe' : 'bin/node'),
    npm: join(nodeRoot, windows ? 'node_modules/npm/bin/npm-cli.js' : 'lib/node_modules/npm/bin/npm-cli.js'),
    dotnet: join(dotnetRoot, windows ? 'dotnet.exe' : 'dotnet') };
}
export function supportedSdk(version) {
  return /^8\.0\.[0-9]+$/.test(version) && Number(version.split('.')[2]) >= 400;
}
export function environment(dotnetRoot = paths().dotnetRoot) {
  const inherited = { ...process.env };
  const pathKey = Object.keys(inherited).find(k => k.toUpperCase() === 'PATH');
  const previousPath = pathKey ? inherited[pathKey] : '';
  for (const key of Object.keys(inherited)) if (key.toUpperCase() === 'PATH') delete inherited[key];
  return { ...inherited, PATH: dirname(paths().node) + delimiter + dotnetRoot + delimiter + previousPath, DOTNET_ROOT: dotnetRoot, DOTNET_ROOT_ARM64: dotnetRoot, DOTNET_ROOT_X64: dotnetRoot,
    DOTNET_CLI_HOME: join(root, '.cache/dotnet-home'), NUGET_PACKAGES: join(root, '.cache/nuget/packages'),
    NUGET_HTTP_CACHE_PATH: join(root, '.cache/nuget/http'), NUGET_PLUGINS_CACHE_PATH: join(root, '.cache/nuget/plugins'),
    DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1', DOTNET_NOLOGO: '1',
    DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false', DOTNET_MULTILEVEL_LOOKUP: '0', MSBUILDDISABLENODEREUSE: '1',
    npm_config_cache: join(root, '.cache/npm'), npm_config_userconfig: join(root, '.cache/empty.npmrc'),
    npm_config_update_notifier: 'false', SVM_NODE: paths().node };
}
export function verifyInstallation(name, sdk = manifest.dotnetSdk) {
  const p = paths(root, platform, sdk);
  const file = join(name === 'node' ? p.nodeRoot : p.dotnetRoot, '.svm-tool.json');
  if (!existsSync(file)) throw new Error('Run eng/bootstrap first; no verified ' + name + ' installation for ' + platform);
  const stamp = JSON.parse(readFileSync(file, 'utf8'));
  const archive = name === 'node' ? manifest.platforms[platform].node :
    sdk === manifest.dotnetSdk ? manifest.platforms[platform].dotnet : manifest.compatibilitySdks?.[sdk]?.[platform];
  if (stamp.platform !== platform || stamp.version !== (name === 'node' ? manifest.node : sdk) ||
      stamp.archiveHash !== archive?.hash || !existsSync(name === 'node' ? p.node : p.dotnet))
    throw new Error('Unverified ' + name + ' cache; reconcile only this platform installation.');
  return p;
}
export function dotnetCommand(reproducible = false) {
  const sdk = process.env.SVM_SDK_VERSION ?? manifest.dotnetSdk;
  if (!supportedSdk(sdk)) throw new Error('SVM_SDK_VERSION must select a supported stable .NET 8 SDK.');
  if (process.env.SVM_DOTNET && !isAbsolute(process.env.SVM_DOTNET)) throw new Error('SVM_DOTNET must be an absolute executable path.');
  const command = process.env.SVM_DOTNET || verifyInstallation('dotnet', sdk).dotnet;
  const env = environment(process.env.SVM_DOTNET ? dirname(command) : paths(root, platform, sdk).dotnetRoot);
  const version = execFileSync(command, ['--version'], { cwd: root, env, encoding: 'utf8' }).trim();
  if (!supportedSdk(version) || !process.env.SVM_DOTNET && version !== sdk || reproducible && version !== manifest.dotnetSdk)
    throw new Error(reproducible ? 'Publishing requires SDK ' + manifest.dotnetSdk : 'Unsupported development SDK.');
  return { command, env, version };
}
export async function run(command, args, options = {}) {
  return await new Promise((accept, reject) => {
    const child = spawn(command, args, { cwd: root, stdio: 'inherit', shell: false, ...options });
    child.once('error', reject);
    child.once('exit', (code, signal) => accept(code ?? (signal ? 130 : 1)));
  });
}
