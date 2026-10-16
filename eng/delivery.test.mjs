import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { root, paths, supportedSdk, platformId } from './toolchain.mjs';
import { verifyWeb } from './publish.mjs';
import { renderNginx } from './packages/nginx.mjs';

test('toolchain accepts supported SDKs and rejects older, preview and other major versions', () => {
  for (const version of ['8.0.400','8.0.425','8.0.999']) assert.equal(supportedSdk(version), true);
  for (const version of ['8.0.399','8.0.425-preview.1','9.0.100','10.0.100','invalid']) assert.equal(supportedSdk(version), false);
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
