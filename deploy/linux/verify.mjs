import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

const args=process.argv.slice(2);
const browserOnly=args.at(-1)==='--browser-only';
const existing=browserOnly || args.at(-1)==='--verify-existing';if(existing)args.pop();
if(args.length!==4 || args[0]!=='--deployment' || args[2]!=='--results') throw new Error('Usage: eng/node deploy/linux/verify.mjs --deployment folder --results folder [--verify-existing|--browser-only]');
const deployment=resolve(args[1]), results=resolve(args[3]);
const config=JSON.parse(await readFile(join(deployment,'operator.json'),'utf8'));
const identity=JSON.parse(await readFile(join(deployment,'deployment.json'),'utf8'));
if(!identity.demo || !identity.project.startsWith('svm-demo-')) throw new Error('Only an explicitly disposable demo may be verified.');
await mkdir(results,{recursive:true});
const agent=new Agent({ca:await readFile(resolve(deployment,config.caFile)),keepAlive:true});
const cookies=new Map(), stages=[];
let phase='startup', browser;
const now=()=>new Date().toISOString();
async function call(path,{method='GET',body,bearer,headers={},url=config.url,cookie=true}={}) {
  const bytes=body===undefined?null:Buffer.isBuffer(body)?body:Buffer.from(JSON.stringify(body));
  return await new Promise((accept,reject)=>{
    const req=https(new URL(path,url),{agent,method,headers:{...(cookie&&!bearer?{Cookie:[...cookies].map(([k,v])=>k+'='+v).join('; ')}:{}),
      ...(bearer?{Authorization:'Bearer '+bearer}:{}),...(bytes?{'Content-Type':Buffer.isBuffer(body)?'application/octet-stream':'application/json','Content-Length':bytes.length}:{}),...headers}},response=>{
      if(cookie&&!bearer)for(const value of response.headers['set-cookie']??[]){const pair=value.split(';')[0],i=pair.indexOf('=');cookies.set(pair.slice(0,i),pair.slice(i+1));}
      const chunks=[];response.on('data',c=>chunks.push(c));response.on('end',()=>{
        const buffer=Buffer.concat(chunks);let data=null;try{data=JSON.parse(buffer.toString());}catch{}
        accept({status:response.statusCode,headers:response.headers,bytes:buffer,data});
      });
    });
    req.setTimeout(15000,()=>req.destroy(new Error('HTTPS request timeout.')));
    req.on('error',reject);if(bytes)req.write(bytes);req.end();
  });
}
function checked(r,path) {
  if(![200,201,202,204,206].includes(r.status)) throw new Error('HTTP '+r.status+' '+(r.data?.code??'')+' at '+path);
  return r.data;
}
const get=async(path,options)=>checked(await call(path,options),path);
async function write(path,body,method='POST',key=randomUUID()) {
  const session=await get('/api/v1/session');
  return checked(await call('/api/v1/manage/'+path,{method,body,headers:{'X-CSRF-TOKEN':session.csrfToken,'Idempotency-Key':key}}),path);
}
async function machine(path,bearer,body,key) {return checked(await call('/api/v1/client/'+path,{method:'POST',body,bearer,headers:key?{'Idempotency-Key':key}:{}}),path);}
async function until(action,predicate,seconds=150) {
  const end=Date.now()+seconds*1000;
  while(Date.now()<end){try{const value=await action();if(predicate(value))return value;}catch{}await new Promise(r=>setTimeout(r,1000));}
  throw new Error('Bounded verification wait expired at '+phase);
}
function compose(...args) {
  return execFileSync('docker',['compose','--env-file',join(deployment,'deployment.env'),'--project-name',identity.project,
    '-f',join(deployment,'compose.yaml'),'--profile','full',...args],{encoding:'utf8',timeout:120000,stdio:['ignore','pipe','pipe']});
}
function complete(name){stages.push(name);console.log('Verified: '+name);}
try {
  let session, software, release, target, deploymentRecord;
  if(!existing) {
  await until(()=>call('/api/v1/session'),r=>r.status===200);
  assert.equal((await call('/api/v1/unrecognized-demo-route')).status,404);
  phase='personnel login and first password';session=await get('/api/v1/session');
  assert.equal((await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password:config.initialPassword}})).status,403);
  session=checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password:config.initialPassword},headers:{'X-CSRF-TOKEN':session.csrfToken}}),'login');
  assert.equal(session.mustChangePassword,true);
  session=checked(await call('/api/v1/session/password',{method:'POST',body:{currentPassword:config.initialPassword,newPassword:config.password},headers:{'X-CSRF-TOKEN':session.csrfToken}}),'password');
  assert.equal(session.mustChangePassword,false);complete(phase);
  phase='explicit fictional ledger and permissions';
  let user=await get('/api/v1/manage/users/'+session.subjectId);
  await write('subjects/'+user.id+'/permissions',{permissions:[...user.permissions,...['asset.read','asset.manage'].map(operation=>({softwareId:null,operation}))],expectedRevision:user.revision,reason:'显式虚构演示授权'},'PUT');
  const processRecord=await write('processes',{code:'DEMO-P',name:'虚构演示工序'});
  software=await write('software',{code:'DEMO-SW',name:'虚构演示视觉软件',category:'Vision'});
  const devices=[];
  for(const suffix of ['A','B']){const d=await write('devices',{processId:processRecord.id,deviceNo:'DEMO-D-'+suffix,name:'虚构验证设备'+suffix});await write('devices/'+d.id+'/software-bindings',{softwareId:software.id,reason:'显式演示映射'});devices.push(d);}
  user=await get('/api/v1/manage/users/'+session.subjectId);
  await write('subjects/'+user.id+'/permissions',{permissions:[...user.permissions,...['release.upload','release.publish','release.disable','enrollment.manage','deployment.create','deployment.control','task.closeUnknown','audit.read'].map(operation=>({softwareId:software.id,operation}))],expectedRevision:user.revision,reason:'显式演示职责'},'PUT');
  complete(phase);
  phase='two verified package copies';
  // A valid empty ZIP represents a simulated package; no factory executable is installed.
  const bytes=Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==','base64'), sha=createHash('sha256').update(bytes).digest('hex');
  const created=await write('software/'+software.id+'/releases',{changeLevel:'Patch',changeSummary:'虚构演示包',changeReason:'Linux 交付验证',expectedVersion:'1.0.0',package:{fileName:'demo-empty.zip',sizeBytes:bytes.length,sha256:sha}});
  const csrf=(await get('/api/v1/session')).csrfToken;
  checked(await call(created.uploadPath,{method:'PUT',body:bytes,headers:{'X-CSRF-TOKEN':csrf}}),'upload');
  release=await until(()=>get('/api/v1/manage/releases/'+created.release.id),r=>r.state==='Test');
  complete(phase);
  phase='isolated identities and installation evidence';const instances=[];
  for(const [index,device]of devices.entries()){
    const grantSecret=randomBytes(32).toString('base64url'),secret=randomBytes(32).toString('base64url');
    const grant=await write('enrollment-grants',{softwareId:software.id,deviceIds:[device.id],expiresAt:new Date(Date.now()+3600000).toISOString(),maxInstances:1,secretMaterial:grantSecret,reason:'显式演示登记'});
    const r=checked(await call('/api/v1/enrollment/instances',{method:'POST',body:{softwareId:software.id,deviceId:device.id,installationKey:randomUUID(),secretMaterial:secret},bearer:grant.id+'.'+grantSecret,headers:{'Idempotency-Key':randomUUID()}}),'enrollment');
    const bearer=r.credentialId+'.'+secret,stream=await machine('report-streams',bearer,{expectedEpoch:0},randomUUID());
    const report=(seq,installed=index===0)=>({streamEpoch:stream.streamEpoch,reportSeq:seq,reportedAt:now(),installationState:installed?'Installed':'NotInstalled',
      installedReleaseId:installed?release.id:null,installedVersion:installed?release.version:null,installedAt:null,runningState:'Stopped',reportedIps:['192.0.2.'+(50+index)],databaseState:{mode:'None',items:[]}});
    await machine('status-reports',bearer,report(1));instances.push({id:r.instanceId,bearer,report});
  }
  const evidence=(await get('/api/v1/manage/releases/'+release.id+'/test-evidence')).items[0];
  await write('releases/'+release.id+'/publish',{testEvidenceId:evidence.id,publishReason:'模拟安装证据验证',publishConclusion:'仅演示，不代表厂家现场安装验收',expectedRevision:release.revision});
  assert.equal((await get('/api/v1/client/versions',{bearer:instances[1].bearer})).items[0].id,release.id);complete(phase);
  phase='protected download HEAD Range and single-node availability';
  const download='/api/v1/packages/'+release.packageId+'/content';
  const head=await call(download,{method:'HEAD'});assert.equal(head.status,200);assert.equal(head.bytes.length,0);
  const ranged=await call(download,{headers:{Range:'bytes=4-19','If-Range':head.headers.etag}});assert.equal(ranged.status,206);assert.deepEqual(ranged.bytes,bytes.subarray(4,20));
  assert.equal(createHash('sha256').update((await call(download)).bytes).digest('hex'),sha);
  compose('stop','worker-b','api-b');
  assert.equal(createHash('sha256').update((await call(download)).bytes).digest('hex'),sha);
  compose('up','-d','api-b','worker-b');
  complete(phase);
  phase='explicit update and independent installation report';
  const selection=await write('target-selections',{softwareId:software.id,mode:'Explicit'});
  const chunk=await write('target-selections/'+selection.id+'/chunks/0',{instanceIds:[instances[1].id]},'PUT');
  const sealed=await write('target-selections/'+selection.id+'/seal',{expectedRevision:chunk.revision,chunkCount:1,expectedDistinctCount:1});
  compose('stop','worker-a','worker-b');
  deploymentRecord=await write('deployments',{softwareId:software.id,selectionId:sealed.id,kind:'Update',targetReleaseId:release.id,
    window:{notBefore:new Date(Date.now()-60000).toISOString(),latestStart:new Date(Date.now()+3600000).toISOString()},reason:'显式虚构更新演示'});
  compose('up','-d','worker-a','worker-b');
  const tasks=await until(()=>get('/api/v1/manage/deployments/'+deploymentRecord.id+'/tasks'),r=>r.items[0]?.state==='Available');
  const task=tasks.items[0],key=randomUUID();target=instances[1];
  const claim=await machine('tasks/'+task.id+'/claim',target.bearer,{},key);
  assert.equal((await machine('tasks/'+task.id+'/claim',target.bearer,{},key)).attemptId,claim.attemptId);
  await machine('attempts/'+claim.attemptId+'/start',target.bearer,{stateReport:target.report(2),preflight:{downloadedSha256:sha,dataProtectionConfirmed:true}},randomUUID());
  const terminal={eventId:randomUUID(),sequence:1,kind:'Terminal',result:'Succeeded'};
  const receipt=await machine('attempts/'+claim.attemptId+'/receipts',target.bearer,terminal);
  assert.equal((await machine('attempts/'+claim.attemptId+'/receipts',target.bearer,terminal)).receiptId,receipt.receiptId);
  assert.equal((await get('/api/v1/manage/instances/'+target.id)).lastSnapshot.installationState,'NotInstalled');
  await machine('status-reports',target.bearer,target.report(3,true));
  assert.equal((await get('/api/v1/manage/instances/'+target.id)).lastSnapshot.installedReleaseId,release.id);
  complete(phase);
  } else {
    phase='verify existing completed demo facts';
    session=await get('/api/v1/session');
    session=checked(await call('/api/v1/session',{method:'POST',body:{employeeNo:config.employeeNo,password:config.password},headers:{'X-CSRF-TOKEN':session.csrfToken}}),'existing demo login');
    software=(await get('/api/v1/manage/software')).items.find(x=>x.code==='DEMO-SW');assert.ok(software);
    release=(await get('/api/v1/manage/software/'+software.id+'/releases?channel=Formal')).items.find(x=>x.version==='1.0.0');assert.ok(release);
    deploymentRecord=(await get('/api/v1/manage/deployments?softwareId='+software.id)).items[0];assert.equal(deploymentRecord?.resultCounts.succeeded,1);
    target=(await get('/api/v1/manage/instances?softwareId='+software.id)).items.find(x=>x.deviceNo==='DEMO-D-B');assert.ok(target);
    assert.equal((await get('/api/v1/manage/instances/'+target.id)).lastSnapshot.installedReleaseId,release.id);complete(phase);
    // Deliberately restart the same node again to exercise service DNS changes after gateway repair.
    if(!browserOnly){compose('stop','worker-b','api-b');compose('up','-d','api-b','worker-b');}
  }
  phase=browserOnly?'shared session':'shared session and process broker restart';
  await until(()=>get('/api/v1/session',{url:config.secondaryUrl}),r=>r.subjectId===session.subjectId);
  if(!browserOnly)compose('restart','rabbitmq','worker-a','worker-b');
  await until(()=>get('/api/v1/manage/releases/'+release.id),r=>r.state==='Formal');
  assert.equal((await get('/api/v1/manage/instances/'+target.id)).lastSnapshot.installedReleaseId,release.id);complete(phase);
  phase='real browser renders current API facts';
  process.env.PLAYWRIGHT_BROWSERS_PATH ??= join(root,'.cache/playwright-browsers');
  const {chromium}=createRequire(join(root,'src/ui/svm-web/package.json'))('playwright');
  // Only these generated demo leaf keys are accepted by Chromium; there is no global TLS bypass.
  browser=await chromium.launch({headless:true,args:['--ignore-certificate-errors-spki-list='+config.browserSpkiPins.join(',')]});
  const page=await browser.newPage({viewport:{width:1440,height:1000}});
  await page.goto(config.url+'/login');await page.getByLabel('工号',{exact:true}).fill(config.employeeNo);await page.getByLabel('密码',{exact:true}).fill(config.password);
  await page.getByRole('button',{name:'登录',exact:true}).click();await page.getByRole('button',{name:'退出',exact:true}).waitFor();
  await page.goto(config.url+'/deployments?softwareId='+software.id+'&releaseId='+release.id);
  await page.getByRole('button',{name:deploymentRecord.id,exact:true}).click();await page.getByRole('heading',{name:/投放详情/}).waitFor();await page.screenshot({path:join(results,'linux-update.png'),fullPage:true});
  assert.deepEqual(await page.evaluate(()=>[localStorage.length,sessionStorage.length]),[0,0]);complete(phase);
  const backend=execFileSync('docker',['version','--format','{{.Server.Os}}/{{.Server.Arch}}'],{encoding:'utf8'}).trim();
  await writeFile(join(results,'result.json'),JSON.stringify({passed:true,stages,existingCompletedDemo:existing,verificationHost:process.platform+'/'+process.arch,backend,
    nativeLinuxX64:process.platform==='linux'&&process.arch==='x64'&&backend==='linux/amd64',releaseCommit:identity.releaseCommit,releaseDirty:identity.releaseDirty,
    simulatedInstallation:true,realGitLab:false,browserTrust:'generated-leaf-SPKI-pins; API uses CA and hostname validation'},null,2)+'\n');
} catch(error) {
  const page=browser?.contexts()[0]?.pages()[0];
  if(page){await page.screenshot({path:join(results,'browser-failure.png'),fullPage:true}).catch(()=>{});console.error('Browser visible text: '+(await page.locator('body').innerText()).slice(0,3000));}
  await writeFile(join(results,'result.json'),JSON.stringify({passed:false,phase,stages,errorType:error.name},null,2)+'\n');
  console.error('Linux demo verification failed at '+phase+': '+error.message);process.exitCode=1;
} finally {agent.destroy();await browser?.close();}
