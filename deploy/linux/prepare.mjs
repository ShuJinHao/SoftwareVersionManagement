import { randomUUID, randomBytes, createHash } from 'node:crypto';
import { readFile, writeFile, mkdir, cp, chmod, access } from 'node:fs/promises';
import { resolve, join, dirname } from 'node:path';
import { isIP } from 'node:net';
import { execFileSync } from 'node:child_process';
import { root } from '../../eng/toolchain.mjs';
import { renderNginx } from '../../eng/packages/nginx.mjs';

const args = process.argv.slice(2);
if (args.length !== 7 || args[0] !== '--demo' || args[1] !== '--settings' || args[3] !== '--release' || args[5] !== '--output')
  throw new Error('Usage: eng/node deploy/linux/prepare.mjs --demo --settings file --release folder --output new-folder');
const settings = JSON.parse(await readFile(resolve(args[2]), 'utf8'));
const release = resolve(args[4]), output = resolve(args[6]);
const packageExecution = {peerResponseSeconds:15,downloadAuthorizationSeconds:20,...settings.packageExecution};
if (!Number.isInteger(packageExecution.peerResponseSeconds) || packageExecution.peerResponseSeconds < 1 ||
    !Number.isInteger(packageExecution.downloadAuthorizationSeconds) || packageExecution.downloadAuthorizationSeconds >= 30 ||
    packageExecution.peerResponseSeconds >= packageExecution.downloadAuthorizationSeconds) throw new Error('Invalid package response/authorization budgets.');
if (!['full', 'preview'].includes(settings.mode) || !['linux/amd64', 'linux/arm64'].includes(settings.platform) ||
    !/^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$/.test(settings.publicHost) || !isIP(settings.publicBind) ||
    ![settings.httpsPort, settings.secondaryHttpsPort].every(p => Number.isInteger(p) && p >= 1024 && p <= 65535) ||
    settings.httpsPort === settings.secondaryHttpsPort || !settings.siteName?.includes('演示') ||
    !/^\d+\.\d+\.\d+\.\d+\/(?:2[4-9])$/.test(settings.networkSubnet) || !isIP(settings.gatewayAddress))
  throw new Error('Invalid explicit demo settings.');
const ipv4 = value => value.split('.').reduce((n, p) => (n * 256 + Number(p)) >>> 0, 0);
const [network, prefix] = settings.networkSubnet.split('/');
const mask = (0xffffffff << (32 - Number(prefix))) >>> 0;
if (isIP(network) !== 4 || isIP(settings.gatewayAddress) !== 4 ||
    ((ipv4(network) & mask) >>> 0) !== ipv4(network) ||
    ((ipv4(settings.gatewayAddress) & mask) >>> 0) !== ipv4(network) ||
    ipv4(settings.gatewayAddress) <= ipv4(network) || ipv4(settings.gatewayAddress) >= (ipv4(network) | (~mask >>> 0)) >>> 0)
  throw new Error('Gateway must be a usable address in the explicit IPv4 subnet.');
try { await access(output); throw new Error('Output exists; no secrets, identity or data are overwritten.'); }
catch (e) { if (e.code !== 'ENOENT') throw e; }
const releaseManifest = JSON.parse(await readFile(join(release, 'release.json'), 'utf8'));
for (const file of releaseManifest.files) {
  if (file.path.startsWith('/') || file.path.split(/[\\/]/).includes('..')) throw new Error('Invalid release manifest path.');
  const bytes = await readFile(join(release, file.path));
  if (createHash('sha256').update(bytes).digest('hex') !== file.sha256) throw new Error('Release content differs from its manifest.');
}
await mkdir(output, { recursive: true, mode: 0o700 });
const secret = () => randomBytes(32).toString('base64url');
const id = randomUUID(), project = 'svm-demo-' + id.replaceAll('-', '').slice(0,12);
const password = secret(), initialPassword = secret() + '!9aA', finalPassword = secret() + '!8bB';
const databaseSecrets = { bootstrap: secret(), migration: secret(), writer: secret(), reader: secret() };
const brokerSecret = secret(), brokerVhost = 'svm-' + id;
async function privateFile(path, value) {
  await mkdir(dirname(path), { recursive: true, mode: 0o700 });
  await writeFile(path, typeof value === 'string' ? value : JSON.stringify(value, null, 2) + '\n', { mode: 0o600 });
  await chmod(path, 0o600);
}
async function file(path, value) { await privateFile(join(output,path),value); }
const certDir = join(output,'private-certificates');
await mkdir(certDir,{mode:0o700});
const cert = name => join(certDir,name);
const openssl = args => {
  try { return execFileSync('openssl',args,{stdio:['ignore','pipe','pipe'],env:{...process.env,SVM_DEMO_CERT_PASSWORD:password}}); }
  catch { throw new Error('Demo certificate assembly failed; private diagnostics suppressed.'); }
};
openssl(['req','-x509','-newkey','rsa:2048','-nodes','-days','30','-sha256','-keyout',cert('root.key'),'-out',cert('root.pem'),
  '-subj','/CN=SVM explicit disposable demo CA','-addext','basicConstraints=critical,CA:TRUE','-addext','keyUsage=critical,keyCertSign,cRLSign']);
