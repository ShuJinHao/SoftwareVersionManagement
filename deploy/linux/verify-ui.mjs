import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, join, relative } from 'node:path';
import { createRequire } from 'node:module';
import { request as https, Agent } from 'node:https';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
import { root } from '../../eng/toolchain.mjs';

const args = process.argv.slice(2);
const resumeFreeze = args.at(-1) === '--resume-freeze';
const resumeViewport = args.at(-1) === '--resume-viewport';
const resumeBrowser = resumeFreeze || resumeViewport || args.at(-1) === '--resume-browser'; if (resumeBrowser) args.pop();
if (args.length !== 4 || args[0] !== '--deployment' || args[2] !== '--results')
  throw new Error('Usage: eng/node deploy/linux/verify-ui.mjs --deployment folder --results folder [--resume-browser|--resume-freeze|--resume-viewport]');
const deployment = resolve(args[1]), results = resolve(args[3]);
const config = JSON.parse(await readFile(join(deployment, 'operator.json'), 'utf8'));
const identity = JSON.parse(await readFile(join(deployment, 'deployment.json'), 'utf8'));
if (!identity.demo || identity.mode !== 'full' || !/^svm-demo-[a-z0-9]+$/.test(identity.project))
  throw new Error('Only an explicitly disposable full demo may be verified.');
const resultPath = relative(resolve(root, 'artifacts/test-results'), results);
if (!resultPath || resultPath === '..' || resultPath.startsWith('../') || resultPath.startsWith('..\\'))
  throw new Error('Results must stay under this project artifacts/test-results.');
if (new URL(config.url).protocol !== 'https:' || !Array.isArray(config.browserSpkiPins) || config.browserSpkiPins.length !== 2)
  throw new Error('The demo HTTPS identity is incomplete.');
