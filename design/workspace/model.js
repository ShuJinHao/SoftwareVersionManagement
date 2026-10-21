// Deliberately isolated product prototype. No API, storage, credentials or production imports.
export const roles = {
  admin: { name: '平台管理员', scope: '全部演示工序', actions: ['asset.create', 'software.add', 'source.manage', 'plc.upload', 'plc.publish', 'plc.confirm', 'deployment.create', 'deployment.control', 'access.manage', 'identity.manage'] },
  engineer: { name: '程序工程师', scope: '装配工序 · 全部演示设备', actions: ['software.add', 'plc.upload', 'plc.publish', 'plc.confirm', 'access.manage'] },
  operator: { name: '现场运维', scope: '装配工序 · 演示装配设备 01', actions: ['plc.confirm', 'deployment.create', 'deployment.control'] },
  viewer: { name: '只读人员', scope: '检测工序 · 全部演示设备', actions: [] },
};

export function initialState() {
  const state = {
    role: 'admin', page: 'workbench', view: 'process', processId: 'assembly', deviceId: null, productId: null,
    previousScope: null, hardwareId: null, selectedTargets: [], expanded: [], query: '', category: 'all', listPage: 1,
    archiveDownloads: [],
    connection: { id: 'gitlab-demo', name: '厂内 GitLab', address: 'https://gitlab.example.invalid', enabled: true },
    sequence: 5, catalog: { query: '', kind: 'all', page: 1 },
    processes: [{ id: 'assembly', name: '装配工序' }, { id: 'inspection', name: '检测工序' }],
    devices: [
      { id: 'device-01', processId: 'assembly', name: '演示装配设备 01', number: 'EQ-000001', assetNumber: '可选现场资产编号', hosts: [
        { id: 'ipc-1', name: '主控工控机', ip: '192.0.2.11' }, { id: 'ipc-2', name: '视觉工控机', ip: '192.0.2.12' }],
        controllers: [{ id: 'plc-1', name: '主控 PLC', ip: '192.0.2.21' }, { id: 'plc-2', name: '搬运 PLC', ip: '192.0.2.22' }] },
      { id: 'device-02', processId: 'assembly', name: '演示装配设备 02', number: 'EQ-000002', assetNumber: '', hosts: [
        { id: 'ipc-3', name: '主控工控机', ip: '192.0.2.31' }], controllers: [{ id: 'plc-3', name: '主控 PLC', ip: '192.0.2.32' }] },
      { id: 'device-03', processId: 'inspection', name: '演示检测设备 01', number: 'EQ-000003', assetNumber: '', hosts: [
        { id: 'ipc-4', name: '检测工控机', ip: '192.0.2.41' }], controllers: [] },
      { id: 'device-04', processId: 'inspection', name: '演示检测设备 02', number: 'EQ-000004', assetNumber: '', hosts: [], controllers: [] },
    ],
    products: [
      { id: 'upper', name: '装配控制软件', kind: 'UpperComputer', source: 'GitLab', project: 'demo / assembly-control', package: 'assembly-control', state: 'ready', version: '1.3.0', tag: 'v1.3.0', note: '优化启动检查，修复历史记录查询。', time: '10-18 09:40', revision: 1 },
      { id: 'vision', name: '外观检测软件', kind: 'Vision', source: 'GitLab', project: 'demo / vision-inspection', package: 'vision-inspection', state: 'failed', version: '2.1.0', tag: 'v2.1.0', note: '调整缺陷识别参数与检测结果展示。', time: '10-18 09:35', revision: 1 },
      { id: 'pending', name: '检测采集软件', kind: 'UpperComputer', source: 'GitLab', project: '', package: '', state: 'unbound', version: null, tag: null, note: '', time: null, revision: 1 },
    ],
    installations: [
      { id: 'installation-1', deviceId: 'device-01', hostId: 'ipc-1', productId: 'upper', version: '1.2.4', running: '运行', report: '正常上报', reported: '10-18 10:26', elapsed: '32 秒', access: true },
      { id: 'installation-2', deviceId: 'device-01', hostId: 'ipc-2', productId: 'vision', version: '2.1.0', running: '最后记录：停止', report: '状态未知', reported: '10-18 09:58', elapsed: '28 分钟', access: true },
      { id: 'installation-3', deviceId: 'device-02', hostId: 'ipc-3', productId: 'upper', version: '1.3.0', running: '运行', report: '正常上报', reported: '10-18 10:26', elapsed: '46 秒', access: true },
      { id: 'installation-4', deviceId: 'device-03', hostId: 'ipc-4', productId: 'pending', version: null, running: '—', report: '尚未接入', reported: '—', elapsed: '—', access: false },
    ],
    archives: [
      { id: 'archive-1', deviceId: 'device-01', controllerId: 'plc-1', name: '主控 PLC 程序', confirmed: '1.0.0', confirmedBy: '演示工程师', confirmedAt: '10-17 16:20', versions: [
        { version: '1.1.0', state: 'Test', file: 'demo-main-plc-1.1.0.zip', size: '24.8 MB', summary: '优化设备复位与联锁处理。', time: '10-18 08:30', conclusion: null },
        { version: '1.0.0', state: 'Formal', file: 'demo-main-plc-1.0.0.zip', size: '24.2 MB', summary: '初始归档版本。', time: '10-17 15:10', conclusion: '演示记录：复位、手动操作与联锁检查通过。' }] },
      { id: 'archive-2', deviceId: 'device-01', controllerId: 'plc-2', name: '搬运 PLC 程序', confirmed: null, confirmedBy: null, confirmedAt: null, versions: [
        { version: '1.0.0', state: 'Test', file: 'demo-transfer-plc-1.0.0.zip', size: '12.6 MB', summary: '首份程序归档，等待现场验证。', time: '10-18 09:00', conclusion: null }] },
      { id: 'archive-3', deviceId: 'device-02', controllerId: 'plc-3', name: '主控 PLC 程序', confirmed: null, versions: [] },
    ],
    tasks: [{ id: 'task-demo-01', deviceId: 'device-02', installationId: 'installation-3', host: '主控工控机', productId: 'upper', version: '1.3.0', state: '已完成', progress: '1 / 1', time: '10-18 09:50' }],
    events: [{ deviceId: 'device-01', title: 'GitLab 正式版本已就绪', detail: '装配控制软件 · v1.3.0 · 等待人员选择更新目标', time: '09:40' },
      { deviceId: 'device-01', title: 'PLC 测试程序已归档', detail: '主控 PLC · 1.1.0 · 等待现场验证', time: '08:30' }],
  };
  // Product libraries are shared; actual installation/confirmation facts stay on each target.
  state.products.push(
    { id: 'plc-main', name: '主控 PLC 程序', kind: 'PLC', source: '程序归档', versions: state.archives[0].versions },
    { id: 'plc-transfer', name: '搬运 PLC 程序', kind: 'PLC', source: '程序归档', versions: state.archives[1].versions },
    { id: 'diagnostics', name: '设备诊断软件', kind: 'UpperComputer', source: 'GitLab', project: 'demo / diagnostics', package: 'diagnostics', state: 'ready', version: '1.0.0', tag: 'v1.0.0', revision: 1 },
  );
  for (const archive of state.archives) {
    archive.productId = archive.id === 'archive-2' ? 'plc-transfer' : 'plc-main';
    delete archive.versions;
    delete archive.name;
  }
  state.installations.push({ id: 'installation-5', deviceId: 'device-01', hostId: 'ipc-1', productId: 'diagnostics', version: '1.0.0', running: '运行', report: '正常上报', reported: '10-18 10:26', elapsed: '35 秒', access: true });
  // Only review fixtures: enough products to inspect real list density and pagination.
  for (let n = 7; n <= 120; n++) {
    const kind = ['UpperComputer', 'Vision', 'PLC'][(n - 7) % 3];
    state.products.push({ id: `catalog-${n}`, name: `演示${kindNames[kind]}软件 ${String(n).padStart(3, '0')}`, kind, source: kind === 'PLC' ? '程序归档' : 'GitLab', versions: [], state: 'unbound', project: '', version: null, revision: 1 });
  }
  return state;
}

