import { readFileSync, writeFileSync, lstatSync, unlinkSync, openSync, closeSync } from 'node:fs';
import { resolve, isAbsolute } from 'node:path';
import { pathToFileURL } from 'node:url';

// Conversion never contacts GitLab or the database. Adoption uses the normal authorized PUT endpoint.
export function convertLegacySources(config) {
  const bindings = config?.import?.bindings, credentials = config?.credentials;
  if (!Array.isArray(bindings) || !bindings.length || bindings.length > 1000 || !Array.isArray(credentials)) throw new Error('Invalid legacy configuration.');
  const result = structuredClone(config);
  result.import.connections ??= []; result.connectionCredentials ??= [];
  if (!Array.isArray(result.import.connections) || !Array.isArray(result.connectionCredentials)) throw new Error('Invalid connection catalog.');
  const seen = new Set();
  for (const binding of bindings) {
    if (typeof binding.id !== 'string' || !/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(binding.id) || seen.has(binding.id) ||
      !Number.isSafeInteger(binding.projectId) || binding.projectId <= 0 || typeof binding.packageName !== 'string' || !/^[A-Za-z0-9._-]{1,200}$/.test(binding.packageName) || binding.packageName === '.' || binding.packageName === '..') throw new Error('Invalid legacy binding.');
    seen.add(binding.id);
    let uri; try { uri = new URL(binding.baseUri); } catch { throw new Error('Invalid HTTPS source.'); }
    if (uri.protocol !== 'https:' || uri.username || uri.password || uri.search || uri.hash || !binding.baseUri.endsWith('/')) throw new Error('Invalid HTTPS source.');
    const matches = credentials.filter(value => value.bindingId === binding.id);
    if (matches.length !== 1 || [matches[0].apiToken, matches[0].packageToken].some(value => typeof value !== 'string' || value.length < 8 || value.length > 4096 || /\s/.test(value))) throw new Error('Missing or invalid private credentials.');
    const connection = { id: binding.id, name: `GitLab project ${binding.projectId}`, baseUri: binding.baseUri, enabled: true,
      projects: [{ id: binding.projectId, name: `Project ${binding.projectId}`, packages: [binding.packageName] }] };
    const secret = { connectionId: binding.id, apiToken: matches[0].apiToken, packageToken: matches[0].packageToken };
    const prior = result.import.connections.find(c => c.id === binding.id), priorSecret = result.connectionCredentials.find(c => c.connectionId === binding.id);
    if (prior && JSON.stringify(prior) !== JSON.stringify(connection) || priorSecret && JSON.stringify(priorSecret) !== JSON.stringify(secret)) throw new Error('Existing connection differs; automatic overwrite is forbidden.');
    if (!prior) result.import.connections.push(connection);
    if (!priorSecret) result.connectionCredentials.push(secret);
  }
  return result;
}
export function convertFile(input, output) {
  if (!isAbsolute(input) || output && !isAbsolute(output)) throw new Error('Use absolute private file paths.');
  const stat = lstatSync(input);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 4 * 1024 * 1024 || process.platform !== 'win32' && (stat.mode & 0o077)) throw new Error('Source must be a private regular file.');
  const converted = convertLegacySources(JSON.parse(readFileSync(input, 'utf8')));
  if (output) {
    const fd = openSync(output, 'wx', 0o600);
    try { writeFileSync(fd, JSON.stringify(converted, null, 2) + '\n'); }
    catch (error) { unlinkSync(output); throw error; }
    finally { closeSync(fd); }
  }
  return { action: output ? 'converted' : 'preview', bindings: converted.import.bindings.map(b => ({ bindingId: b.id, subjectId: b.subjectId,
    softwareId: b.softwareId, connectionId: b.id, projectId: b.projectId, packageName: b.packageName })),
    databaseChanged: false, next: 'Restart with the converted private file, then explicitly adopt each source in its software panel. Existing IDs and history are retained.' };
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const [mode, input, output, ...extra] = process.argv.slice(2);
    if (extra.length || !input || mode !== 'preview' && mode !== 'apply' || mode === 'preview' && output || mode === 'apply' && !output) throw new Error('Usage: source-config.mjs preview absolute-input | apply absolute-input absolute-new-output');
    console.log(JSON.stringify(convertFile(input, mode === 'apply' ? output : undefined), null, 2));
  } catch { console.error('Conversion rejected: check command, private input and unused output path. No credentials are printed.'); process.exitCode = 1; }
}
