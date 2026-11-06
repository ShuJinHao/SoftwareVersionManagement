import { readFile, readdir, mkdir, writeFile, copyFile, realpath } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { createHash } from 'node:crypto';
import { root, paths } from './toolchain.mjs';

const json = async file => JSON.parse(await readFile(file, 'utf8'));
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const packageRef = (type, name, version) => `pkg:${type}/${name.replace('@', '%40')}@${version}`;

export function applicationBom(policy, npm, hosts, commit) {
  const components = new Map();
  function add(type, name, version, location, integrity) {
    const reviewed = policy[type].find(p => p.name.toLowerCase() === name.toLowerCase() && p.version === version);
    if (!reviewed) throw new Error(`Unreviewed release component: ${type}/${name}@${version}`);
    const ref = packageRef(type, reviewed.name, version);
    if (!components.has(ref)) components.set(ref, { type: 'library', 'bom-ref': ref, name: reviewed.name, version,
      purl: ref, licenses: [{ expression: reviewed.license }], properties: [] });
    const component = components.get(ref);
    if (!component.properties.some(p => p.value === location)) component.properties.push({ name: 'svm:observed-in', value: location });
    if (integrity && !component.properties.some(p => p.name === 'svm:archive-integrity')) component.properties.push({ name: 'svm:archive-integrity', value: integrity });
  }
  for (const [host, deps] of Object.entries(hosts)) {
    for (const [identity, library] of Object.entries(deps.libraries)) {
      if (library.type !== 'package') continue;
      const split = identity.lastIndexOf('/');
      add('nuget', identity.slice(0, split), identity.slice(split + 1), host + '.deps.json', library.sha512);
    }
  }
  for (const [path, item] of Object.entries(npm.packages)) {
    if (!path || item.dev) continue;
    add('npm', item.name ?? path.split('node_modules/').at(-1), item.version, 'web-lock-runtime-closure', item.integrity);
  }
  return { bomFormat: 'CycloneDX', specVersion: '1.6', version: 1,
    metadata: { component: { type: 'application', name: 'SoftwareVersionManagement', version: commit },
      properties: [{ name: 'svm:coverage', value: 'Published backend deps and conservative non-dev web lock closure; web tree shaking may remove code. Host OS/runtime images are inventoried separately. This is not a vulnerability scan or legal approval.' }] },
    components: [...components.values()].sort((a, b) => a.purl.localeCompare(b.purl)) };
}

export async function writeReleaseMaterials(output, dotnet, commit) {
  const policy = await json(join(root, 'build/dependency-policy.json'));
  const npm = await json(join(root, 'src/ui/svm-web/package-lock.json'));
  const hostNames = ['Svm.HttpApi', 'Svm.Worker', 'Svm.Migration'];
  const hosts = Object.fromEntries(await Promise.all(hostNames.map(async host => [host, await json(join(output, host, host + '.deps.json'))])));
  await writeFile(join(output, 'sbom.cdx.json'), JSON.stringify(applicationBom(policy, npm, hosts, commit), null, 2) + '\n');
  const build = { format: 1, commit, sdk: dotnet.version, node: process.version, npm: (await json(join(root, 'build/toolchain.lock.json'))).npm,
    shipped: false, nuget: policy.nuget, npmPackages: policy.npm,
    nativeBuildPackages: Object.entries(npm.packages).filter(([p, v]) => p && (v.os || v.cpu)).map(([p, v]) => ({ path: p, version: v.version, os: v.os, cpu: v.cpu, integrity: v.integrity })),
    browserTools: (await json(join(root, 'src/ui/svm-web/node_modules/playwright-core/browsers.json'))).browsers,
    notices: 'SDK/Node/npm/browser tools are build/test inputs, not Linux runtime requirements. Browser revisions are declared Playwright inputs, not a claim that each binary was installed or tested.' };
  const materials = join(output, 'licenses/build-tools'); await mkdir(materials, { recursive: true });
  const sdkRoot = dirname(await realpath(dotnet.command)); const nodeRoot = dirname(await realpath(paths().node));
  const roots = [['dotnet', sdkRoot], ['node', process.platform === 'win32' ? nodeRoot : dirname(nodeRoot)], ['npm', dirname(dirname(paths().npm))]];
  const evidence = [];
  for (const [tool, folder] of roots) {
    let found = 0;
    for (const name of await readdir(folder)) {
      if (!/^(license|third.?party.?notices)(\.txt)?$/i.test(name)) continue;
      const bytes = await readFile(join(folder, name)); const target = tool + '-' + name;
      await copyFile(join(folder, name), join(materials, target)); found++;
      evidence.push({ tool, path: 'licenses/build-tools/' + target, sha256: digest(bytes) });
    }
    if (!found) throw new Error('Missing build tool license: ' + tool);
  }
  build.licenseEvidence = evidence;
  await writeFile(join(output, 'build-inventory.json'), JSON.stringify(build, null, 2) + '\n');
  await writeFile(join(output, 'THIRD-PARTY.md'), `# Third-party delivery materials\n\nCode commit: ${commit}.\n\n- sbom.cdx.json: backend packages observed in published deps plus the conservative runtime web lock closure.\n- build-inventory.json: all reviewed build/test packages, native platform variants and declared browser revisions; these tools are not deployed.\n- licenses/: archived package and tool notices, including hash-wasm embedded C evidence in wwwroot/licenses.\n- Runtime, PostgreSQL, RabbitMQ, Nginx and their OS packages: run deploy/linux/inventory.mjs against the actual prepared image IDs. Keep its image inventory and license archives with the release. A package lock is not an OS image audit.\n\nThe project itself has no added open-source license. Recorded license expressions and source notices do not grant additional redistribution rights. Review corresponding source/notice obligations before external redistribution. No claim of vulnerability-free software is made.\n`);
}