const certificates = {};
async function leaf(name, server, hosts = []) {
  openssl(['req','-new','-newkey','rsa:2048','-nodes','-keyout',cert(name+'.key'),'-out',cert(name+'.csr'),'-subj','/CN='+name]);
  const ext = 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage='+(server?'serverAuth':'clientAuth')+'\n'+
    (hosts.length ? 'subjectAltName='+hosts.map(h=>(isIP(h)?'IP:':'DNS:')+h).join(',')+'\n':'');
  await privateFile(cert(name+'.ext'),ext);
  openssl(['x509','-req','-in',cert(name+'.csr'),'-CA',cert('root.pem'),'-CAkey',cert('root.key'),'-set_serial','0x'+randomBytes(16).toString('hex'),
    '-days','30','-sha256','-extfile',cert(name+'.ext'),'-out',cert(name+'.pem')]);
  openssl(['pkcs12','-export','-in',cert(name+'.pem'),'-inkey',cert(name+'.key'),'-out',cert(name+'.pfx'),'-passout','env:SVM_DEMO_CERT_PASSWORD']);
  const der = openssl(['x509','-in',cert(name+'.pem'),'-outform','DER']);
  const pub = openssl(['x509','-in',cert(name+'.pem'),'-pubkey','-noout']);
  const publicKey = execFileSync('openssl',['pkey','-pubin','-outform','DER'],{input:pub,stdio:['pipe','pipe','pipe']});
  certificates[name]={sha256:createHash('sha256').update(der).digest('hex'),sha1:createHash('sha1').update(der).digest('hex').match(/../g).join(':').toUpperCase(),
    spki:createHash('sha256').update(publicKey).digest('base64')};
  for (const suffix of ['.key','.pfx','.pem']) await chmod(cert(name+suffix),0o600);
}
await leaf('protection',false);
for (const node of ['node-a','node-b']) {
  await leaf('server-'+node,true,[settings.publicHost,'127.0.0.1','gateway','api-'+node.at(-1)]);
  for (const role of ['peer','gateway','collector']) await leaf(role+'-'+node,false);
}
await leaf('rabbitmq',true,['rabbitmq']);
const uid = settings.serviceUid ?? process.getuid?.() ?? 10001, gid = settings.serviceGid ?? process.getgid?.() ?? 10001;
if (!Number.isInteger(uid) || uid < 1 || !Number.isInteger(gid) || gid < 1) throw new Error('Choose explicit non-root service UID/GID.');
const connection = (role,pass) => 'Host=postgres;Port=5432;Database=svm_demo;Username='+role+';Password='+pass+
  ';SSL Mode=Disable;Timeout=5;Command Timeout=10;Maximum Pool Size=20;Include Error Detail=false;Log Parameters=false;';
const persistence = { writerConnectionString:connection('svm_demo_app',databaseSecrets.writer),readerConnectionString:connection('svm_demo_read',databaseSecrets.reader) };
const personnel = path => ({applicationName:'svm/'+id,certificatePath:path+'/certs/protection.pfx',certificatePassword:password,
  policy:{sessionHours:8,minimumPasswordLength:15,maximumPasswordLength:128,hashIterations:600000,accountFailures:5,accountWindowSeconds:300,addressFailures:30,addressWindowSeconds:60}});
const messaging = { host:'rabbitmq',port:5671,virtualHost:brokerVhost,username:'svm_service',password:brokerSecret,siteId:id,useTls:true,tlsServerName:'rabbitmq',queryDelaySeconds:2 };
const peers = ['node-a','node-b'].flatMap(node => ['Peer','Gateway','Collector'].map(role=>({
  certificateSha256:certificates[role.toLowerCase()+'-'+node].sha256,nodeId:node,subjectId:randomUUID(),role })));