export const kindNames = { UpperComputer: '上位机', Vision: '视觉', PLC: 'PLC' };
export const sourceStates = {
  unbound: { label: '待关联 GitLab', tone: 'neutral', description: '选择已配置连接中的项目和产物后，平台开始同步正式发布。' },
  waiting: { label: '等待正式发布', tone: 'neutral', description: '项目已关联，尚未发现符合条件的正式 Tag。' },
  syncing: { label: '同步中', tone: 'blue', description: '正在核对正式 Tag、流水线和发布清单。' },
  processing: { label: '包处理中', tone: 'blue', description: '下载校验与两个副本核对完成后，版本才可供包。' },
  ready: { label: '同步正常', tone: 'green', description: '正式版本已在平台就绪，设备更新由人员单独发起。' },
  failed: { label: '同步失败', tone: 'amber', description: '演示故障：GitLab 暂时不可达。已有可用版本仍保留，待恢复后重新同步。' },
};

export function visibleDevices(state) {
  return state.devices.filter(d => state.role === 'admin' || state.role === 'engineer' && d.processId === 'assembly' || state.role === 'operator' && d.id === 'device-01' || state.role === 'viewer' && d.processId === 'inspection');
}
export function can(state, action, deviceId) {
  return roles[state.role].actions.includes(action) && (!deviceId || visibleDevices(state).some(d => d.id === deviceId));
}
export function selectRole(state, role) {
  if (!roles[role]) throw new Error('未知演示角色');
  state.role = role; state.catalog = { query: '', kind: 'all', page: 1 };
  const devices = visibleDevices(state);
  selectScope(state, { processId: devices[0]?.processId ?? null });
  state.previousScope = null;
}
export function rowsForDevice(state, deviceId) {
  if (!visibleDevices(state).some(d => d.id === deviceId)) return [];
  const device = state.devices.find(d => d.id === deviceId);
  return [
    ...state.installations.filter(x => x.deviceId === deviceId).map(x => {
      const product = state.products.find(p => p.id === x.productId), host = device.hosts.find(h => h.id === x.hostId);
      return { ...x, hardwareId: x.hostId, bound: !!host, name: product.name, kind: product.kind, product, host: host?.name ?? '待补充归属', ip: host?.ip ?? '—', latest: product.version, source: 'GitLab', update: !!product.version && product.version !== x.version };
    }),
    ...state.archives.filter(x => x.deviceId === deviceId).map(x => {
      const plc = device.controllers.find(p => p.id === x.controllerId), product = state.products.find(p => p.id === x.productId);
      return { ...x, hardwareId: x.controllerId, bound: !!plc, name: product.name, product, kind: 'PLC', host: plc?.name ?? '待补充归属', ip: plc?.ip ?? '—', version: x.confirmed, latest: latestVersion(product), source: '程序归档', report: '人工确认', running: '未采集', update: false };
    }),
  ];
}
export function createDevice(state, { processId, name, assetNumber = '' }) {
  if (!can(state, 'asset.create') || !state.processes.some(p => p.id === processId)) throw new Error('无权在该工序创建设备');
  if (!name.trim()) throw new Error('请填写设备名称');
  const n = state.sequence++, device = { id: `prototype-device-${n}`, processId, name: name.trim(), number: `EQ-${String(n).padStart(6, '0')}`, assetNumber: assetNumber.trim(), hosts: [], controllers: [] };
  state.devices.push(device); selectScope(state, { deviceId: device.id });
  return device;
}
export function deviceHardware(device) {
  return [...device.hosts.map(h => ({ ...h, type: 'IPC' })), ...device.controllers.map(h => ({ ...h, type: 'PLC' }))];
}
export function addHardware(state, { deviceId, type, name, ip = '' }) {
  const d = visibleDevices(state).find(d => d.id === deviceId);
  if (!d || !can(state, 'software.add', deviceId)) throw new Error('无权维护此设备');
  if (!['IPC', 'PLC'].includes(type) || !name.trim()) throw new Error('请选择硬件类型并填写名称');
  const hardware = { id: `prototype-hardware-${state.sequence++}`, name: name.trim(), ip: ip.trim() };
  (type === 'PLC' ? d.controllers : d.hosts).push(hardware);
  return hardware;
}
export function bindSoftware(state, { deviceId, hardwareId, kind, productId, name = '', role }) {
  if (state.role !== role) throw new Error('人员已变化，请重新选择');
  const d = visibleDevices(state).find(d => d.id === deviceId);
  if (!d || !can(state, 'software.add', deviceId)) throw new Error('无权维护此设备');
  if (!Object.hasOwn(kindNames, kind)) throw new Error('软件类型无效');
  const plc = kind === 'PLC', hardware = (plc ? d.controllers : d.hosts).find(h => h.id === hardwareId);
  if (!hardware) throw new Error('必须绑定当前设备下类型匹配的具体硬件');
  let p = catalogProducts(state).find(p => p.id === productId && p.kind === kind);
  if (productId && !p) throw new Error('所选软件不存在、类型不匹配或无权使用');
  if (!p && !name.trim()) throw new Error('请填写软件或程序名称');
  const records = plc ? state.archives : state.installations, field = plc ? 'controllerId' : 'hostId';
  if (p && records.some(r => r.deviceId === deviceId && r[field] === hardwareId && r.productId === p.id)) throw new Error('该硬件已关联此软件');
  const n = state.sequence++;
  if (!p) {
    p = { id: `product-new-${n}`, name: name.trim(), kind, source: plc ? '程序归档' : 'GitLab', versions: [], project: '', package: '', state: 'unbound', version: null, tag: null, revision: 1 };
    state.products.push(p);
  }
  const record = plc
    ? { id: `archive-new-${n}`, deviceId, controllerId: hardwareId, productId: p.id, confirmed: null, revision: 0 }
    : { id: `installation-new-${n}`, deviceId, hostId: hardwareId, productId: p.id, version: null, running: '—', report: '尚未接入', reported: '—', elapsed: '—', access: false };
  records.push(record);
  return record;
}
function boundArchive(state, archiveId) {
  const archive = state.archives.find(a => a.id === archiveId);
  const d = visibleDevices(state).find(d => d.id === archive?.deviceId);
  if (!d?.controllers.some(c => c.id === archive.controllerId)) throw new Error('PLC 归属无效或无权查看');
  return archive;
}
export function downloadArchive(state, { archiveId, version, role }) {
  if (state.role !== role) throw new Error('人员已变化');
  const archive = boundArchive(state, archiveId);
  const record = state.products.find(p => p.id === archive.productId)?.versions.find(v => v.version === version);
  if (!record) throw new Error('归档版本不存在');
  const download = { id: `download-${state.sequence++}`, archiveId, controllerId: archive.controllerId, version, file: record.file, time: new Date().toISOString() };
  state.archiveDownloads.push(download);
  return download;
}
export function confirmPlcUsage(state, { archiveId, controllerId, version, revision, role, usedAt, evidence, confirmed }) {
  if (state.role !== role) throw new Error('人员已变化');
  const archive = boundArchive(state, archiveId);
  if (!can(state, 'plc.confirm', archive.deviceId)) throw new Error('无现场使用登记权限');
  if (controllerId !== archive.controllerId || revision !== (archive.revision ?? 0)) throw new Error('控制器或记录已变化，请重新核实');
  const record = state.products.find(p => p.id === archive.productId)?.versions.find(v => v.version === version);
  if (!record) throw new Error('归档版本不存在');
  if (!confirmed || !evidence.trim() || !Number.isFinite(Date.parse(usedAt))) throw new Error('请填写现场时间、说明并确认已完成现场操作');
  const entry = { version, previousVersion: archive.confirmed ?? null, controllerId, usedAt, evidence: evidence.trim(), operator: `演示人员（${roles[state.role].name}）`, recordedAt: new Date().toISOString() };
  archive.history = [...(archive.history ?? []), entry];
  archive.confirmed = version; archive.confirmedBy = entry.operator; archive.confirmedAt = usedAt; archive.revision = revision + 1;
  return entry;
}
export function publishArchive(state, { archiveId, version, conclusion, verification, confirmed }) {
  const archive = state.archives.find(a => a.id === archiveId);
  const program = state.products.find(p => p.id === archive?.productId);
  if (!archive || !can(state, 'plc.publish', archive.deviceId) || !canManageProgram(state, program.id, 'plc.publish')) throw new Error('无权发布该 PLC 程序');
  boundArchive(state, archiveId);
  const record = program.versions.find(v => v.version === version);
  if (!record || record.state !== 'Test') throw new Error('测试版本已变化，请重新核实');
  if (!confirmed || !conclusion.trim() || !verification.trim()) throw new Error('请填写现场验证记录、结论并确认对象');
  record.state = 'Formal'; record.conclusion = conclusion.trim(); record.verification = verification.trim(); record.publisher = `演示人员（${roles[state.role].name}）`; record.publishedAt = '本次原型操作';
  // Publishing an archive must not change the actual/confirmed PLC version.
  return record;
}
export function importRelease(state, productId) {
  if (!can(state, 'source.manage')) throw new Error('无权管理发布来源');
  const product = state.products.find(p => p.id === productId);
  if (!product?.project) throw new Error('请先关联 GitLab 项目');
  if (!state.connection.enabled) throw new Error('演示连接已停用，已有版本仍保留');
  product.state = 'ready'; product.version = product.kind === 'Vision' ? '2.1.1' : '1.3.1'; product.tag = 'v' + product.version; product.time = '本次原型操作'; product.revision++;
  return product;
}