await mkdir(results, { recursive: true });
const agent = new Agent({ ca: await readFile(resolve(deployment, config.caFile)), keepAlive: true });
const cookies = new Map(), stages = [], injections = [], screenshots = [], diagnostics = [];
let phase = 'startup', browser, page, workersStopped = false;
const fixtureFile = join(deployment, 'ui-fixture.json');
let fixture = { format: 1, project: identity.project, releaseCommit: identity.releaseCommit, prepared: false, resumeBrowserSafe: true };
async function saveFixture() { await writeFile(fixtureFile, JSON.stringify(fixture, null, 2) + '\n', { mode: 0o600 }); }
const now = () => new Date().toISOString();
const delay = ms => new Promise(done => setTimeout(done, ms));
const digest = value => createHash('sha256').update(value).digest('hex');
async function call(path, { method = 'GET', body, bearer, headers = {}, cookie = true } = {}) {
  const bytes = body === undefined ? null : Buffer.isBuffer(body) ? body : Buffer.from(JSON.stringify(body));
  return await new Promise((accept, reject) => {
    const req = https(new URL(path, config.url), { agent, method, headers: {
      ...(bearer ? { Authorization: 'Bearer ' + bearer } : cookie ? { Cookie: [...cookies].map(([k, v]) => k + '=' + v).join('; ') } : {}),
      ...(bytes ? { 'Content-Type': Buffer.isBuffer(body) ? 'application/octet-stream' : 'application/json', 'Content-Length': bytes.length } : {}), ...headers,
    } }, response => {
      if (cookie && !bearer) for (const value of response.headers['set-cookie'] ?? []) {
        const pair = value.split(';')[0], i = pair.indexOf('='); cookies.set(pair.slice(0, i), pair.slice(i + 1));
      }
      const chunks = []; let size = 0;
      response.on('data', chunk => { size += chunk.length; if (size > 4 * 1024 * 1024) req.destroy(new Error('Bounded response limit exceeded.')); else chunks.push(chunk); });
      response.on('error', reject);
      response.on('end', () => {
        const bytes = Buffer.concat(chunks);
        let data = null; try { data = JSON.parse(bytes.toString()); } catch { /* No response body is logged. */ }
        accept({ status: response.statusCode, headers: response.headers, bytes, data });
      });
    });
    const budget = setTimeout(() => req.destroy(new Error('HTTPS request deadline exceeded.')), 15000);
    req.once('close', () => clearTimeout(budget)); req.on('error', reject);
    if (bytes) req.write(bytes); req.end();
  });
}
// Playwright's APIRequestContext does not inherit Chromium's leaf pins. The route proxy uses
// this exact demo CA and hostname validation while retaining the browser's own session/CSRF.
async function forward(route, changedUrl) {
  const request = route.request(), url = new URL(changedUrl ?? request.url());
  assert.equal(url.origin, new URL(config.url).origin);
  return await call(url.pathname + url.search, { method: request.method(), body: request.postDataBuffer() ?? undefined,
    headers: { ...await request.allHeaders(), 'accept-encoding': 'identity' }, cookie: false });
}
function fulfill(response) {
  return { status: response.status, body: response.bytes,
    headers: Object.fromEntries(Object.entries(response.headers).filter(([, value]) => value !== undefined)
      .map(([key, value]) => [key, Array.isArray(value) ? value.join('\n') : String(value)])) };
}
function checked(response, path) {
  if (![200, 201, 202, 204].includes(response.status))
    throw new Error('HTTP ' + response.status + ' ' + (response.data?.code ?? '') + ' at ' + path.split('?')[0]);
  return response.data;
}
const get = async path => checked(await call(path), path);
async function write(path, body, method = 'POST') {
  const session = await get('/api/v1/session');
  return checked(await call('/api/v1/manage/' + path, { method, body,
    headers: { 'X-CSRF-TOKEN': session.csrfToken, 'Idempotency-Key': randomUUID() } }), path);
}
const machine = async (path, bearer, body, key) => checked(await call('/api/v1/client/' + path, {
  method: 'POST', bearer, body, headers: key ? { 'Idempotency-Key': key } : {},
}), path);
async function until(action, predicate, seconds = 90) {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    const value = await action(); if (predicate(value)) return value; await delay(300);
  }
  throw new Error('Bounded verification wait expired.');
}
function compose(...args) {
  execFileSync('docker', ['compose', '--env-file', join(deployment, 'deployment.env'), '--project-name', identity.project,
    '-f', join(deployment, 'compose.yaml'), '--profile', 'full', ...args],
  { encoding: 'utf8', timeout: 60000, stdio: ['ignore', 'pipe', 'pipe'] });
}
function complete(name) { stages.push(name); console.log('Verified UI: ' + name); }
const permissions = ['release.upload', 'release.publish', 'enrollment.manage', 'deployment.create', 'deployment.control', 'task.closeUnknown'];
async function grantPermissions(subject, additions) {
  const user = await get('/api/v1/manage/users/' + subject);
  const existing = new Set(user.permissions.map(p => (p.softwareId ?? '') + ':' + p.operation));
  await write('subjects/' + subject + '/permissions', { permissions: [...user.permissions, ...additions.filter(p => !existing.has((p.softwareId ?? '') + ':' + p.operation))],
    expectedRevision: user.revision, reason: '明确虚构的界面正确性夹具授权' }, 'PUT');
}
try {
  let saved;
  try { saved = JSON.parse(await readFile(fixtureFile, 'utf8')); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (saved) fixture = saved;
  if (resumeBrowser) {
    const validIdentity = saved?.prepared && saved.format === 1 && saved.project === identity.project && saved.releaseCommit === identity.releaseCommit;
    const safeFreeze = resumeFreeze && (saved?.resumeCheckpoint === 'freeze' || saved?.lastFailurePhase === 'frozen filter draft keeps its version while materials change') && !saved.freezeRequestStarted && !saved.pauseOperation;
    const safeViewport = resumeViewport && (saved?.resumeCheckpoint === 'viewport' || saved?.lastFailurePhase === 'responsive overflow and equivalent 200 percent viewport') && saved.pauseOperation?.verificationComplete === true;
    if (!validIdentity || !(resumeViewport ? safeViewport : resumeFreeze ? safeFreeze : saved.resumeBrowserSafe))
      throw new Error('Resume requires a matching safe checkpoint; a possibly submitted operation is never recreated.');
    fixture = saved;
    if (resumeFreeze) { fixture.resumeCheckpoint = 'freeze'; await saveFixture(); }
    if (resumeViewport) { fixture.resumeCheckpoint = 'viewport'; await saveFixture(); }
  } else {
    if (saved) throw new Error('This demo already has a UI verification checkpoint; use a safe browser resume or inspect the existing facts.');
    await saveFixture();
  }
  await until(() => call('/api/v1/session'), r => r.status === 200, 30);
  let software, releases, instances, primary, secondary, initialTasks;
  if (resumeBrowser) {
    phase = 'resume existing prepared fixture without recreating business';
    const session = await get('/api/v1/session');
    checked(await call('/api/v1/session', { method: 'POST', body: { employeeNo: config.employeeNo, password: config.password },
      headers: { 'X-CSRF-TOKEN': session.csrfToken } }), 'resume fixture login');
    ({ software, releases, instances, primary, secondary, initialTasks } = fixture);
    assert.equal((await get('/api/v1/manage/deployments/' + primary.id)).targetReleaseId, releases[0].id);
    assert.equal((await get('/api/v1/manage/deployments/' + secondary.id)).targetReleaseId, releases[1].id);
    if (resumeFreeze) {
      const tasks = await Promise.all(initialTasks.map(t => get('/api/v1/manage/tasks/' + t.id)));
      assert.ok(tasks.every(t => !t.isDeferred));
      assert.ok(tasks.slice(0, 2).every(t => t.state === 'AwaitingResult'));
      assert.equal(tasks[2].state, 'Available');
    }
    if (resumeViewport) {
      const tasks = await Promise.all(initialTasks.map(t => get('/api/v1/manage/tasks/' + t.id)));
      assert.ok(tasks.slice(0, 2).every(t => t.state === 'ClosedUnknown' && t.manualClosure !== null));
      assert.equal(tasks[2].state, 'Available');
    }
    complete(phase);
  } else {
  phase = 'dedicated demo session and fictional fixtures';
  let session = await get('/api/v1/session');
  session = checked(await call('/api/v1/session', { method: 'POST', body: { employeeNo: config.employeeNo, password: config.initialPassword },
    headers: { 'X-CSRF-TOKEN': session.csrfToken } }), 'demo login');
  assert.equal(session.mustChangePassword, true);
  session = checked(await call('/api/v1/session/password', { method: 'POST', body: { currentPassword: config.initialPassword, newPassword: config.password },
    headers: { 'X-CSRF-TOKEN': session.csrfToken } }), 'demo password');
  await grantPermissions(session.subjectId, ['asset.read', 'asset.manage'].map(operation => ({ softwareId: null, operation })));
  const suffix = identity.deploymentId.replaceAll('-', '').slice(0, 8);
  const processRecord = await write('processes', { code: 'UI-P-' + suffix, name: '明确虚构的界面验证工序' });
  software = await write('software', { code: 'UI-S-' + suffix, name: '明确虚构的界面验证视觉软件', category: 'Vision' });
  await grantPermissions(session.subjectId, permissions.map(operation => ({ softwareId: software.id, operation })));
  const caps = await get('/api/v1/manage/software/' + software.id + '/deployment-capabilities');
  assert.ok(caps.batchSize >= 3); assert.equal(caps.resultWaitSeconds, 1);
  instances = [];
  for (let index = 0; index < 3; index++) {
    const device = await write('devices', { processId: processRecord.id, deviceNo: 'UI-D-' + suffix + '-' + index,
      name: '明确虚构的超长中文设备名称用于验证窄屏及键盘操作' + '设备名称内容'.repeat(8) + index });
    await write('devices/' + device.id + '/software-bindings', { softwareId: software.id, reason: '明确虚构映射' });
    const grantSecret = randomBytes(32).toString('base64url'), secret = randomBytes(32).toString('base64url');
    const grant = await write('enrollment-grants', { softwareId: software.id, deviceIds: [device.id],
      expiresAt: new Date(Date.now() + 3600000).toISOString(), maxInstances: 1, secretMaterial: grantSecret, reason: '明确虚构登记' });
    const registered = checked(await call('/api/v1/enrollment/instances', { method: 'POST', bearer: grant.id + '.' + grantSecret,
      body: { softwareId: software.id, deviceId: device.id, installationKey: randomUUID(), secretMaterial: secret },
      headers: { 'Idempotency-Key': randomUUID() } }), 'fixture enrollment');
    const bearer = registered.credentialId + '.' + secret;
    const stream = await machine('report-streams', bearer, { expectedEpoch: 0 }, randomUUID());
    const instance = { id: registered.instanceId, bearer, device, streamEpoch: stream.streamEpoch, sequence: 0 };
    instance.report = release => ({ streamEpoch: instance.streamEpoch, reportSeq: ++instance.sequence, reportedAt: now(),
      installationState: release ? 'Installed' : 'NotInstalled', installedReleaseId: release?.id ?? null, installedVersion: release?.version ?? null,
      installedAt: null, runningState: 'Stopped', reportedIps: ['192.0.2.' + (100 + index)], databaseState: { mode: 'None', items: [] } });
    await machine('status-reports', bearer, instance.report(null)); instances.push(instance);
  }
  releases = [];
  for (let index = 0; index < 2; index++) {
    const bytes = Buffer.concat([Buffer.from('UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA==', 'base64'), Buffer.alloc(index)]);
    if (index) bytes[20] = index; // Two valid tiny ZIPs; no executable is installed by this fixture.
    const created = await write('software/' + software.id + '/releases', { changeLevel: 'Patch', changeSummary: '明确虚构界面验证包',
      changeReason: '仅准备界面夹具，不代表安装验收', expectedVersion: '1.0.' + index,
      package: { fileName: 'ui-fixture-' + index + '.zip', sizeBytes: bytes.length, sha256: digest(bytes) } });
    checked(await call(created.uploadPath, { method: 'PUT', body: bytes, headers: { 'X-CSRF-TOKEN': (await get('/api/v1/session')).csrfToken } }), 'fixture package upload');
    const release = await until(() => get('/api/v1/manage/releases/' + created.release.id), r => r.state === 'Test');
    await machine('status-reports', instances[0].bearer, instances[0].report(release));
    const proof = (await get('/api/v1/manage/releases/' + release.id + '/test-evidence')).items[0];
    await write('releases/' + release.id + '/publish', { testEvidenceId: proof.id, publishReason: '明确虚构的界面夹具',
      publishConclusion: '模拟上报，不代表厂家安装或数据保护验收', expectedRevision: release.revision });
    releases.push({ ...release, sha256: digest(bytes) });
  }
  await machine('status-reports', instances[0].bearer, instances[0].report(null));
  async function createDeployment(release) {
    const selection = await write('target-selections', { softwareId: software.id, mode: 'Explicit' });
    const chunk = await write('target-selections/' + selection.id + '/chunks/0', { instanceIds: instances.map(i => i.id) }, 'PUT');
    const sealed = await write('target-selections/' + selection.id + '/seal', { expectedRevision: chunk.revision, chunkCount: 1, expectedDistinctCount: 3 });
    return await write('deployments', { softwareId: software.id, selectionId: sealed.id, kind: 'Update', targetReleaseId: release.id,
      window: { notBefore: new Date(Date.now() - 60000).toISOString(), latestStart: new Date(Date.now() + 3600000).toISOString() }, reason: '明确虚构界面验证投放' });
  }
  primary = await createDeployment(releases[0]);
  initialTasks = (await until(() => get('/api/v1/manage/deployments/' + primary.id + '/tasks'),
    p => p.items.length === 3 && p.items.every(t => t.state === 'Available'))).items.sort((a, b) => a.id.localeCompare(b.id));
  const claims = await Promise.all(initialTasks.slice(0, 2).map(async task => {
    const instance = instances.find(i => i.id === task.instanceId);
    const claim = await machine('tasks/' + task.id + '/claim', instance.bearer, {}, randomUUID());
    return { task, instance, claim };
  }));
  await Promise.all(claims.map(({ instance, claim }) => machine('attempts/' + claim.attemptId + '/start', instance.bearer,
    { stateReport: instance.report(null), preflight: { downloadedSha256: releases[0].sha256, dataProtectionConfirmed: true } }, randomUUID())));
  for (const { instance, claim } of claims) {
    for (let sequence = 1; sequence <= 3; sequence++) await machine('attempts/' + claim.attemptId + '/receipts', instance.bearer,
      { eventId: randomUUID(), sequence, kind: 'Progress', progress: sequence === 1 ? 'Downloading' : 'ReadyToInstall', detail: '明确虚构分页回执 ' + sequence });
  }
  secondary = await createDeployment(releases[1]);
  await until(() => get('/api/v1/manage/deployments/' + secondary.id), d => d.state === 'Rejected');
  await until(() => get('/api/v1/manage/tasks/' + initialTasks[0].id), t => t.state === 'AwaitingResult' && Date.parse(t.responseDeadlineAt) < Date.now());
  fixture = { ...fixture, prepared: true, resumeBrowserSafe: true, software, releases, primary, secondary, initialTasks,
    instances: instances.map(i => ({ id: i.id, device: i.device })) };
  await saveFixture();
  complete(phase);
  }

  process.env.PLAYWRIGHT_BROWSERS_PATH ??= join(root, '.cache/playwright-browsers');
  const require = createRequire(join(root, 'src/ui/svm-web/package.json'));
  const { chromium } = require('playwright'), { expect } = require('@playwright/test');
  browser = await chromium.launch({ headless: true, args: ['--ignore-certificate-errors-spki-list=' + config.browserSpkiPins.join(',')] });
  const context = await browser.newContext({ viewport: { width: 1366, height: 768 }, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' });
  page = await context.newPage(); page.setDefaultTimeout(15000); const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.name));
  await page.goto(config.url + '/login'); await page.getByLabel('工号', { exact: true }).fill(config.employeeNo);
  await page.getByLabel('密码', { exact: true }).fill(config.password); await page.getByRole('button', { name: '登录', exact: true }).click();
  await page.getByRole('button', { name: '退出', exact: true }).waitFor();
  const location = config.url + '/deployments?softwareId=' + software.id + '&releaseId=' + releases[0].id;
  let polls = 0;
  page.on('response', response => { if (response.request().method() === 'GET' && new URL(response.url()).pathname === '/api/v1/manage/deployments/' + primary.id) polls++; });
  await page.route(url => /\/deployments\/[^/]+\/tasks$|\/tasks\/[^/]+\/receipts$/.test(url.pathname), async route => {
    const url = new URL(route.request().url()); url.searchParams.set('pageSize', '1');
    const response = await forward(route, url.href); await route.fulfill(fulfill(response));
  });
  injections.push({ kind: 'bounded-pagination', pageSize: 1, source: 'real HTTPS API and real protected cursors' });
  await page.goto(location); await page.getByRole('button', { name: primary.id, exact: true }).waitFor();
  const taskRow = id => page.locator('tr').filter({ hasText: id });
  async function allTaskPages() {
    await expect(page.getByRole('button', { name: '刷新当前投放', exact: true })).toBeEnabled();
    for (let count = 0; count < 3; count++) {
      const more = page.getByRole('button', { name: '更多任务', exact: true });
      if (!await more.isVisible()) break; await more.click();
      await expect.poll(() => page.locator('tr').filter({ hasText: /尚无终态|Unknown|结果未知/ }).count()).toBeGreaterThan(count + 1);
    }
    for (const task of initialTasks) await expect(taskRow(task.id)).toHaveCount(1);
  }
  const selectField = label => page.getByRole('combobox', { name: label, exact: true });
  if (!resumeFreeze && !resumeViewport) {
  phase = 'late detail response cannot replace another selection';
  let releaseHeld, sawHeld, deliveredOld;
  const gate = new Promise(done => { releaseHeld = done; }), held = new Promise(done => { sawHeld = done; }), oldDelivered = new Promise(done => { deliveredOld = done; });
  const delayed = config.url + '/api/v1/manage/deployments/' + primary.id;
  await page.route(delayed, async route => {
    const response = await forward(route); sawHeld(); await gate;
    await route.fulfill(fulfill(response)).catch(() => {}); deliveredOld(); // The old reader may already have been canceled by the page.
  }, { times: 1 });
  await page.getByRole('button', { name: primary.id, exact: true }).click();
  await Promise.race([held, delay(15000).then(() => { throw new Error('The delayed real detail response was not captured.'); })]);
  await page.getByRole('button', { name: secondary.id, exact: true }).click();
  await expect(page.getByText('固定目标 ' + releases[1].id, { exact: false })).toBeVisible(); releaseHeld();
  await Promise.race([oldDelivered, delay(15000).then(() => { throw new Error('The old detail response did not finish.'); })]); await page.unroute(delayed);
  await expect(page.getByRole('heading', { name: '投放详情 · 无合格目标', exact: true })).toBeVisible();
  for (const task of initialTasks) await expect(taskRow(task.id)).toHaveCount(0);
  injections.push({ kind: 'delayed-old-detail', capturedRealResponse: true, changedSelectionBeforeDelivery: true }); complete(phase);

  phase = 'three task pages and second-page receipts survive two summary polls';
  await page.getByRole('button', { name: primary.id, exact: true }).click(); await page.getByRole('heading', { name: /投放详情/ }).waitFor();
  await page.getByRole('button', { name: '更多任务', exact: true }).click(); await expect(taskRow(initialTasks[1].id)).toHaveCount(1);
  await taskRow(initialTasks[1].id).getByRole('button', { name: '回执', exact: true }).click();
  await expect(page.getByRole('heading', { name: '任务回执 · ' + initialTasks[1].id, exact: true })).toBeVisible();
  const receiptParagraphs = page.locator('p').filter({ hasText: /^序号 \d+ ·/ });
  await expect(receiptParagraphs).toHaveCount(1);
  for (let count = 0; count < 2; count++) { await page.getByRole('button', { name: '更多回执', exact: true }).click(); await expect(receiptParagraphs).toHaveCount(count + 2); }
  await page.getByRole('button', { name: '更多任务', exact: true }).click(); await expect(taskRow(initialTasks[2].id)).toHaveCount(1);
  const beforePolls = polls; await expect.poll(() => polls, { timeout: 20000 }).toBeGreaterThanOrEqual(beforePolls + 2);
  for (const task of initialTasks) await expect(taskRow(task.id)).toHaveCount(1);
  await expect(page.getByRole('heading', { name: '任务回执 · ' + initialTasks[1].id, exact: true })).toBeVisible();
  for (const sequence of [1, 2, 3]) await expect(page.getByText('序号 ' + sequence, { exact: false })).toBeVisible(); complete(phase);

  phase = 'defer and restore remain bound to the open batch';
  fixture.resumeBrowserSafe = false; fixture.mutatingBrowserStage = phase; await saveFixture();
  await page.getByLabel('控制原因', { exact: true }).fill('明确虚构的暂缓和恢复');
  await taskRow(initialTasks[2].id).getByRole('button', { name: '暂缓', exact: true }).click();
  await until(() => get('/api/v1/manage/tasks/' + initialTasks[2].id), t => t.isDeferred);
  await allTaskPages(); await expect(taskRow(initialTasks[2].id).getByRole('button', { name: '恢复任务', exact: true })).toBeEnabled();
  await taskRow(initialTasks[2].id).getByRole('button', { name: '恢复任务', exact: true }).click();
  await until(() => get('/api/v1/manage/tasks/' + initialTasks[2].id), t => !t.isDeferred); complete(phase);
  }

  if (!resumeViewport) {
  phase = 'frozen filter draft keeps its version while materials change';
  fixture.resumeCheckpoint = 'freeze'; await saveFixture();
  compose('stop', 'worker-a', 'worker-b'); workersStopped = true;
  await page.getByRole('button', { name: '选择更新目标', exact: true }).click();
  await selectField('正式目标版本').selectOption(releases[0].id);
  await selectField('目标方式').selectOption('Filter');
  await page.getByRole('checkbox', { name: '使用配置的下一时段', exact: true }).uncheck();
  await page.getByLabel('UTC 开始', { exact: true }).fill(new Date(Date.now() - 60000).toISOString().slice(0, 16));
  await page.getByLabel('UTC 最晚开始', { exact: true }).fill(new Date(Date.now() + 3600000).toISOString().slice(0, 16));
  await page.getByLabel('投放原因', { exact: true }).fill('明确虚构的冻结草稿');
  const selectionResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/manage/target-selections' && r.request().method() === 'POST');
  fixture.freezeRequestStarted = true; await saveFixture();
  await page.getByRole('button', { name: '封存目标并创建更新投放', exact: true }).click();
  const frozenSelection = await (await selectionResponse).json(); assert.equal(frozenSelection.state, 'Building');
  fixture.frozenSelectionId = frozenSelection.id; await saveFixture();
  await expect(selectField('正式目标版本')).toBeDisabled();
  await selectField('版本').selectOption(releases[1].id);
  await page.getByRole('button', { name: '查询资料历史', exact: true }).click();
  compose('up', '-d', 'worker-a', 'worker-b'); workersStopped = false;
  await until(() => get('/api/v1/manage/target-selections/' + frozenSelection.id), s => s.state === 'Sealed');
  const createdResponse = page.waitForResponse(r => new URL(r.url()).pathname === '/api/v1/manage/deployments' && r.request().method() === 'POST');
  await page.getByRole('button', { name: '刷新封存结果并继续', exact: true }).click();
  const frozenDeployment = await (await createdResponse).json(); assert.equal(frozenDeployment.targetReleaseId, releases[0].id);
  injections.push({ kind: 'owned-workers-paused-for-building-snapshot', recoveryAcceptance: false }); complete(phase);

  phase = 'committed response loss retains one operation until explicit verification';
  await page.getByRole('button', { name: primary.id, exact: true }).click(); await page.getByRole('heading', { name: /投放详情/ }).waitFor();
  await page.getByLabel('控制原因', { exact: true }).fill('明确虚构的响应丢失核实');
  const attempts = []; let committed;
  const pausePath = config.url + '/api/v1/manage/deployments/' + primary.id + '/pause';
  await page.route(pausePath, async route => {
    attempts.push({ key: route.request().headers()['idempotency-key'], body: route.request().postData() });
    if (attempts.length === 1) { fixture.pauseOperation = { path: new URL(pausePath).pathname, ...attempts[0], verificationComplete: false }; await saveFixture(); }
    const response = await forward(route); assert.equal(response.status, 200); committed = response.data;
    if (attempts.length === 1) await route.abort('failed'); else await route.fulfill(fulfill(response));
  });
  await page.getByRole('button', { name: '暂停投放', exact: true }).click();
  await page.getByRole('button', { name: '核实原操作', exact: true }).waitFor(); assert.equal(attempts.length, 1);
  await expect(page.getByText(attempts[0].key, { exact: true })).toBeVisible();
  await delay(500); assert.equal(attempts.length, 1); assert.ok(committed.pauseReasons.some(r => r.code === 'ManualPause'));
  await page.getByRole('button', { name: '核实原操作', exact: true }).click();
  await expect(page.getByRole('button', { name: '核实原操作', exact: true })).toHaveCount(0);
  assert.equal(attempts.length, 2); assert.deepEqual(attempts[0], attempts[1]); await page.unroute(pausePath);
  fixture.pauseOperation.verificationComplete = true; await saveFixture();
  injections.push({ kind: 'committed-pause-response-lost', sameKeyAndBodyOnManualVerification: true,
    automaticResends: 0, manualVerifications: 1, bodySha256: digest(attempts[0].body) }); complete(phase);

  phase = 'each unknown closure requires its own evidence and keyboard confirmation';
  await allTaskPages();
  for (const [index, task] of initialTasks.slice(0, 2).entries()) {
    await taskRow(task.id).getByRole('button', { name: '人工关闭未知', exact: true }).click();
    const dialog = page.getByRole('dialog'); await expect(dialog).toHaveCount(1);
    await expect(dialog).toContainText(task.id); await expect(dialog.getByLabel('关闭原因', { exact: true })).toHaveValue('');
    await expect(dialog.getByLabel('本任务现场核实材料', { exact: true })).toHaveValue('');
    await expect(dialog.getByRole('checkbox', { name: '已现场核实本任务没有正在安装', exact: true })).not.toBeChecked();
    await expect(dialog.getByRole('button', { name: '确认关闭本任务跟踪', exact: true })).toBeDisabled();
    await dialog.getByLabel('关闭原因', { exact: true }).focus(); await page.keyboard.press('Tab');
    await expect(dialog.getByLabel('本任务现场核实材料', { exact: true })).toBeFocused();
    await dialog.getByLabel('关闭原因', { exact: true }).fill('明确虚构关闭原因 ' + index);
    await dialog.getByLabel('本任务现场核实材料', { exact: true }).fill('明确虚构：逐项核实任务 ' + task.id + ' 没有现场安装进程；仅验证平台界面。');
    await dialog.getByRole('checkbox', { name: '已现场核实本任务没有正在安装', exact: true }).check();
    await dialog.getByRole('button', { name: '确认关闭本任务跟踪', exact: true }).click();
    await until(() => get('/api/v1/manage/tasks/' + task.id), t => t.state === 'ClosedUnknown');
    await expect(page.getByRole('dialog')).toHaveCount(0); await allTaskPages();
  }
  complete(phase);
  }

  phase = 'responsive overflow and equivalent 200 percent viewport';
  fixture.resumeCheckpoint = 'viewport'; await saveFixture();
  if (resumeViewport) {
    await page.getByRole('button', { name: primary.id, exact: true }).click();
    await page.getByRole('heading', { name: /投放详情/ }).waitFor(); await allTaskPages();
  }
  await page.getByRole('button', { name: '选择更新目标', exact: true }).click();
  async function inspectViewport(target, name, width, height) {
    await target.setViewportSize({ width, height });
    const geometry = await target.evaluate(() => ({ width: innerWidth, height: innerHeight, dpr: devicePixelRatio,
      documentWidth: document.documentElement.scrollWidth, viewportWidth: document.documentElement.clientWidth }));
    assert.ok(geometry.documentWidth <= geometry.viewportWidth + 1, 'The full page must not overflow horizontally.');
    await target.screenshot({ path: join(results, name + '.png'), fullPage: true }); screenshots.push({ name, ...geometry });
  }
  for (const [width, height] of [[1280, 720], [1366, 768], [1920, 1080]]) await inspectViewport(page, 'ui-' + width + 'x' + height, width, height);
  const zoomContext = await browser.newContext({ viewport: { width: 683, height: 384 }, deviceScaleFactor: 2, locale: 'zh-CN', timezoneId: 'Asia/Shanghai' });
  await zoomContext.addCookies(await context.cookies()); const zoomPage = await zoomContext.newPage(); zoomPage.setDefaultTimeout(15000);
  await zoomPage.goto(location); await zoomPage.getByRole('button', { name: primary.id, exact: true }).click();
  await zoomPage.getByRole('heading', { name: /投放详情/ }).waitFor(); await zoomPage.getByRole('button', { name: '选择更新目标', exact: true }).click();
  await inspectViewport(zoomPage, 'ui-200-percent-equivalent', 683, 384);
  const scrollRegion = zoomPage.getByRole('region', { name: '可横向滚动的数据表格', exact: true }).filter({ has: zoomPage.getByRole('checkbox', { name: '选择实例 ' + instances[0].id, exact: true }) });
  diagnostics.push({ check: 'zoom-scroll-region-count', count: await scrollRegion.count() });
  await expect(scrollRegion).toHaveCount(1); await scrollRegion.focus(); await expect(scrollRegion).toBeFocused();
  diagnostics.push({ check: 'zoom-scroll-region-before-key', ...await scrollRegion.evaluate(node => ({
    width: node.clientWidth, scrollWidth: node.scrollWidth, scrollLeft: node.scrollLeft, focused: document.activeElement === node,
    overflowX: getComputedStyle(node).overflowX,
  })) });
  const beforeScroll = await scrollRegion.evaluate(node => node.scrollLeft); await zoomPage.keyboard.press('ArrowRight');
  await expect.poll(() => scrollRegion.evaluate(node => node.scrollLeft)).toBeGreaterThan(beforeScroll);
  diagnostics.push({ check: 'zoom-scroll-region-after-key', scrollLeft: await scrollRegion.evaluate(node => node.scrollLeft) });
  for (const target of [page, zoomPage]) {
    const storage = await target.evaluate(() => [localStorage.length, sessionStorage.length]); diagnostics.push({ check: 'browser-storage', local: storage[0], session: storage[1] }); assert.deepEqual(storage, [0, 0]);
  }
  diagnostics.push({ check: 'page-errors', types: pageErrors }); assert.deepEqual(pageErrors, []); await zoomContext.close(); complete(phase);
  await writeFile(join(results, 'result.json'), JSON.stringify({ passed: true, stages, injections, screenshots, diagnostics,
    resumedFrom: resumeViewport ? 'viewport' : resumeFreeze ? 'freeze' : resumeBrowser ? 'prepared browser facts' : null, previouslyCompletedStagesNotRerun: resumeFreeze || resumeViewport,
    verificationHost: process.platform + '/' + process.arch, releaseCommit: identity.releaseCommit, releaseDirty: identity.releaseDirty,
    browserTrust: 'generated-leaf-SPKI-pins; preparation API validates CA and hostname', simulatedInstallation: true, realGitLab: false,
    zoomVerification: '200 percent equivalent CSS viewport and device pixel ratio; desktop browser UI zoom not asserted',
    notCovered: ['factory installation', 'production capacity', 'MQ recovery', 'file recovery', 'backup restore'] }, null, 2) + '\n');
} catch (error) {
  fixture.lastFailurePhase = phase; await saveFixture().catch(() => {});
  await page?.screenshot({ path: join(results, 'browser-failure.png'), fullPage: true }).catch(() => {});
  await writeFile(join(results, 'result.json'), JSON.stringify({ passed: false, phase, stages, injections, screenshots, diagnostics, errorType: error.name,
    resumedFrom: resumeViewport ? 'viewport' : resumeFreeze ? 'freeze' : resumeBrowser ? 'prepared browser facts' : null,
    simulatedInstallation: true, realGitLab: false }, null, 2) + '\n');
  console.error('Targeted UI verification failed at ' + phase + ' (' + error.name + ').'); process.exitCode = 1;
} finally {
  if (workersStopped) { try { compose('up', '-d', 'worker-a', 'worker-b'); } catch { console.error('Owned demo workers could not be restarted.'); process.exitCode = 1; } }
  agent.destroy(); await browser?.close();
}
