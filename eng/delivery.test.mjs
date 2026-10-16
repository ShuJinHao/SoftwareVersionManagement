import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { root, paths, supportedSdk, platformId, resolveDotnet } from './toolchain.mjs';
import { changeImpact } from './ci-changes.mjs';
import { verifyWeb } from './publish.mjs';
import { renderNginx } from './packages/nginx.mjs';

test('toolchain accepts supported SDKs and rejects older, preview and other major versions', () => {
  for (const version of ['8.0.400','8.0.425','8.0.999']) assert.equal(supportedSdk(version), true);
  for (const version of ['8.0.399','8.0.425-preview.1','9.0.100','10.0.100','invalid']) assert.equal(supportedSdk(version), false);
});
function sdkResolver(variables, versions, repository = sdk => '/reviewed/' + sdk + '/dotnet') {
  const calls = [];
  const selected = () => resolveDotnet(variables, {
    probe: command => { calls.push(command); if (!(command in versions)) throw new Error('Missing SDK'); return { command, version: versions[command] }; },
    pathCommands: () => ['/path/old/dotnet', '/path/compatible/dotnet'], repository,
  });
  return { calls, selected };
}
test('toolchain honors explicit executable before an explicit repository version and never falls back on invalid explicit input', () => {
  const explicit = sdkResolver({ SVM_DOTNET: '/explicit/dotnet', SVM_SDK_VERSION: 'invalid' }, { '/explicit/dotnet': '8.0.400' });
  assert.equal(explicit.selected().source, 'SVM_DOTNET'); assert.deepEqual(explicit.calls, ['/explicit/dotnet']);
  const invalid = sdkResolver({ SVM_DOTNET: '/missing/dotnet' }, { '/path/compatible/dotnet': '8.0.425' });
  assert.throws(invalid.selected, /SVM_DOTNET/); assert.deepEqual(invalid.calls, ['/missing/dotnet']);
  assert.throws(() => resolveDotnet({ SVM_DOTNET: '' }), /absolute/);
  assert.throws(() => resolveDotnet({ SVM_DOTNET: 'relative/dotnet' }), /absolute/);
});
test('toolchain requires the exact verified repository cache for an explicit SDK version', () => {
  const explicit = sdkResolver({ SVM_SDK_VERSION: '8.0.400' }, { '/reviewed/8.0.400/dotnet': '8.0.400', '/path/compatible/dotnet': '8.0.425' });
  assert.equal(explicit.selected().version, '8.0.400'); assert.deepEqual(explicit.calls, ['/reviewed/8.0.400/dotnet']);
  const mismatch = sdkResolver({ SVM_SDK_VERSION: '8.0.400' }, { '/reviewed/8.0.400/dotnet': '8.0.425', '/path/compatible/dotnet': '8.0.425' });
  assert.throws(mismatch.selected, /matching verified/); assert.deepEqual(mismatch.calls, ['/reviewed/8.0.400/dotnet']);
  assert.throws(() => resolveDotnet({ SVM_SDK_VERSION: '' }), /supported/);
});
test('toolchain tries compatible PATH SDKs before reviewed repository fallback', () => {
  const found = sdkResolver({}, { '/path/old/dotnet': '7.0.410', '/path/compatible/dotnet': '8.0.400', '/reviewed/8.0.425/dotnet': '8.0.425' });
  assert.equal(found.selected().source, 'PATH'); assert.deepEqual(found.calls, ['/path/old/dotnet', '/path/compatible/dotnet']);
  const fallback = sdkResolver({}, { '/path/old/dotnet': '9.0.100', '/reviewed/8.0.425/dotnet': '8.0.425' });
  assert.equal(fallback.selected().source, 'repository'); assert.deepEqual(fallback.calls, ['/path/old/dotnet', '/path/compatible/dotnet', '/reviewed/8.0.425/dotnet']);
});
test('toolchain node-only bootstrap does not require or replace an explicitly provided company SDK', () => {
  const result = spawnSync(process.execPath,[join(root,'eng/bootstrap.mjs'),'--node-only'],
    {encoding:'utf8',env:{...process.env,SVM_DOTNET:'/unavailable/company/dotnet'}});
  assert.equal(result.status,0,result.stderr); assert.match(result.stdout,/Node-only preparation/);
});
test('toolchain separates platform caches and resolves Windows executables without Bash', () => {
  assert.equal(platformId('win32','x64'),'win-x64');
  const windows = paths('/workspace/中文 project',platformId('win32','x64'));
  assert.ok(windows.dotnet.endsWith('dotnet.exe')); assert.ok(windows.node.endsWith('node.exe'));
  assert.ok(windows.npm.includes('node_modules/npm/bin/npm-cli.js'));
  assert.notEqual(windows.dotnetRoot,paths('/workspace/中文 project','linux-x64').dotnetRoot);
  assert.notEqual(paths(root,'linux-x64','8.0.400').dotnetRoot,paths(root,'linux-x64').dotnetRoot);
  assert.throws(()=>paths(root,'win-arm64'), /No reviewed/);
});
test('toolchain rejects broad test selection and results escaping the evidence folder', () => {
  const invoke = args => spawnSync(process.execPath,[join(root,'eng/test.mjs'),...args],{encoding:'utf8'});
  assert.notEqual(invoke(['framework','--preview']).status,0);
  assert.notEqual(invoke(['architecture','--filter','anything','--preview']).status,0);
  assert.notEqual(invoke(['tools','--filter','toolchain','--results-directory','../../outside','--preview']).status,0);
  assert.equal(invoke(['tools','--filter','toolchain','--preview']).status,0);
});
test('web selection requires exact existing files and explicit test names', () => {
  const invoke = args => spawnSync(process.execPath, [join(root, 'eng/test.mjs'), 'web', ...args, '--preview'], { encoding: 'utf8' });
  for (const args of [[], ['--filter','response loss'], ['--file','tests/*.test.ts','--filter','response loss'],
    ['--file','../tests/deployments.test.ts','--filter','response loss'], ['--file','tests/missing.test.ts','--filter','response loss'],
    ['--file','tests/deployments.test.ts','--filter','.*']]) assert.notEqual(invoke(args).status, 0);
  const accepted = invoke(['--file','tests/deployments.test.ts','--filter','response loss']);
  assert.equal(accepted.status,0); assert.deepEqual(JSON.parse(accepted.stdout).files, ['tests/deployments.test.ts']);
  assert.equal(JSON.parse(accepted.stdout).preview,true);
  const unmatched=spawnSync(process.execPath,[join(root,'eng/test.mjs'),'web','--file','tests/deployments.test.ts','--filter','this-test-does-not-exist','--list'],{encoding:'utf8'});
  assert.notEqual(unmatched.status,0); assert.match(unmatched.stderr,/match no tests/);
});
test('CI impact skips pure documentation and selects tools and web independently', () => {
  assert.deepEqual(changeImpact(['README.md','docs/详细设计.md']),{code:false,tools:false,web:false,browser:false});
  assert.deepEqual(changeImpact(['eng/toolchain.mjs']),{code:true,tools:true,web:false,browser:false});
  assert.deepEqual(changeImpact(['src/ui/svm-web/tests/deployments.test.ts']),{code:true,tools:false,web:true,browser:false});
  assert.deepEqual(changeImpact(['src/ui/svm-web/src/pages/DeploymentsPage.vue']),{code:true,tools:false,web:true,browser:true});
  assert.deepEqual(changeImpact(['deploy/linux/verify-ui.mjs']),{code:true,tools:false,web:false,browser:true});
});
test('publish rejects missing page, referenced assets and license notices', async () => {
  await mkdir(join(root,'artifacts/test-results/tools-fixtures'),{recursive:true});
  const folder=await mkdtemp(join(root,'artifacts/test-results/tools-fixtures','中文 spaced-'));
  try {
    await assert.rejects(verifyWeb(folder));
    await writeFile(join(folder,'index.html'),'<script src="/assets/app.js"></script>');
    await assert.rejects(verifyWeb(folder));
    await mkdir(join(folder,'assets'));await writeFile(join(folder,'assets/app.js'),'');
    await assert.rejects(verifyWeb(folder));
    await mkdir(join(folder,'licenses'));
    for(const name of ['hash-wasm-4.12.0-MIT.txt','hash-wasm-4.12.0-SHA256.txt']) await writeFile(join(folder,'licenses',name),'notice');
    await verifyWeb(folder);
  } finally {await rm(folder,{recursive:true,force:true});}
});
const gateway = () => ({workerUser:'nginx',certificateDirectory:'/private/certificates',gatewayFingerprints:['AA:'.repeat(19)+'AA','BB:'.repeat(19)+'BB'],
  nodes:['node-a','node-b'].map((nodeId,i)=>({nodeId,generation:'a06fa973-45d4-40e2-885f-ed7ebca6d22b',apiHost:'api-'+(i?'b':'a'),apiTlsName:'api-'+(i?'b':'a'),gatewayTlsName:'gateway',
    publicPort:10443+i*1000,privatePort:20443+i*1000,apiPublicPort:7443,apiInternalPort:7444,rootPath:'/private/'+nodeId,logDirectory:'/logs/'+nodeId,maxPackageBytes:1024,uploadIdleSeconds:30}))});