export function latestVersion(product) {
  if (product.kind !== 'PLC') return product.version;
  return product.versions.filter(v => v.state === 'Formal').map(v => v.version).sort((a, b) => {
    const left = a.split('.').map(Number), right = b.split('.').map(Number);
    return right[0] - left[0] || right[1] - left[1] || right[2] - left[2];
  })[0] ?? null;
}
export function relatedTargets(state, productId) {
  return visibleDevices(state).flatMap(d => rowsForDevice(state, d.id).filter(r => r.productId === productId).map(r => ({ ...r, device: d })));
}
export function relatedDevices(state, productId) {
  const ids = new Set(relatedTargets(state, productId).map(t => t.deviceId));
  return visibleDevices(state).filter(d => ids.has(d.id));
}
export function catalogProducts(state) {
  if (state.role === 'admin') return state.products;
  const ids = new Set(visibleDevices(state).flatMap(d => rowsForDevice(state, d.id).map(r => r.productId)));
  return state.products.filter(p => ids.has(p.id));
}
export function catalogPage(state, { query = '', kind = 'all', page = 1 } = state.catalog) {
  const matches = catalogProducts(state).filter(p => (kind === 'all' || p.kind === kind) && `${p.name} ${p.project ?? ''}`.toLocaleLowerCase().includes(query.trim().toLocaleLowerCase()));
  const pages = Math.max(1, Math.ceil(matches.length / 20)), current = Math.min(Math.max(1, page), pages);
  return { items: matches.slice((current - 1) * 20, current * 20), total: matches.length, page: current, pages };
}
export function canManageProgram(state, productId, action) {
  // Explicit prototype product grants, independent from visibility of a related device.
  return can(state, action) && (state.role === 'admin' || state.role === 'engineer' && ['plc-main', 'plc-transfer'].includes(productId));
}
export function updateEligibility(state, target, version) {
  if (target.kind === 'PLC') return 'PLC 使用程序归档，不下发更新';
  if (!can(state, 'deployment.create', target.deviceId)) return '无此机台更新权限';
  if (!target.bound) return '工控机归属待补充';
  if (!target.access || !target.version) return '尚未接入或版本待核实';
  if (!version) return '尚无正式版本';
  if (state.tasks.some(t => t.installationId === target.id && t.state !== '已完成')) return '已有进行中任务';
  const a = version.split('.').map(Number), b = target.version.split('.').map(Number);
  const direction = a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
  return direction === 0 ? '已是此版本' : direction < 0 ? '需要回退，当前未开放' : '';
}
export function createUpdates(state, { productId, version, installationIds, reason, role, bindings }) {
  if (state.role !== role) throw new Error('人员已变化，请重新选择目标');
  const product = state.products.find(p => p.id === productId);
  if (!product || product.kind === 'PLC' || latestVersion(product) !== version) throw new Error('目标正式版本已变化，请重新核实');
  const targets = relatedTargets(state, productId), ids = [...new Set(installationIds)];
  if (!ids.length || !reason.trim()) throw new Error('请选择机台并填写更新原因');
  const chosen = ids.map(id => { const target = targets.find(t => t.id === id); if (!target || updateEligibility(state, target, version)) throw new Error('目标不可更新，请重新核实'); return target; });
  if (bindings && chosen.some(t => !bindings.some(b => b.id === t.id && b.deviceId === t.deviceId && b.hostId === t.hostId))) throw new Error('安装硬件归属已变化，请重新核实');
  const tasks = chosen.map(t => ({ id: `prototype-task-${state.sequence++}`, installationId: t.id, deviceId: t.deviceId, hostId: t.hostId, host: t.host, productId, version, reason: reason.trim(), state: '等待领取', progress: '0 / 1', time: '本次原型操作' }));
  state.tasks.unshift(...tasks);
  return tasks;
}

