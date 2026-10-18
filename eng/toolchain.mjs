import { readFileSync, existsSync, realpathSync, accessSync, constants } from 'node:fs';
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
export function environment(dotnetRoot, variables = process.env) {
  const inherited = { ...variables };
  const pathKey = Object.keys(inherited).find(k => k.toUpperCase() === 'PATH');
  const previousPath = pathKey ? inherited[pathKey] : '';
  for (const key of Object.keys(inherited)) if (key.toUpperCase() === 'PATH') delete inherited[key];
  return { ...inherited, PATH: [dirname(paths().node), dotnetRoot, previousPath].filter(Boolean).join(delimiter),
    ...(dotnetRoot ? { DOTNET_ROOT: dotnetRoot, DOTNET_ROOT_ARM64: dotnetRoot, DOTNET_ROOT_X64: dotnetRoot } : {}),
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
function pathCommands(variables) {
  const key = Object.keys(variables).find(k => k.toUpperCase() === 'PATH');
  const filename = process.platform === 'win32' ? 'dotnet.exe' : 'dotnet';
  const candidates = (variables[key] ?? '').split(delimiter).filter(Boolean).map(folder => join(folder, filename));
  return [...new Set(candidates)].filter(command => {
    try { accessSync(command, constants.X_OK); return true; } catch { return false; }
  });
}
function probeDotnet(command, variables) {
  const actual = realpathSync(command);
  const env = environment(dirname(actual), variables);
  const version = execFileSync(actual, ['--version'], { cwd: root, env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  return { command: actual, env, version };
}
// Development and publishing share one range. Explicit selections never silently fall back.
export function resolveDotnet(variables = process.env, adapters = {}) {
  const probe = adapters.probe ?? ((command) => probeDotnet(command, variables));
  const repository = adapters.repository ?? (sdk => verifyInstallation('dotnet', sdk).dotnet);
  function inspect(command, expected) {
    const selected = probe(command);
    if (!supportedSdk(selected.version) || expected && selected.version !== expected) throw new Error('Unsupported .NET 8 SDK selection.');
    return selected;
  }
  if (variables.SVM_DOTNET !== undefined) {
    if (!variables.SVM_DOTNET || !isAbsolute(variables.SVM_DOTNET)) throw new Error('SVM_DOTNET must be an absolute executable path.');
    try { return { ...inspect(variables.SVM_DOTNET), source: 'SVM_DOTNET' }; }
    catch { throw new Error('SVM_DOTNET is unavailable or does not resolve a supported stable .NET 8 SDK.'); }
  }
  if (variables.SVM_SDK_VERSION !== undefined) {
    const sdk = variables.SVM_SDK_VERSION;
    if (!supportedSdk(sdk)) throw new Error('SVM_SDK_VERSION must select a supported stable .NET 8 SDK.');
    try { return { ...inspect(repository(sdk), sdk), source: 'SVM_SDK_VERSION' }; }
    catch { throw new Error('SVM_SDK_VERSION requires its matching verified repository SDK; run eng/bootstrap for that reviewed version.'); }
  }
  for (const command of (adapters.pathCommands ?? pathCommands)(variables)) {
    try { return { ...inspect(command), source: 'PATH' }; } catch { /* An implicit incompatible PATH entry is not a selection. */ }
  }
  for (const sdk of [manifest.dotnetSdk, ...Object.keys(manifest.compatibilitySdks ?? {})]) {
    try { return { ...inspect(repository(sdk), sdk), source: 'repository' }; } catch { /* Try the next reviewed installed cache. */ }
  }
  throw new Error('No supported stable .NET 8 SDK found; provide SVM_DOTNET or run eng/bootstrap.');
}
export function dotnetCommand() {
  return resolveDotnet();
}
export async function run(command, args, options = {}) {
  return await new Promise((accept, reject) => {
    const child = spawn(command, args, { cwd: root, stdio: 'inherit', shell: false, ...options });
    child.once('error', reject);
    child.once('exit', (code, signal) => accept(code ?? (signal ? 130 : 1)));
  });
}
