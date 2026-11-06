import { readFile, writeFile, mkdir, access } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';

const args = process.argv.slice(2);
if (args.length !== 4 || args[0] !== '--deployment' || args[2] !== '--output') throw new Error('Usage: inventory.mjs --deployment folder --output new-private-folder');
const deployment = resolve(args[1]), output = resolve(args[3]);
const descriptor = JSON.parse(await readFile(join(deployment, 'deployment.json'), 'utf8'));
if (!/^svm-(demo|site|restore)-[a-f0-9]{12}$/.test(descriptor.project)) throw new Error('Invalid deployment identity.');
try { await access(output); throw new Error('Inventory output must be new.'); } catch (e) { if (e.code !== 'ENOENT') throw e; }
await mkdir(output, { recursive: true, mode: 0o700 });
const env = Object.fromEntries((await readFile(join(deployment, 'deployment.env'), 'utf8')).trim().split('\n').map(l => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]));
const docker = args => execFileSync('docker', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, timeout: 60000, stdio: ['ignore','pipe','pipe'] });
const files = [], images = [];
for (const role of ['RUNTIME','POSTGRES','NGINX',...(descriptor.mode === 'full' ? ['RABBITMQ'] : [])]) {
  const reference = env['SVM_' + role + '_IMAGE'];
  if (!reference || reference.startsWith('-') || /\s/.test(reference)) throw new Error('Invalid reviewed image reference.');
  const data = JSON.parse(docker(['image','inspect',reference]))[0];
  if (data.Os + '/' + data.Architecture !== descriptor.platform) throw new Error('Image architecture differs from deployment.');
  if (role !== 'RUNTIME' && (!/@sha256:[a-f0-9]{64}$/.test(reference) || !data.RepoDigests?.some(x=>x.endsWith(reference.slice(reference.indexOf('@')))))) throw new Error('Image digest differs from reviewed reference.');
  const run = ['run','--rm','--pull','never','--network','none','--read-only','--security-opt','no-new-privileges:true','--entrypoint','sh',data.Id,'-c'];
  const packages = docker([...run, 'if command -v dpkg-query >/dev/null; then dpkg-query -W -f=\'${Package}\\t${Version}\\t${Architecture}\\n\'; elif command -v apk >/dev/null; then apk info -v; else exit 4; fi']);
  const licenses = execFileSync('docker', [...run, 'set -- /usr/share/doc; for p in /usr/share/common-licenses /usr/share/dotnet/LICENSE.txt /usr/share/dotnet/ThirdPartyNotices.txt /opt/rabbitmq/LICENSE* /opt/rabbitmq/NOTICE* /usr/local/share/doc/postgresql; do if [ -e "$p" ]; then set -- "$@" "$p"; fi; done; tar -c -f - "$@" 2>/dev/null'], { maxBuffer: 128 * 1024 * 1024, timeout: 60000, stdio:['ignore','pipe','pipe'] });
  for (const [suffix, bytes] of [['packages.txt', Buffer.from(packages)], ['licenses.tar', licenses]]) {
    const path = role.toLowerCase() + '-' + suffix; await writeFile(join(output,path), bytes, {mode:0o600});
    files.push({path, size:bytes.length, sha256:createHash('sha256').update(bytes).digest('hex')});
  }
  images.push({role, reference, id:data.Id, repoDigests:data.RepoDigests, platform:descriptor.platform,
    vendorVersions:(data.Config.Env??[]).filter(x=>/^(PG_VERSION|RABBITMQ_VERSION|NGINX_VERSION|ASPNET_VERSION|DOTNET_VERSION)=/.test(x)),
    coverage:'Installed OS package database and image-provided /usr/share/doc and common-licenses. Images also include vendor applications; retain the immutable image and upstream source notices. No vulnerability or redistribution certification.'});
}
await writeFile(join(output,'images-inventory.json'),JSON.stringify({format:1,project:descriptor.project,releaseCommit:descriptor.releaseCommit,images,files},null,2)+'\n',{mode:0o600});
console.log(`Recorded ${images.length} immutable images with package and license evidence.`);