// One workspace, three projections of the same authorized relationships.
// Software focus always covers all authorized related devices, never a hidden old device filter.
export function selectScope(state, scope) {
  const old = { processId: state.processId, deviceId: state.deviceId };
  if (state.hardwareId) old.hardwareId = state.hardwareId;
  if (scope.productId) {
    if (!catalogProducts(state).some(p => p.id === scope.productId)) throw new Error('无权查看此软件');
    if (state.view !== 'software') state.previousScope = old;
    state.view = 'software'; state.productId = scope.productId; state.processId = null; state.deviceId = null; state.hardwareId = null;
  } else if (scope.deviceId) {
    const d = visibleDevices(state).find(d => d.id === scope.deviceId);
    if (!d) throw new Error('无权查看此设备');
    if (scope.hardwareId && !deviceHardware(d).some(h => h.id === scope.hardwareId)) throw new Error('硬件不属于此设备');
    state.view = 'device'; state.deviceId = d.id; state.processId = d.processId; state.productId = null; state.previousScope = null;
    state.hardwareId = scope.hardwareId ?? null;
  } else {
    const id = scope.processId ?? null;
    if (id && !state.processes.some(p => p.id === id && (state.role === 'admin' || visibleDevices(state).some(d => d.processId === id)))) throw new Error('无权查看此工序');
    state.view = 'process'; state.processId = id; state.deviceId = null; state.productId = null; state.previousScope = null; state.hardwareId = null;
  }
  state.page = 'workbench'; state.selectedTargets = []; state.expanded = []; state.query = ''; state.category = 'all'; state.listPage = 1;
}
export function workspaceDevices(state) {
  if (state.view === 'software') return relatedDevices(state, state.productId);
  return visibleDevices(state).filter(d => state.view === 'device' ? d.id === state.deviceId : !state.processId || d.processId === state.processId);
}
export function workspaceRows(state) {
  return workspaceDevices(state).flatMap(d => rowsForDevice(state, d.id).filter(r => (state.view !== 'software' || r.productId === state.productId) && (!state.hardwareId || r.hardwareId === state.hardwareId)).map(r => ({ ...r, device: d })));
}
export function workspaceTasks(state) {
  const ids = new Set(workspaceRows(state).map(r => r.id));
  return state.tasks.filter(t => ids.has(t.installationId));
}
export function selectTarget(state, id, selected) {
  if (state.view !== 'software') throw new Error('请先选择软件');
  const target = workspaceRows(state).find(r => r.id === id), p = state.products.find(p => p.id === state.productId);
  if (!target || selected && updateEligibility(state, target, latestVersion(p))) throw new Error('此安装对象当前不可更新');
  state.selectedTargets = state.selectedTargets.filter(x => x !== id);
  if (selected) state.selectedTargets.push(id);
}