test('nginx uses explicit service names, validates TLS and overwrites spoofable forwarding headers', () => {
  const text=renderNginx(gateway());
  assert.ok(!text.includes('host.docker.internal'));assert.ok(!text.includes('proxy_ssl_name localhost'));
  assert.ok(text.includes('server api-a:7443'));assert.ok(text.includes('proxy_ssl_verify on'));
  assert.ok(text.includes('proxy_set_header X-Forwarded-For $remote_addr'));
  assert.ok(!text.includes('$proxy_add_x_forwarded_for'));
  assert.ok(text.includes('proxy_set_header If-Range $http_if_range'));
  assert.ok(text.includes('auth_request /__svm_authorize'));
  const nonroot=gateway();nonroot.workerUser=null;nonroot.resolverAddress='127.0.0.11';const config=renderNginx(nonroot);
  assert.ok(!/^user /m.test(config));
  assert.ok(config.includes('server api-a:7443 resolve;'));assert.ok(config.includes('zone public_node_a 64k; resolver 127.0.0.11 valid=5s'));
  for(const kind of ['client_body','proxy','fastcgi','uwsgi','scgi']) assert.match(config,new RegExp(kind+'_temp_path /tmp/'));
});
test('nginx rejects injected addresses and incomplete explicit topology', () => {
  const c=gateway();c.nodes[0].apiHost='api-a; injected';assert.throws(()=>renderNginx(c));
  const resolver=gateway();resolver.resolverAddress='127.0.0.11; injection';assert.throws(()=>renderNginx(resolver));
  const missing=gateway();delete missing.nodes[0].apiTlsName;assert.throws(()=>renderNginx(missing));
});
test('solution retains exactly the approved projects and unique standard folder metadata', async () => {
  const text=await readFile(join(root,'SoftwareVersionManagement.sln'),'utf8');
  assert.ok(!text.includes('66A26720-8FB5-11D2-AA7E-00C04F688DDE'));
  const entries=[...text.matchAll(/^Project\("\{([^}]+)\}"\) = "([^"]+)", "([^"]+)", "\{([^}]+)\}"/gm)];
  assert.equal(entries.filter(e=>e[3].endsWith('.csproj')).length,29);
  assert.equal(new Set(entries.map(e=>e[4])).size,entries.length);
  assert.equal(entries.filter(e=>e[2]==='modules').length,1);
  const nested=text.split('GlobalSection(NestedProjects)')[1].split('EndGlobalSection')[0].split('\n').filter(l=>l.includes(' = '));
  assert.equal(new Set(nested.map(l=>l.split(' = ')[0].trim())).size,nested.length);
});