const nodes = ['node-a','node-b'].map(node=>({nodeId:node,internalBaseUri:'https://api-'+node.at(-1)+':7444/',serverCertificateSha256:certificates['server-'+node].sha256}));
for (const node of ['node-a','node-b']) {
  const directory = 'runtime/'+node;
  const copyCert = async name => { await mkdir(join(output,directory,'certs'),{recursive:true,mode:0o700}); await cp(cert(name),join(output,directory,'certs',name)); };
  for (const name of ['root.pem','protection.pfx','server-'+node+'.pfx','peer-'+node+'.pfx','collector-'+node+'.pfx']) await copyCert(name);
  await file(directory+'/persistence.json',persistence);await file(directory+'/personnel.json',personnel('/svm/config'));
  await file(directory+'/site.json',{siteId:id,siteName:settings.siteName,siteTimeZone:settings.timeZone});
  await file(directory+'/instances.json',{enrollmentMaxLifetimeSeconds:86400,enrollmentMaxCount:100,recoveryMaxLifetimeSeconds:1800});
  await file(directory+'/host.json',{publicCertificatePath:'/svm/config/certs/server-'+node+'.pfx',publicCertificatePassword:password,knownProxies:[settings.gatewayAddress],healthPort:8081});
  if (settings.mode==='full') {
    await file(directory+'/messaging.json',messaging); await file(directory+'/tasks.json',settings.task);
    await file(directory+'/package.json',{nodeId:node,rootPath:'/svm/packages',serviceSubjectId:randomUUID(),internalListenAddress:'0.0.0.0',internalListenPort:7444,
      serverCertificatePath:'/svm/config/certs/server-'+node+'.pfx',serverCertificatePassword:password,clientCertificatePath:'/svm/config/certs/peer-'+node+'.pfx',clientCertificatePassword:password,
      trustCertificatePath:'/svm/config/certs/root.pem',collectorCertificatePath:'/svm/config/certs/collector-'+node+'.pfx',collectorCertificatePassword:password,
      downloadLogDirectory:'/svm/logs',limits:settings.packageLimits,execution:packageExecution,nodes,peers});
  }
  await mkdir(join(output,'packages',node),{recursive:true,mode:0o700});await mkdir(join(output,'logs',node),{recursive:true,mode:0o700});
}
await file('init/migration.json',{connectionString:connection('svm_demo_migrate',databaseSecrets.migration),writerRole:'svm_demo_app',readerRole:'svm_demo_read',enableInboxWrites:settings.mode==='full'});
await file('init/personnel.json',personnel('/svm/init'));await mkdir(join(output,'init/certs'),{recursive:true,mode:0o700});await cp(cert('protection.pfx'),join(output,'init/certs/protection.pfx'));
await file('init/seed.json',{writerConnectionString:persistence.writerConnectionString,personnelConfigurationFile:'/svm/init/personnel.json',employeeNo:'DEMO-ADMIN',displayName:'虚构演示管理员',temporaryPassword:initialPassword});
const quote = value => "'" + value.replaceAll("'","''") + "'";
await mkdir(join(output,'postgres/data'),{recursive:true,mode:0o700});
await file('postgres/bootstrap-password',databaseSecrets.bootstrap);
await file('postgres/init.sql',Object.entries({svm_demo_migrate:databaseSecrets.migration,svm_demo_app:databaseSecrets.writer,svm_demo_read:databaseSecrets.reader})
  .map(([role,pass])=>'CREATE ROLE '+role+' LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS PASSWORD '+quote(pass)+';').join('\n')+
  '\nALTER ROLE svm_demo_read SET default_transaction_read_only=on;\nCREATE DATABASE svm_demo OWNER svm_demo_migrate;\nREVOKE ALL ON DATABASE svm_demo FROM PUBLIC;\nGRANT CONNECT ON DATABASE svm_demo TO svm_demo_migrate,svm_demo_app,svm_demo_read;\n\\connect svm_demo\nREVOKE CREATE ON SCHEMA public FROM PUBLIC;\n');
await mkdir(join(output,'rabbitmq/data'),{recursive:true,mode:0o700});await mkdir(join(output,'rabbitmq/config/certs'),{recursive:true,mode:0o700});
for (const name of ['root.pem','rabbitmq.pem','rabbitmq.key']) await cp(cert(name),join(output,'rabbitmq/config/certs',name));
const salt=randomBytes(4), passwordHash=Buffer.concat([salt,createHash('sha256').update(Buffer.concat([salt,Buffer.from(brokerSecret)])).digest()]).toString('base64');
await file('rabbitmq/config/definitions.json',{users:[{name:'svm_service',password_hash:passwordHash,hashing_algorithm:'rabbit_password_hashing_sha256',tags:''}],
  vhosts:[{name:brokerVhost,metadata:{default_queue_type:'quorum'}}],permissions:[{user:'svm_service',vhost:brokerVhost,configure:'.*',write:'.*',read:'.*'}]});
