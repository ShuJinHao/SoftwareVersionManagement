import { access, chmod, cp, lstat, mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { createReadStream as fileStream } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';
import { root } from './toolchain.mjs';
import { renderNginx } from './packages/nginx.mjs';

export function candidateGatewayTemplate() {
  const nodes = ['node-a','node-b'];
  let text = renderNginx({workerUser:null,resolverAddress:'127.0.0.11',certificateDirectory:'/svm/gateway/certs',
    gatewayFingerprints:['AA:'.repeat(19)+'AA','BB:'.repeat(19)+'BB'],nodes:nodes.map((node,i)=>({nodeId:node,
      generation:i?'22222222-2222-4222-8222-222222222222':'11111111-1111-4111-8111-111111111111',publicPort:10443+i*1000,privatePort:20443+i*1000,
      apiHost:'api-'+node.at(-1),apiTlsName:'api-'+node.at(-1),gatewayTlsName:'gateway',apiPublicPort:7443,apiInternalPort:7444,
      rootPath:'/svm/'+node+'/files',logDirectory:'/svm/'+node+'/logs',maxPackageBytes:987654321012,uploadIdleSeconds:599,downloadAuthorizationSeconds:27}))});
  for(const [from,to] of [['a'.repeat(40),'FINGERPRINT_node-a'],['b'.repeat(40),'FINGERPRINT_node-b'],
    ['11111111-1111-4111-8111-111111111111','GENERATION_node-a'],['22222222-2222-4222-8222-222222222222','GENERATION_node-b'],
    ['987654321012','MAX_BYTES'],['599','IDLE_SECONDS'],['27s; leave','AUTH_SECONDS@@s; leave']]) {
    if(!text.includes(from))throw Error('Gateway template sentinel missing.');
    text=text.replaceAll(from,to==='AUTH_SECONDS@@s; leave'?'@@'+to:'@@'+to+'@@');
  }
  return text;
}
export async function digest(path) { const hash=createHash('sha256');for await(const bytes of fileStream(path))hash.update(bytes);return hash.digest('hex'); }
export async function publicInventory(directory) {
  const files=[];
  async function walk(folder) {
    for(const name of (await readdir(folder)).sort()) {
      const path=join(folder,name),info=await lstat(path),local=relative(directory,path).replaceAll('\\','/');
      if(info.isSymbolicLink()||!info.isFile()&&!info.isDirectory()||!/^[-A-Za-z0-9_./@+]+$/.test(local)||local.split('/').some(p=>p==='..'||p==='.'||!p))throw Error('Invalid public bundle entry.');
      if(info.isDirectory())await walk(path);else files.push({path:local,size:info.size,sha256:await digest(path)});
      if(files.length>10000)throw Error('Candidate inventory bound exceeded.');
    }
  }
  await walk(directory);return files;
}
async function main() {
  const args=process.argv.slice(2);
  if(args.length!==4||args[0]!=='--release'||args[2]!=='--output')throw Error('Usage: eng/node eng/candidate.mjs --release complete-clean-release --output new-bundle-directory');
  const release=resolve(args[1]),output=resolve(args[3]);
  const head=execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim();
  if(execFileSync('git',['status','--porcelain'],{cwd:root,encoding:'utf8'}).trim())throw Error('Candidate requires a clean workspace.');
  const manifest=JSON.parse(await readFile(join(release,'release.json'),'utf8'));
  if(manifest.dirty||manifest.commit!==head||manifest.runtimeMinimum!=='8.0.31'||manifest.runtimeRollForward!=='LatestPatch')throw Error('Candidate requires the current clean complete Release.');
  const actual=await publicInventory(release);
  if(actual.length!==manifest.files.length+1)throw Error('Unexpected release content.');
  for(const file of manifest.files)if(!actual.some(x=>x.path===file.path&&x.size===file.size&&x.sha256===file.sha256))throw Error('Release checksum mismatch.');
  if(output===release||output.startsWith(release+'/')||release.startsWith(output+'/'))throw Error('Bundle and Release must be separate.');
  try{await access(output);throw Error('Candidate output already exists.');}catch(e){if(e.code!=='ENOENT')throw e;}
  const candidateId='RC-'+new Date().toISOString().slice(0,10).replaceAll('-','')+'-'+head.slice(0,7),platform='linux/amd64';
  const archive=join(dirname(output),candidateId+'.tar.gz');
  try{await access(archive);throw Error('Candidate archive already exists.');}catch(e){if(e.code!=='ENOENT')throw e;}
  const docker=(args,options={})=>execFileSync('docker',args,{encoding:'utf8',timeout:120000,maxBuffer:128*1024*1024,stdio:['ignore','pipe','pipe'],...options});
  await mkdir(output,{recursive:true});await mkdir(join(output,'templates'));await mkdir(join(output,'materials'));await mkdir(join(output,'docs'));
  await cp(release,join(output,'release'),{recursive:true});
  for(const name of ['compose.yaml','compose.preview.yaml','compose.recovery.yaml','compose.review.yaml','svm.sh','operations.sh'])await cp(join(root,'deploy/linux',name),join(output,'templates',name));
  for(const name of ['ACCEPTANCE.md','ACCOUNTS.md','REGRESSION.md','OPERATIONS.md','DELIVERY-CHECKLIST.md'])await cp(join(root,'deploy/linux',name),join(output,'docs',name));
  await mkdir(join(output,'docs/gitlab'));
  for(const name of ['README.md','manifest.mjs','source-config.mjs','gitlab-ci.example.yml'])await cp(join(root,'eng/gitlab',name),join(output,'docs/gitlab',name));
  const gitLabReadme=await readFile(join(output,'docs/gitlab/README.md'),'utf8');
  await writeFile(join(output,'docs/gitlab/README.md'),gitLabReadme.replaceAll('../../docs/软件框架设计.md#11-审阅出口与当前验证状态','https://github.com/ShuJinHao/SoftwareVersionManagement/blob/'+head+'/docs/软件框架设计.md#11-审阅出口与当前验证状态'));
  const checklist=await readFile(join(output,'docs/DELIVERY-CHECKLIST.md'),'utf8');await writeFile(join(output,'docs/DELIVERY-CHECKLIST.md'),checklist.replaceAll('../../eng/gitlab/README.md','gitlab/README.md'));
  await cp(join(root,'deploy/linux/acceptance-settings.template.json'),join(output,'acceptance-settings.template.json'));
  await cp(join(root,'deploy/linux/install.sh'),join(output,'install.sh'));await chmod(join(output,'install.sh'),0o755);
  await writeFile(join(output,'templates/nginx.conf'),candidateGatewayTemplate());
  const recipe=await readFile(join(root,'deploy/linux/Dockerfile'),'utf8');
  const base=/^FROM (\S+@sha256:[a-f0-9]{64})$/m.exec(recipe)?.[1];if(!base)throw Error('Pinned runtime base required.');
  const references={ASPNET:base};
  for(const [role,file] of [['POSTGRES','postgres.local.json'],['RABBITMQ','rabbitmq.local.json'],['NGINX','nginx.packages.json']])references[role]=JSON.parse(await readFile(join(root,'build',file),'utf8')).images[platform];
  const images=[];
  for(const [role,reference] of Object.entries(references)) {
    console.log('Candidate material: '+role+' linux/amd64');
    docker(['pull','--platform',platform,reference],{timeout:300000});
    const data=JSON.parse(docker(['image','inspect',reference]))[0];
    if(data.Os+'/'+data.Architecture!==platform||!data.RepoDigests?.some(d=>d.endsWith(reference.slice(reference.indexOf('@')))))throw Error('Pinned image identity mismatch.');
    const tag='svm-candidate/'+candidateId.toLowerCase()+':'+role.toLowerCase();
    // Candidate names are immutable; never replace an unrelated tag.
    let existing;try{existing=JSON.parse(docker(['image','inspect',tag]))[0].Id;}catch{}
    if(existing&&existing!==data.Id)throw Error('Candidate image tag already has different content.');
    docker(['tag',data.Id,tag]);images.push({role,reference,tag,id:data.Id,platform});
    const run=['run','--rm','--pull','never','--platform',platform,'--network','none','--read-only','--security-opt','no-new-privileges:true','--entrypoint','sh',data.Id,'-c'];
    const packages=docker([...run,"if command -v dpkg-query >/dev/null; then dpkg-query -W -f='${Package}\\t${Version}\\t${Architecture}\\n'; elif command -v apk >/dev/null; then apk info -v; else exit 4; fi"]);
    await writeFile(join(output,'materials',role.toLowerCase()+'-packages.txt'),packages);
    const licenses=docker([...run,'set -- /usr/share/doc; for p in /usr/share/common-licenses /usr/share/dotnet/LICENSE.txt /usr/share/dotnet/ThirdPartyNotices.txt /opt/rabbitmq/LICENSE* /opt/rabbitmq/NOTICE* /usr/local/share/doc/postgresql; do if [ -e "$p" ]; then set -- "$@" "$p"; fi; done; tar -c -f - "$@" 2>/dev/null'],{encoding:null});
    await writeFile(join(output,'materials',role.toLowerCase()+'-licenses.tar'),licenses);
  }
  await writeFile(join(output,'templates/Dockerfile'),recipe.replace(base,images.find(x=>x.role==='ASPNET').tag));
  await writeFile(join(output,'images.lock.tsv'),images.map(i=>[i.role,i.reference,i.tag,i.id,i.platform].join('\t')).join('\n')+'\n');
  docker(['save','-o',join(output,'images.tar'),...images.map(i=>i.tag)],{timeout:300000});
  await writeFile(join(output,'materials/README.txt'),'Immutable image IDs, actual OS package inventory and image-provided license material. The application SBOM and build assets are in release/materials. This is not vulnerability clearance or redistribution certification. Runtime assembly only adds the verified published files and public site CA; no private keys or credentials are in these images.\n');
  const files=await publicInventory(output);
  await writeFile(join(output,'candidate.json'),JSON.stringify({format:1,candidateId,commit:head,platform,images,files},null,2)+'\n');
  const inventory=await publicInventory(output);
  await writeFile(join(output,'SHA256SUMS'),inventory.map(f=>f.sha256+'  '+f.path).join('\n')+'\n');
  execFileSync('tar',['-C',output,'-czf',archive,'.'],{timeout:300000,stdio:['ignore','pipe','pipe']});
  await writeFile(archive+'.sha256',await digest(archive)+'  '+candidateId+'.tar.gz\n');
  console.log(JSON.stringify({candidateId,commit:head,platform,files:inventory.length+1,output,archive}));
}
if(process.argv[1]&&resolve(process.argv[1])===join(root,'eng/candidate.mjs'))await main().catch(()=>{console.error('Candidate assembly failed; incomplete output is not a candidate. Inspect the named stage before retrying in a new directory.');process.exitCode=1;});