await file('rabbitmq/config/rabbitmq.conf','load_definitions = /etc/rabbitmq/definitions.json\nlisteners.tcp = none\nlisteners.ssl.default = 5671\nssl_options.cacertfile = /etc/rabbitmq/certs/root.pem\nssl_options.certfile = /etc/rabbitmq/certs/rabbitmq.pem\nssl_options.keyfile = /etc/rabbitmq/certs/rabbitmq.key\nssl_options.verify = verify_none\nssl_options.fail_if_no_peer_cert = false\n');
await mkdir(join(output,'gateway/certs'),{recursive:true,mode:0o700});
for (const name of ['root.pem',...['node-a','node-b'].flatMap(node=>['server-'+node+'.pem','server-'+node+'.key','gateway-'+node+'.pem','gateway-'+node+'.key'])])
  await cp(cert(name),join(output,'gateway/certs',name));
const nginx = {workerUser:null,resolverAddress:'127.0.0.11',certificateDirectory:'/svm/gateway/certs',gatewayFingerprints:['node-a','node-b'].map(n=>certificates['gateway-'+n].sha1),
  nodes:['node-a','node-b'].map((node,index)=>({nodeId:node,generation:randomUUID(),publicPort:10443+1000*index,privatePort:20443+1000*index,
    apiHost:'api-'+node.at(-1),apiTlsName:'api-'+node.at(-1),gatewayTlsName:'gateway',apiPublicPort:7443,apiInternalPort:7444,
    rootPath:'/svm/'+node+'/files',logDirectory:'/svm/'+node+'/logs',...settings.packageLimits,downloadAuthorizationSeconds:packageExecution.downloadAuthorizationSeconds}))};
await file('gateway/nginx.conf',renderNginx(nginx));
await cp(release,join(output,'image/release'),{recursive:true});
await cp(cert('root.pem'),join(output,'image/ca.crt'));await cp(join(root,'deploy/linux/Dockerfile'),join(output,'image/Dockerfile'));
for (const name of ['compose.yaml','compose.preview.yaml','svm.sh']) {await cp(join(root,'deploy/linux',name),join(output,name));await chmod(join(output,name),name.endsWith('.sh')?0o700:0o600);}
const images={};
for (const [name,manifest] of [['POSTGRES','postgres.local.json'],['RABBITMQ','rabbitmq.local.json'],['NGINX','nginx.packages.json']])
  images[name]=JSON.parse(await readFile(join(root,'build',manifest),'utf8')).images[settings.platform];
await file('deployment.env',Object.entries({SVM_PROJECT:project,SVM_CODE_COMMIT:releaseManifest.commit,SVM_RUNTIME_IMAGE:'svm/runtime:'+project,SVM_UID:uid,SVM_GID:gid,SVM_MODE:settings.mode,
  SVM_PLATFORM:settings.platform,SVM_POSTGRES_IMAGE:images.POSTGRES,SVM_RABBITMQ_IMAGE:images.RABBITMQ,SVM_NGINX_IMAGE:images.NGINX,
  SVM_PUBLIC_BIND:settings.publicBind,SVM_HTTPS_PORT:settings.httpsPort,SVM_SECONDARY_HTTPS_PORT:settings.secondaryHttpsPort,
  SVM_NETWORK_SUBNET:settings.networkSubnet,SVM_GATEWAY_ADDRESS:settings.gatewayAddress,
  SVM_PACKAGE_CONFIG_FILE:settings.mode==='full'?'/svm/config/package.json':'',SVM_MESSAGING_CONFIG_FILE:settings.mode==='full'?'/svm/config/messaging.json':'',
  SVM_TASK_CONFIG_FILE:settings.mode==='full'?'/svm/config/tasks.json':''}).map(([k,v])=>k+'='+v).join('\n')+'\n');
await file('.svm-demo',project+'\n');
await file('operator.json',{url:'https://'+settings.publicHost+':'+settings.httpsPort,secondaryUrl:'https://'+settings.publicHost+':'+settings.secondaryHttpsPort,
  employeeNo:'DEMO-ADMIN',initialPassword,password:finalPassword,caFile:'private-certificates/root.pem',browserSpkiPins:['node-a','node-b'].map(n=>certificates['server-'+n].spki)});
await file('deployment.json',{format:1,demo:true,deploymentId:id,project,platform:settings.platform,mode:settings.mode,releaseCommit:releaseManifest.commit,releaseDirty:releaseManifest.dirty,
  resourceDirectories:['postgres','rabbitmq','packages','logs'],certificateExpiresWithinDays:30,serviceUid:uid,serviceGid:gid});
console.log('Prepared explicitly fictional demo '+project+'; private credentials stay in '+join(output,'operator.json'));
