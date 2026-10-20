import { initialState, roles, kindNames, sourceStates, visibleDevices, can, selectRole, rowsForDevice, createDevice, publishArchive, importRelease, latestVersion, relatedTargets, relatedDevices, catalogProducts, catalogPage, canManageProgram, updateEligibility, createUpdates, selectScope, workspaceDevices, workspaceRows, workspaceTasks, selectTarget } from './model.js';

let state = initialState(), dialogReturnFocus = null, selectedRole = 'engineer', toastTimer, catalogInput = { query: '', kind: 'all' };
const root = document.querySelector('#app'), overlay = document.querySelector('#overlay');
const e = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
const paths = {
  factory: '<path d="M3 21V9l6 3V7l6 4V3h4v18zM7 17h1m4 0h1m4 0h1"/>',
  grid: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  box: '<path d="m12 3 9 5v9l-9 5-9-5V8zM3 8l9 5 9-5M12 13v9M7 5.8l9 5"/>',
  task: '<path d="M8 3h8v4H8zM8 5H5v16h14V5h-3M8 12l2 2 5-4M8 18h7"/>',
  settings: '<path d="M4 6h16M4 12h16M4 18h16"/><circle cx="8" cy="6" r="2" fill="currentColor"/><circle cx="16" cy="12" r="2" fill="currentColor"/><circle cx="10" cy="18" r="2" fill="currentColor"/>',
  chevron: '<path d="m9 6 6 6-6 6"/>', down: '<path d="m6 9 6 6 6-6"/>', plus: '<path d="M12 5v14M5 12h14"/>',
  monitor: '<rect x="3" y="4" width="18" height="13" rx="2"/><path d="M8 21h8M12 17v4"/>',
  eye: '<path d="M2 12s3-7 10-7 10 7 10 7-3 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  cpu: '<rect x="5" y="5" width="14" height="14" rx="2"/><path d="M9 2v3m6-3v3M9 19v3m6-3v3M2 9h3m-3 6h3m14-6h3m-3 6h3"/><rect x="9" y="9" width="6" height="6" rx="1"/>',
  search: '<circle cx="10" cy="10" r="6.5"/><path d="m15 15 5 5"/>',
  refresh: '<path d="M20 7v5h-5M4 17v-5h5"/><path d="M6 7a7 7 0 0 1 12-1l2 3M4 15l2 3a7 7 0 0 0 12-1"/>',
  shield: '<path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6zM8 12l3 3 5-6"/>',
  close: '<path d="m6 6 12 12M6 18 18 6"/>', arrow: '<path d="M4 12h16m-6-6 6 6-6 6"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  git: '<circle cx="7" cy="5" r="2"/><circle cx="17" cy="6" r="2"/><circle cx="7" cy="19" r="2"/><path d="M7 7v10m10-9v3a4 4 0 0 1-4 4H7"/>',
  key: '<circle cx="7" cy="8" r="4"/><path d="m10 11 9 9m-2-2 3-3m-6 0 3-3"/>',
  upload: '<path d="M12 16V3m-5 5 5-5 5 5M4 15v6h16v-6"/>',
  check: '<path d="m5 12 4 4L19 6"/>', empty: '<path d="m3 7 9-4 9 4v13H3zM3 12h5l2 3h4l2-3h5"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21v-2a8 8 0 0 1 16 0v2"/>',
};
const icon = name => `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${paths[name] ?? paths.box}</svg>`;
const pill = (label, tone = 'neutral', dot = false) => `<span class="pill ${tone}">${dot ? '<i class="status-dot"></i>' : ''}${e(label)}</span>`;
const button = (label, action, id = '', style = '', symbol = '', disabled = false) => `<button type="button" class="${style}" data-action="${action}" data-id="${e(id)}" ${!label && action === 'close' ? 'aria-label="关闭对话框"' : ''} ${disabled ? 'disabled' : ''}>${symbol ? icon(symbol) : ''}${e(label)}</button>`;
const device = () => visibleDevices(state).find(d => d.id === state.deviceId);
const product = id => state.products.find(p => p.id === id);
const processName = id => state.processes.find(p => p.id === id)?.name ?? '未选择工序';
const kindIcon = kind => icon(kind === 'PLC' ? 'cpu' : kind === 'Vision' ? 'eye' : 'monitor');
const pageNames = { workbench: '软件工作台', settings: '系统管理' };
const fact = (name, value) => `<div class="fact-line"><span>${e(name)}</span><strong>${e(value)}</strong></div>`;
function toast(message) { clearTimeout(toastTimer); document.querySelector('#notice').textContent = message; toastTimer = setTimeout(() => document.querySelector('#notice').textContent = '', 6500); }
function roleOptions(value) { return Object.entries(roles).map(([id, role]) => `<option value="${id}" ${id === value ? 'selected' : ''}>${role.name}</option>`).join(''); }
function empty(title, description, action = '') { return `<div class="empty">${icon('empty')}<h3>${e(title)}</h3><p>${e(description)}</p>${action}</div>`; }
function visibleProducts() { return catalogProducts(state); }

function render() {
  root.innerHTML = `<div class="prototype-bar"><strong>交互原型 · 全部为虚构数据</strong><span>不连接 GitLab、PLC 或生产 API · 操作仅保留在本页内存</span>${button('重置演示', 'reset')}</div>
  <div class="app"><aside class="sidebar"><div class="brand"><div class="logo">${icon('factory')}</div><div><strong>软件管控</strong><small>SOFTWARE OPERATIONS</small></div></div>
  <nav class="nav" aria-label="主要导航">${[['workbench','grid','软件工作台'],...(can(state,'identity.manage')?[['settings','settings','系统管理']]:[])].map(([id, symbol, label]) => `<button type="button" data-action="page" data-id="${id}" class="${state.page === id ? 'active' : ''}" title="${label}" ${state.page === id ? 'aria-current="page"' : ''}>${icon(symbol)}<span>${label}</span></button>`).join('')}</nav>
  <div class="sidebar-bottom"><strong><i class="site-dot"></i>虚构演示厂区</strong><p>独立部署 · Asia/Shanghai</p></div></aside>
  <main class="main"><header class="topbar"><div class="breadcrumbs">${icon('factory')}<span>演示厂区</span>${icon('chevron')}<strong>${pageNames[state.page]}</strong></div>
  <div class="account"><label class="prototype-role"><span>预览身份</span><select id="role-select" aria-label="预览身份">${roleOptions(state.role)}</select></label><button type="button" data-action="account" aria-label="打开账户菜单"><span class="avatar">演</span><span class="account-copy"><strong>演示人员</strong><small>${roles[state.role].name}</small></span>${icon('down')}</button></div></header>
  <div class="content">${state.page === 'workbench' ? workbenchView() : permissionsView()}</div></main></div>`;
}
function pageHeading(title, subtitle, actions = '') { return `<div class="page-heading"><div><div class="eyebrow">${state.page === 'workbench' ? 'SOFTWARE WORKSPACE' : 'SYSTEM MANAGEMENT'}</div><h1>${title}</h1><p>${subtitle}</p></div><div class="heading-actions">${actions}</div></div>`; }
function focusScope(scope) { closeDialog(); selectScope(state,scope); render(); }
function workbenchView() {
  const devices = visibleDevices(state), processes = state.processes.filter(p => devices.some(d => d.processId === p.id) || state.role === 'admin');
  const selected = state.view==='device' ? 'device|'+state.deviceId : state.view==='process' ? 'process|'+(state.processId??'') : '';
  return `${pageHeading('软件工作台','按工序、设备或软件，找到现场。',button('查找软件','find-software','','primary','search'))}
  <div class="mobile-device-picker"><label>工序 / 设备<select id="scope-picker"><option value="" ${!selected?'selected':''} disabled>当前正在查看软件的全部关联机台</option><option value="process|" ${selected==='process|'?'selected':''}>全部工序</option>${processes.map(p=>`<option value="process|${p.id}" ${selected==='process|'+p.id?'selected':''}>${e(p.name)}</option>${devices.filter(d=>d.processId===p.id).map(d=>`<option value="device|${d.id}" ${selected==='device|'+d.id?'selected':''}>　${e(d.name)}</option>`).join('')}`).join('')}</select></label></div>
  <div class="workspace"><aside class="device-nav" aria-label="工序与设备"><div class="device-nav-head"><h2>工序与设备</h2>${can(state,'asset.create')?button('新增','create-process','','ghost small','plus'):''}</div>
  ${button('全部工序','process','','tree-all '+(state.view==='process'&&!state.processId?'active':''),'factory')}
  ${processes.map(p=>`<button type="button" class="process-choice ${state.view==='process'&&state.processId===p.id?'active':''}" data-action="process" data-id="${p.id}"><span>${icon('down')}${e(p.name)}</span><small>${devices.filter(d=>d.processId===p.id).length}</small></button>${devices.filter(d=>d.processId===p.id).map(d=>`<button class="device-choice ${state.deviceId===d.id?'active':''}" type="button" data-action="device" data-id="${d.id}" ${state.deviceId===d.id?'aria-current="true"':''}>${icon('grid')}<span class="device-mini"><strong>${e(d.name)}</strong><small>${e(d.number)}</small></span></button>`).join('')}`).join('')}
  <div class="tree-foot">${icon('shield')} ${e(roles[state.role].scope)}</div></aside>
  <section class="device-main" aria-label="软件工作台内容">${state.view==='process'?processView():state.view==='device'?deviceView(device()):softwareFocusView()}</section></div>`;
}
function contextHeader(title, subtitle, actions='', back='') {
  return `<div class="context-header">${back?`<div class="context-back">${back}</div>`:''}<div class="context-heading"><div><h2>${e(title)}</h2><p>${e(subtitle)}</p></div><div class="context-actions">${actions}</div></div></div>`;
}
function queryBar(extra='') {
  return `<form id="workspace-search" class="toolbar"><div class="filters"><label class="search-box">${icon('search')}<input name="query" aria-label="筛选当前列表" placeholder="${state.view==='device'?'软件名称':'设备名称或编号'}" value="${e(state.query)}"></label><button type="submit" class="small">查询</button>${state.view==='device'?`<select name="category" aria-label="软件类型">${[['all','全部类型'],...Object.entries(kindNames)].map(([id,name])=>`<option value="${id}" ${state.category===id?'selected':''}>${name}</option>`).join('')}</select>`:''}</div>${extra}</form>`;
}
function paginate(items) {
  const pages=Math.max(1,Math.ceil(items.length/20)), page=Math.max(1,Math.min(state.listPage,pages));
  return {items:items.slice((page-1)*20,page*20),page,pages,total:items.length};
}
function listFooter(result, label) {
  return `<div class="table-foot"><span>${result.total} ${label}${result.pages>1?` · 第 ${result.page} / ${result.pages} 页`:''}</span>${result.pages>1?`<div>${button('上一页','list-page',String(result.page-1),'small','',result.page===1)}${button('下一页','list-page',String(result.page+1),'small','',result.page===result.pages)}</div>`:''}</div>`;
}
const matchesQuery = value => value.toLocaleLowerCase().includes(state.query.trim().toLocaleLowerCase());
function taskCell(row) {
  const t=state.tasks.find(t=>t.installationId===row.id);
  return t?`<button type="button" class="task-link ${t.state==='已完成'?'complete':''}" data-action="task" data-id="${t.id}">${icon(t.state==='已完成'?'check':'clock')}${e(t.state)}</button><span class="sub">目标 ${e(t.version)}</span>`:'<span class="muted">—</span>';
}
function fieldStatus(row) {
  return row.kind==='PLC'?pill(row.version?'已人工确认':'待人工确认',row.version?'neutral':'amber'):pill(row.report==='正常上报'?row.running:row.report,row.report==='正常上报'?'green':row.report==='状态未知'?'amber':'neutral',true);
}
function processView() {
  const result=paginate(workspaceDevices(state).filter(d=>matchesQuery(d.name+' '+d.number)));
  return `<section class="surface">${contextHeader(state.processId?processName(state.processId):'全部工序','查看设备，再点软件查看它关联的机台。',can(state,'asset.create')?button('新增设备','create-device','','small','plus'):'')}${queryBar()}
  ${result.items.length?`<div class="table-scroll" tabindex="0" role="region" aria-label="工序设备概况"><table><thead><tr><th>设备</th><th>对应软件</th><th>现场情况</th><th>更新进度</th></tr></thead><tbody>${result.items.map(d=>{
    const rows=rowsForDevice(state,d.id), products=[...new Map(rows.map(r=>[r.productId,r.product])).values()], shown=products.filter((p,i)=>products.findIndex(x=>x.kind===p.kind)===i), unknown=rows.filter(r=>r.kind!=='PLC'&&r.report==='状态未知').length, pending=rows.filter(r=>r.kind!=='PLC'&&!r.access).length, active=state.tasks.filter(t=>t.deviceId===d.id&&t.state!=='已完成');
    return `<tr><td>${button(d.name,'device',d.id,'link object-name')}<span class="sub">${e(d.number)}${!state.processId?' · '+e(processName(d.processId)):''}</span></td><td><div class="software-tags">${shown.map(p=>button(p.name,'focus-software',p.id,'software-tag',p.kind==='PLC'?'cpu':p.kind==='Vision'?'eye':'monitor')).join('')}${products.length>shown.length?button('+'+(products.length-shown.length)+' 款','device',d.id,'link small'):''}${!products.length?'<span class="muted">尚未添加软件</span>':''}</div></td><td>${unknown?pill(unknown+' 项状态未知','amber'):pending?pill(pending+' 项待接入'):rows.some(r=>r.kind!=='PLC')?pill('上报正常','green',true):pill(rows.length?'PLC 待按台确认':'暂无接入')}${unknown&&pending?`<span class="sub">${pending} 项待接入</span>`:''}</td><td>${active.length?button(active.length+' 项进行中','device',d.id,'link','clock'):'<span class="muted">—</span>'}</td></tr>`;
  }).join('')}</tbody></table></div>${listFooter(result,'台设备')}`:empty('没有匹配的设备','可调整查询，或在此工序新增设备。')}</section>`;
}
function deviceView(d) {
  if(!d)return empty('无可查看设备','请选择有权查看的工序或设备。');
  const grouped=new Map();
  for(const r of workspaceRows(state).filter(r=>(state.category==='all'||r.kind===state.category)&&matchesQuery(r.name))) {if(!grouped.has(r.productId))grouped.set(r.productId,[]);grouped.get(r.productId).push(r);}
  const result=paginate([...grouped.values()]);
  return `<section class="surface">${contextHeader(d.name,processName(d.processId)+' · '+d.number,
    button('设备资料','hardware',d.id,'small')+button('更新记录','context-tasks','','small')+(can(state,'software.add',d.id)?button('添加软件 / PLC','add-software',d.id,'primary small','plus'):''),button(processName(d.processId),'process',d.processId,'link'))}
  ${queryBar()}${result.items.length?`<div class="table-scroll" tabindex="0" role="region" aria-label="设备软件状态"><table><thead><tr><th>软件</th><th>当前版本</th><th>最新正式版</th><th>现场状态</th><th>更新进度</th><th></th></tr></thead><tbody>${result.items.map(rows=>{
    const r=rows[0],multi=rows.length>1,open=state.expanded.includes(r.productId);
    const identity=`<div class="software-cell"><span class="software-icon ${r.kind}">${kindIcon(r.kind)}</span><div>${button(r.name,'focus-software',r.productId,'link object-name')}<span class="sub">${kindNames[r.kind]}${multi?' · '+rows.length+' 个安装位置':''}</span></div></div>`;
    if(!multi)return `<tr><td>${identity}</td>${targetFacts(r)}<td>${button('详情','detail',r.id,'link')}</td></tr>`;
    return `<tr class="group-row"><td>${identity}</td><td colspan="3">${button((open?'收起':'展开')+rows.length+' 个安装位置','expand',r.productId,'link','down')}</td><td>${groupTasks(rows)}</td><td></td></tr>${open?rows.map(t=>`<tr class="child-row"><td>${e(t.host)}</td>${targetFacts(t)}<td>${button('详情','detail',t.id,'link')}</td></tr>`).join(''):''}`;
  }).join('')}</tbody></table></div>${listFooter(result,'款软件')}`:empty('当前没有对应软件','添加时自动关联到本设备，可复用已有软件或 PLC 程序库。',can(state,'software.add',d.id)?button('添加软件 / PLC','add-software',d.id,'primary','plus'):'')}</section>`;
}
function targetFacts(r) {
  return `<td><strong>${e(r.version??'—')}</strong>${r.kind==='PLC'?'<span class="sub">人工确认使用</span>':''}</td><td>${e(r.latest??'—')}${r.update?'<span class="new-badge">有新版</span>':''}</td><td>${fieldStatus(r)}</td><td>${r.kind==='PLC'?'<span class="muted">—</span>':taskCell(r)}</td>`;
}
function softwareFocusView() {
  const p=visibleProducts().find(p=>p.id===state.productId);if(!p)return empty('无权查看该软件','请选择当前范围内的软件。');
  const all=workspaceRows(state), grouped=workspaceDevices(state).filter(d=>matchesQuery(d.name+' '+d.number)).map(d=>({device:d,targets:all.filter(t=>t.deviceId===d.id)})), result=paginate(grouped), selectable=p.kind!=='PLC'&&can(state,'deployment.create');
  return `<section class="surface">${contextHeader(p.name,`${kindNames[p.kind]} · 全部有权查看的关联机台 · ${workspaceDevices(state).length} 台 / ${all.length} 个位置`,button('版本与来源','source',p.id,'small','box')+(p.kind!=='PLC'?button('更新记录','context-tasks','','small'):''),state.previousScope?button('返回刚才的现场','back-scope','','link'):button('全部工序','process','','link'))}
  ${queryBar(`<span class="list-hint">${p.kind==='PLC'?'当前版本为人工确认使用版本':'实际版本以现场上报为准'}</span>`)}
  ${selectable?`<div class="selection-bar"><label>目标正式版<select id="update-version" aria-label="目标正式版" ${!p.version?'disabled':''}><option>${e(p.version??'暂无可用正式版')}</option></select></label><span id="selected-count" aria-live="polite">已选 ${state.selectedTargets.length} 个安装位置</span>${button('更新所选','confirm-update',p.id,'primary small','',!state.selectedTargets.length)}</div>`:''}
  ${result.items.length?`<div class="table-scroll" tabindex="0" role="region" aria-label="软件关联机台"><table><thead><tr>${selectable?'<th aria-label="选择安装位置"></th>':''}<th>关联设备</th><th>${p.kind==='PLC'?'确认使用版本':'当前版本'}</th><th>现场状态</th>${p.kind!=='PLC'?'<th>更新进度</th>':''}<th></th></tr></thead><tbody>${result.items.map(({device:d,targets})=>{
    if(targets.length===1)return softwareTargetRow(targets[0],selectable,false);
    const open=state.expanded.includes(d.id);
    return `<tr class="group-row">${selectable?'<td></td>':''}<td>${button(d.name,'device',d.id,'link object-name')}<span class="sub">${e(d.number)} · ${e(processName(d.processId))}</span></td><td colspan="${p.kind==='PLC'?3:2}">${button((open?'收起':'展开')+targets.length+' 个安装位置','expand',d.id,'link','down')}<span class="sub">每个位置分别选择</span></td>${p.kind!=='PLC'?`<td>${groupTasks(targets)}</td><td></td>`:''}</tr>${open?targets.map(t=>softwareTargetRow(t,selectable,true)).join(''):''}`;
  }).join('')}</tbody></table></div>${listFooter(result,'台关联设备')}`:empty('暂无匹配的关联设备',state.query?'调整查询条件后重试。':'可从设备内添加此软件，已有版本仍可查看。')}</section>`;
}
function softwareTargetRow(t,selectable,child) {
  const reason=updateEligibility(state,t,t.latest);
  return `<tr class="${child?'child-row':''}">${selectable?`<td><input type="checkbox" name="workspace-target" value="${t.id}" aria-label="选择 ${e(t.device.name)} ${e(t.host)}" ${state.selectedTargets.includes(t.id)?'checked':''} ${reason?'disabled':''} ${reason?`title="${e(reason)}"`:''}></td>`:''}
  <td>${child?`<strong>${e(t.host)}</strong>`:button(t.device.name,'device',t.deviceId,'link object-name')}<span class="sub">${child?e(t.device.number):e(t.device.number)+' · '+e(processName(t.device.processId))}</span></td><td><strong>${e(t.version??'—')}</strong>${selectable&&reason?`<span class="sub">${e(reason)}</span>`:''}</td><td>${fieldStatus(t)}</td>${t.kind!=='PLC'?`<td>${taskCell(t)}</td>`:''}<td>${button('详情','detail',t.id,'link')}</td></tr>`;
}
function catalogDialog() {
  const result=catalogPage(state);
  showDialog('查找软件','选择后查看这款软件的全部关联机台',`<div class="catalog-search"><label class="search-box">${icon('search')}<input name="query" aria-label="查找软件" placeholder="软件名称或 GitLab 项目" value="${e(catalogInput.query)}"></label><select name="kind" aria-label="目录软件类型">${[['all','全部类型'],...Object.entries(kindNames)].map(([id,name])=>`<option value="${id}" ${catalogInput.kind===id?'selected':''}>${name}</option>`).join('')}</select><button type="submit">查询</button></div>
  <div class="table-scroll" tabindex="0" role="region" aria-label="可选软件目录"><table class="catalog-table"><thead><tr><th>软件 / 程序</th><th>类型</th><th>关联机台</th><th></th></tr></thead><tbody>${result.items.map(p=>`<tr><td><div class="software-cell"><span class="software-icon ${p.kind}">${kindIcon(p.kind)}</span><strong>${e(p.name)}</strong></div></td><td>${kindNames[p.kind]}</td><td>${relatedDevices(state,p.id).length} 台</td><td>${button('选择','focus-software',p.id,'link')}</td></tr>`).join('')||'<tr><td colspan="4">没有匹配的软件</td></tr>'}</tbody></table></div>
  <div class="table-foot"><span>第 ${result.page} / ${result.pages} 页 · 每页 20 款 · 共 ${result.total} 款</span><div>${button('上一页','catalog-page',String(result.page-1),'small','',result.page===1)}${button('下一页','catalog-page',String(result.page+1),'small','',result.page===result.pages)}</div></div>`,button('关闭','close'),false,form=>{state.catalog={query:String(form.get('query')??''),kind:String(form.get('kind')??'all'),page:1};catalogInput={query:state.catalog.query,kind:state.catalog.kind};catalogDialog();});
  overlay.querySelector('dialog').classList.add('wide-dialog');
}
function groupTasks(rows) {
  const ids=rows.map(r=>r.id), tasks=workspaceTasks(state).filter(t=>ids.includes(t.installationId)), active=tasks.filter(t=>t.state!=='已完成');
  return tasks.length?button(active.length?active.length+' 项进行中':tasks.length+' 项已完成','group-tasks',ids.join('|'),'link small',active.length?'clock':'check'):'<span class="muted">—</span>';
}
function contextTasks(ids=null) {
  const tasks=workspaceTasks(state).filter(t=>!ids||ids.includes(t.installationId)), title=state.view==='software'?product(state.productId).name:state.view==='device'?device().name:processName(state.processId);
  showDialog('更新记录',title,tasks.map(t=>`<div class="version-record"><div class="version-record-head"><strong>${e(state.devices.find(d=>d.id===t.deviceId).name)}</strong>${button(t.state,'task',t.id,'link')}</div><p>${e(product(t.productId).name)} · ${e(t.host)} → ${e(t.version)}</p><small>${e(t.time)}</small></div>`).join('')||empty('暂无更新记录','从软件的关联机台中选择更新对象。'),button('关闭','close'),true);
}
function hardwareView(d) {
  const cards = (items, type) => items.map(h => `<article class="hardware-card"><div class="panel-head"><span class="software-icon ${type === 'PLC' ? 'PLC' : ''}">${icon(type === 'PLC' ? 'cpu' : 'monitor')}</span>${pill(type)}</div><h3>${e(h.name)}</h3><p class="muted">${e(d.name)}</p>${fact('登记地址',h.ip || '尚未填写')}${fact(type === 'PLC' ? '程序归档' : '关联软件',type === 'PLC' ? state.archives.filter(a=>a.controllerId===h.id).length + ' 项程序关联' : state.installations.filter(i=>i.hostId===h.id).length + ' 项')}<small>地址是资料，不作为身份或在线证明。</small></article>`).join('');
  return `<div class="section-title"><h3>工控机</h3>${can(state,'software.add',d.id) ? button('添加工控机','add-host',d.id,'small','plus') : ''}</div><div class="hardware-grid">${cards(d.hosts,'工控机') || empty('尚未添加工控机','添加软件时可同时登记所在工控机。')}</div><div class="section-title"><h3>PLC 控制器</h3>${can(state,'software.add',d.id) ? button('添加 PLC','add-plc',d.id,'small','plus') : ''}</div><div class="hardware-grid">${cards(d.controllers,'PLC') || empty('尚未添加 PLC','可关联已有程序库，每台控制器分别记录确认使用版本。')}</div>`;
}
function accessView(d) {
  return `<section class="panel"><div class="panel-head"><h3>当前设备的接入配置</h3>${pill('身份与凭据')}</div><div class="notice-inline">接入材料会固定设备、工控机及软件，现场程序首次登记后取得独立身份。接入暂停只限制 API，不控制现场进程。</div>${state.installations.filter(i=>i.deviceId===d.id).map(i => `<div class="version-record"><div class="version-record-head"><strong>${e(product(i.productId).name)}</strong>${pill(i.access?'已接入':'待接入',i.access?'green':'neutral')}</div><p>${e(d.hosts.find(h=>h.id===i.hostId)?.name ?? '待补充归属')} · ${e(d.name)}</p>${can(state,'access.manage',d.id) ? button(i.access?'查看身份与恢复方式':'生成接入材料','access',i.id,'small','key') : '<small>当前角色可查看状态，无接入管理权限。</small>'}</div>`).join('') || empty('尚无软件接入对象','先在当前设备添加软件和所属工控机。')}<div class="muted-box">PLC 上传凭据在对应 PLC 程序内管理，只有上传归档权限，不具备发布或在线下装能力。</div></section>`;
}
const actionRows = [['查看设备、版本和记录','read'],['创建设备和工序','asset.create'],['添加设备软件 / PLC','software.add'],['关联 GitLab 来源','source.manage'],['上传 PLC 测试归档','plc.upload'],['PLC 人工验证转正式','plc.publish'],['创建更新任务','deployment.create'],['暂停和继续更新','deployment.control'],['管理软件接入','access.manage'],['管理人员与授权','identity.manage']];
function permissionsView() {
  if(!can(state,'identity.manage')) return empty('无权管理人员','当前账号的权限仅覆盖已显示的现场范围。');
  const role=roles[selectedRole];
  return `${pageHeading('系统管理','人员权限与厂内连接。')}<div class="management-tabs">${button('人员与权限','settings-people','','active')}${button('GitLab 连接','connections')}</div>
  <div class="role-grid"><aside class="panel role-list"><div class="panel-head"><h3>角色模板</h3></div>${Object.entries(roles).map(([id,r])=>`<button class="role-choice ${id===selectedRole?'active':''}" data-action="role-detail" data-id="${id}">${r.name}<small>${r.scope}</small></button>`).join('')}</aside><section class="surface"><div class="toolbar"><div><h2>${role.name}</h2><span class="scope-chip">${role.scope}</span></div>${button('预览此人员的页面','preview-role',selectedRole,'small','user')}</div><div class="table-scroll" tabindex="0"><table class="permissions-table"><thead><tr><th>操作</th><th>此角色的有效权限</th><th>授权范围</th></tr></thead><tbody>${actionRows.map(([label,action])=>{const allowed=action==='read'||role.actions.includes(action);return `<tr><td>${label}</td><td>${allowed?`<span class="checkmark">${icon('check')} 允许</span>`:'<span class="denied">— 未授予</span>'}</td><td>${allowed?e(action==='source.manage'||action==='identity.manage'?'显式平台管理权限':action.startsWith('plc.')?'显式授权的 PLC 程序库':role.scope):'—'}</td></tr>`;}).join('')}</tbody></table></div><div class="table-foot"><span>此处是待实施权限模型的预览，不修改当前系统账号或授权。</span></div></section></div><div class="notice-inline source-detail">创建是一项独立权限。现场范围不会自动带来共享版本的发布或停用权限；未实现的 PLC 下装、回退等操作不出现在授权目录。</div>`;
}

function closeDialog() { const d=overlay.querySelector('dialog'); if(d?.open)d.close(); overlay.innerHTML=''; if(dialogReturnFocus?.isConnected)dialogReturnFocus.focus(); }
function showDialog(title, subtitle, body, footer='', drawer=false, submit) {
  closeDialog(); dialogReturnFocus=document.activeElement;
  overlay.innerHTML=`<dialog class="${drawer?'drawer':''}" aria-labelledby="dialog-title"><form id="modal-form"><div class="dialog-head"><div><div class="context-path">${e(subtitle)}</div><h2 id="dialog-title">${e(title)}</h2></div>${button('','close','','ghost','close')}</div><div class="dialog-body"><div id="form-error" class="form-error" role="alert"></div>${body}</div><div class="dialog-footer">${footer || button('关闭','close')}</div></form></dialog>`;
  const dialog=overlay.querySelector('dialog'); dialog.addEventListener('cancel',ev=>{ev.preventDefault();closeDialog();}); dialog.showModal();
  overlay.querySelector('form').addEventListener('submit',ev=>{ev.preventDefault();if(submit){try{submit(new FormData(ev.currentTarget));}catch(error){overlay.querySelector('#form-error').textContent=error.message;}}});
}
const submitButton = label => `<button type="submit" class="primary">${e(label)}</button>`;
function detail(id) {
  const row=visibleDevices(state).flatMap(d=>rowsForDevice(state,d.id)).find(r=>r.id===id), d=visibleDevices(state).find(d=>d.id===row?.deviceId); if(!row||!d)return;
  if(row.kind==='PLC')return plcDetail(row.id);
  const p=row.product, status=sourceStates[p.state];
  showDialog(row.name,`${processName(d.processId)} / ${d.name} / ${row.host}`,`<div class="detail-banner"><span class="software-icon ${row.kind}">${kindIcon(row.kind)}</span><div><h3>${e(row.name)}</h3><small>${kindNames[row.kind]} · ${e(row.host)} · ${e(row.ip)}</small></div></div>
  <div class="notice-inline ${row.report==='状态未知'?'amber':''}">${row.report==='状态未知'?'超过五分钟未收到新报告，当前运行状态未知。下方保留最后上报事实。':row.report==='尚未接入'?'软件已关联此设备，等待现场程序完成接入。':'现场事实来自当前软件安装身份的有效上报。'}</div>
  ${fact('实际安装版本',row.version??'尚未上报')}${fact('最近运行记录',row.running)}${fact('最后上报时间',row.reported)}${fact('未上报时长',row.elapsed)}${fact('接入状态',row.access?'允许 API 接入':'尚未接入')}<hr><div class="panel-head"><h3>可用正式版本</h3>${pill(status.label,status.tone)}</div>${fact('最新版本',p.version??'暂无')}${fact('正式 Tag',p.tag??'暂无')}${fact('GitLab 项目',p.project||'待关联')}<p class="muted">${e(p.note||'配置 GitLab 来源后查看正式版本。')}</p><div class="action-links">${button('查看发布来源','source',p.id,'link')}${can(state,'access.manage',d.id)?button('接入配置','access',row.id,'link'):''}</div>`,`${button('关闭','close')}${row.update&&can(state,'deployment.create',d.id)?button('选择此设备更新','update',row.id,'primary'):''}`,true);
}
function plcDetail(id, channel='Test') {
  const archive=state.archives.find(a=>a.id===id);
  if(archive&&visibleDevices(state).some(d=>d.id===archive.deviceId)) plcLibrary(archive.productId,id,channel);
}
function plcLibrary(productId, archiveId='', channel='Test') {
  const p=visibleProducts().find(x=>x.id===productId), archive=state.archives.find(a=>a.id===archiveId&&a.productId===productId), d=visibleDevices(state).find(d=>d.id===archive?.deviceId);if(!p||p.kind!=='PLC')return;
  const versions=p.versions.filter(v=>v.state===channel), context=d?`${d.name} / ${d.controllers.find(c=>c.id===archive.controllerId)?.name}`:'共享程序版本库';
  const key=p.id+'|'+(d?archiveId:'');
  showDialog(p.name,context,`<div class="detail-banner"><span class="software-icon PLC">${icon('cpu')}</span><div><h3>${e(p.name)}</h3><small>PLC 程序 · ${relatedDevices(state,p.id).length} 台关联机台</small></div></div><div class="notice-inline">同一程序库可用于多台 PLC，各台控制器分别保留现场确认版本。转正式不会自动下装。</div>${d?fact('当前控制器确认使用',archive.confirmed??'尚未确认'):''}<div class="action-links">${button('查看全部关联机台','source-devices',p.id,'link')}</div><div class="detail-tabs">${button('测试版本','library-test',key,channel==='Test'?'active':'')}${button('正式版本','library-formal',key,channel==='Formal'?'active':'')}</div>
  ${versions.map(v=>`<article class="version-record"><div class="version-record-head"><strong>${e(v.version)} ${pill(v.state==='Test'?'待现场验证':'正式归档',v.state==='Test'?'amber':'green')}</strong>${v.state==='Test'&&d&&canManageProgram(state,p.id,'plc.publish')?button('验证并转正式','publish',archiveId+'|'+v.version,'small'):''}</div><p>${e(v.summary)}</p><small>${e(v.file)} · ${e(v.size)} · ${e(v.time)}</small>${v.conclusion?`<div class="muted-box source-detail">验证结论：${e(v.conclusion)}<br>发布人：${e(v.publisher??'演示工程师')} · ${e(v.publishedAt??v.time)}</div>`:''}<div class="action-links">${button('下载归档','prototype-download','','link')}</div></article>`).join('')||empty(channel==='Test'?'暂无测试版本':'暂无正式版本',channel==='Test'?'上传归档后，程序进入测试版本。':'人员验证并转正式后在此显示。')}
  ${!d&&versions.some(v=>v.state==='Test')?'<p class="page-caption">从关联机台选择实际验证的 PLC，再填写现场验证记录。</p>':''}<div class="action-links">${d&&can(state,'access.manage',d.id)?button('API 上传接入方式','plc-api',archiveId,'link','key'):''}</div>`,`${button('关闭','close')}${d&&canManageProgram(state,p.id,'plc.upload')?button('上传测试归档','upload-plc',archiveId,'primary','upload'):''}`,true);
}

function publishDialog(value) {
  const [id,version]=value.split('|'),archive=state.archives.find(a=>a.id===id),d=visibleDevices(state).find(d=>d.id===archive?.deviceId);if(!d||!canManageProgram(state,archive.productId,'plc.publish'))return;
  const fixed={archiveId:id,version,role:state.role};
  showDialog('PLC 现场验证与转正式',`${d.name} / ${d.controllers.find(c=>c.id===archive.controllerId)?.name} / ${version}`,`<div class="notice-inline">固定本程序库及测试版本，记录在当前 PLC 上完成的验证。转正式后关联机台都可查看此版本，但各台确认使用版本保持不变。</div><div class="field-group"><label>验证对象<div class="readonly-field">${e(d.name)} · ${e(d.controllers.find(c=>c.id===archive.controllerId)?.name)} · ${e(product(archive.productId).name)} · ${e(version)}</div></label><label>现场验证记录<textarea name="verification" required placeholder="说明测试环境、验证项目和现场结果"></textarea></label><label>测试结论<textarea name="conclusion" required placeholder="填写本次程序可转为正式归档的结论"></textarea></label><label class="checkbox"><input type="checkbox" name="confirmed" required>我已核对该 PLC 和程序版本，完成现场验证。</label></div><p class="page-caption">原型操作：仅改变演示归档状态，不上传文件、不控制设备。</p>`,`${button('取消','close')}${submitButton('转为正式归档')}`,false,form=>{if(state.role!==fixed.role)throw new Error('人员已变化，请重新核实');publishArchive(state,{...fixed,verification:String(form.get('verification')),conclusion:String(form.get('conclusion')),confirmed:form.has('confirmed')});closeDialog();render();plcDetail(id,'Formal');toast('原型：共享程序版本已转正式，各台 PLC 的现场确认版本保持原值。');});
}
function sourceDialog(id) {
  const p=visibleProducts().find(p=>p.id===id);if(!p)return;if(p.kind==='PLC')return plcLibrary(id);const status=sourceStates[p.state];
  showDialog(p.name+' · 版本',kindNames[p.kind]+' / 共享版本库',
  `<div class="panel-head"><h3>正式版本</h3>${pill(status.label,status.tone)}</div>${p.version?`<article class="version-record"><div class="version-record-head"><strong>${e(p.version)}</strong>${pill('可用正式版','green')}</div><p>${e(p.note||'本演示版本暂无变更说明。')}</p><small>${e(p.tag)} · ${e(p.time||'演示版本')}</small><div class="action-links">${button('下载程序包','prototype-download','','link')}${button('查看关联机台','focus-software',id,'link')}</div></article>`:empty('暂无可用正式版本',status.description)}
  ${p.state==='failed'?`<div class="notice-inline amber source-detail">${e(status.description)}</div>`:''}<details class="source-fold"><summary>GitLab 来源</summary>${fact('配置连接','厂内 GitLab（演示占位）')}${fact('项目',p.project||'待关联')}${fact('产物',p.package||'待选择')}${fact('最近核对',p.time||'尚未同步')}<p class="page-caption">受保护正式 Tag、成功流水线及发布清单核验通过，包副本就绪后显示为正式版。</p></details>
  <p class="page-caption">原型只提供当前版本示例；完整发布历史在正式实现中按需查询。</p><details class="prototype-controls"><summary>原型状态演示</summary><div class="detail-tabs">${Object.entries(sourceStates).filter(([key])=>key!=='unbound').map(([key,v])=>button(v.label,'source-state',id+'|'+key,'small')).join('')}</div></details>`,`${button('关闭','close')}${can(state,'source.manage')?(p.project?button('演示导入新 Tag','import',id,'primary','refresh'):button('关联 GitLab 项目','bind-source',id,'primary','git')):''}`,true);
}
function bindSource(id) {
  const p=product(id);if(!p||!can(state,'source.manage'))return;
  showDialog('关联 GitLab 来源',p.name,`<div class="notice-inline">管理员配置服务器和凭据后，工程人员从允许的项目中选择。秘密不会进入此表单。</div><div class="field-group"><label>服务器连接<div class="readonly-field">厂内 GitLab · 演示占位</div></label><label>允许关联的项目<select name="project"><option value="demo / inspection-collector">demo / inspection-collector</option><option value="demo / assembly-control">demo / assembly-control</option><option value="demo / vision-inspection">demo / vision-inspection</option></select></label><label>产物名称<input name="package" required value="${e(p.package||'inspection-collector')}" /></label><label>当前软件<div class="readonly-field">${e(p.name)} · ${kindNames[p.kind]}</div></label></div><p class="page-caption">首次关联后进入等待正式发布状态；不会替设备安装。</p>`,`${button('取消','close')}${submitButton('关联并等待同步')}`,false,form=>{if(!can(state,'source.manage'))throw new Error('来源管理权限已变化');p.project=String(form.get('project'));p.package=String(form.get('package'));p.state='waiting';closeDialog();render();sourceDialog(id);});
}
function addSoftware(deviceId, initialKind='UpperComputer') {
  const d=visibleDevices(state).find(x=>x.id===deviceId);if(!d||!can(state,'software.add',d.id))return;
  const isPlc=initialKind==='PLC', candidates=visibleProducts().filter(p=>p.kind===initialKind), hardware=isPlc?d.controllers:d.hosts, fixedRole=state.role;
  showDialog(isPlc?'添加 PLC 与程序':'添加设备软件',`${processName(d.processId)} / ${d.name}`,`<div class="form-grid"><label class="wide">当前设备<div class="readonly-field">${e(d.name)} · ${e(processName(d.processId))}</div></label><label class="wide">软件类型<select name="kind" id="add-kind" data-device="${d.id}">${Object.entries(kindNames).map(([id,name])=>`<option value="${id}" ${id===initialKind?'selected':''}>${name}</option>`).join('')}</select></label><label class="wide">${isPlc?'程序库':'软件'}<select name="product" id="product-choice"><option value="new">新建，并关联当前设备</option>${candidates.map(p=>`<option value="${p.id}">使用已有：${e(p.name)}</option>`).join('')}</select></label><label class="wide" data-new-product>新${isPlc?'程序':'软件'}名称<input name="name" placeholder="填写名称，内部编号自动生成" /></label><label class="wide">${isPlc?'所在 PLC':'所在工控机'}<select name="host" id="host-choice"><option value="new">同时添加${isPlc?' PLC':'工控机'}</option>${hardware.map(h=>`<option value="${h.id}">${e(h.name)} · ${e(h.ip)}</option>`).join('')}</select></label><label data-new-host>新${isPlc?' PLC':'工控机'}名称<input name="hostName" /></label><label data-new-host>IP 地址（可选）<input name="ip" placeholder="登记地址" /></label><p class="form-hint wide">${isPlc?'同一程序库可关联多台 PLC；每台分别记录确认使用版本。':'同一软件可关联多台设备，也可在一台设备的不同工控机安装。'}</p></div>`,`${button('取消','close')}${submitButton('添加并关联设备')}`,false,form=>{
    if(state.role!==fixedRole||!can(state,'software.add',d.id))throw new Error('人员或权限已变化');
    const selected=String(form.get('product')), selectedHost=String(form.get('host')), name=String(form.get('name')??'').trim(), hostName=String(form.get('hostName')??'').trim();
    let p=candidates.find(p=>p.id===selected), h=hardware.find(h=>h.id===selectedHost);
    if(selected!=='new'&&!p)throw new Error('所选软件已不在可用范围');
    if(!p&&!name)throw new Error('请填写新软件或程序名称');
    if(!h&&!hostName)throw new Error('请填写新硬件名称或选择已有硬件');
    if(p&&h&&(isPlc?state.archives.some(a=>a.deviceId===d.id&&a.productId===p.id&&a.controllerId===h.id):state.installations.some(i=>i.deviceId===d.id&&i.productId===p.id&&i.hostId===h.id)))throw new Error('该位置已关联此软件，请查看现有记录');
    const n=state.sequence++;
    if(!p){p={id:`product-new-${n}`,name,kind:initialKind,source:isPlc?'程序归档':'GitLab',versions:[],project:'',package:'',state:'unbound',version:null,tag:null,revision:1};state.products.push(p);}
    if(!h){h={id:`hardware-new-${n}`,name:hostName,ip:String(form.get('ip')||'—')};hardware.push(h);}
    if(isPlc)state.archives.push({id:`archive-new-${n}`,deviceId:d.id,controllerId:h.id,productId:p.id,confirmed:null});
    else state.installations.push({id:`installation-new-${n}`,deviceId:d.id,hostId:h.id,productId:p.id,version:null,running:'—',report:'尚未接入',reported:'—',elapsed:'—',access:false});
    selectScope(state,{deviceId:d.id});closeDialog();render();toast('原型：已关联到当前设备，共用版本库，保留独立现场事实。');
  });
  refreshAddFields();
}
function refreshAddFields() {
  const productChoice=overlay.querySelector('#product-choice'),hostChoice=overlay.querySelector('#host-choice');
  if(productChoice)overlay.querySelectorAll('[data-new-product]').forEach(x=>{x.hidden=productChoice.value!=='new';});
  if(hostChoice)overlay.querySelectorAll('[data-new-host]').forEach(x=>{x.hidden=hostChoice.value!=='new';});
}
function newDevice() {
  if(!can(state,'asset.create'))return;const fixedProcess=state.processId;
  showDialog('新增设备',processName(fixedProcess),`<div class="field-group"><label>所属工序<select name="processId">${state.processes.map(p=>`<option value="${p.id}" ${p.id===fixedProcess?'selected':''}>${e(p.name)}</option>`).join('')}</select></label><label>设备名称<input name="name" required maxlength="128" placeholder="填写现场设备名称" /></label><label>平台设备编号<div class="readonly-field">保存时自动生成</div></label><label>现场资产编号（可选）<input name="assetNumber" maxlength="64" placeholder="工厂已有编号，可不填写" /></label></div>`,`${button('取消','close')}${submitButton('创建设备')}`,false,form=>{createDevice(state,{processId:String(form.get('processId')),name:String(form.get('name')),assetNumber:String(form.get('assetNumber'))});closeDialog();render();toast('原型：设备编号已自动生成，可继续添加工控机与程序。');});
}
function updateDialog(id) {
  const row=visibleDevices(state).flatMap(d=>rowsForDevice(state,d.id)).find(r=>r.id===id);
  if(!row||row.kind==='PLC')return;
  focusScope({productId:row.productId});
  if(!updateEligibility(state,row,row.latest)){selectTarget(state,id,true);render();confirmUpdate();}
}
function confirmUpdate() {
  const p=visibleProducts().find(p=>p.id===state.productId);if(!p||p.kind==='PLC'||!state.selectedTargets.length)return;
  const fixed={productId:p.id,version:p.version,role:state.role,installationIds:[...state.selectedTargets]}, chosen=relatedTargets(state,p.id).filter(t=>fixed.installationIds.includes(t.id));
  showDialog('确认更新',p.name+' → '+p.version,`<div class="notice-inline">仅更新下面 ${new Set(chosen.map(t=>t.deviceId)).size} 台设备的 ${chosen.length} 个安装位置。</div><ul class="chosen-targets">${chosen.map(t=>`<li><strong>${e(t.device.name)}</strong><span>${e(t.host)} · ${e(t.version)} → ${e(p.version)}</span></li>`).join('')}</ul><label>更新原因<textarea name="reason" required placeholder="填写本次更新原因"></textarea></label><p class="page-caption">这是操作原型，不执行真实安装。正式实现沿用时段、批次及数据保护核验。</p>`,`${button('返回选择','close')}${submitButton('创建演示更新任务')}`,false,form=>{const tasks=createUpdates(state,{...fixed,reason:String(form.get('reason')??'')});state.selectedTargets=[];closeDialog();render();toast(`原型：${tasks.length} 个更新任务已创建，进度显示在原机台行。`);});
}
function taskDetail(id) {
  const t=state.tasks.find(t=>t.id===id),d=visibleDevices(state).find(d=>d.id===t?.deviceId);if(!d)return;
  showDialog('设备更新任务',`${d.name} / ${product(t.productId).name}`,`${fact('目标设备',d.name)}${fact('所在工控机',t.host??'主控工控机')}${fact('目标正式版本',t.version)}${fact('当前状态',t.state)}${fact('完成数量',t.progress)}<div class="notice-inline source-detail">任务回执与现场安装上报分别记录。暂停不会停止已获许可的现场进程。</div>`,`${button('关闭','close')}${can(state,'deployment.control',d.id)&&t.state!=='已完成'?button(t.state==='已暂停'?'继续等待领取':'暂停后续执行','task-control',id,'primary'):''}`,true);
}
function accessDialog(id) {
  const i=state.installations.find(i=>i.id===id),d=visibleDevices(state).find(d=>d.id===i?.deviceId);if(!d||!can(state,'access.manage',d.id))return;
  showDialog('现场软件接入',`${d.name} / ${product(i.productId).name}`,`<div class="notice-inline">将当前对象的受限接入材料交给对应现场程序。首次登记后使用独立凭据，不共享人员账号。</div>${fact('设备',d.name)}${fact('软件',product(i.productId).name)}${fact('工控机',d.hosts.find(h=>h.id===i.hostId)?.name??'待补充归属')}${fact('接入状态',i.access?'已接入':'等待首次登记')}<hr><div class="steps"><div class="step complete">确认当前对象<span>无需手填设备 UUID</span></div><div class="step">生成受限材料<span>限定有效期和登记数量</span></div><div class="step">现场程序首次登记<span>身份不能改绑其他设备</span></div><div class="step">独立上报与更新<span>可单独撤销及恢复</span></div></div><div class="muted-box">本原型不生成真实秘密、不变更凭据。恢复保留原身份与安装履历，并使旧凭据失效。</div>`,button('关闭','close'),true);
}
function uploadPlc(id) {
  const a=state.archives.find(a=>a.id===id),d=visibleDevices(state).find(d=>d.id===a?.deviceId);if(!d||!can(state,'plc.upload',d.id))return;
  const program=product(a.productId);if(!canManageProgram(state,program.id,'plc.upload'))return;const next=program.versions.length?`${Number(program.versions[0].version.split('.')[0])}.${Number(program.versions[0].version.split('.')[1])+1}.0`:'1.0.0';
  showDialog('上传 PLC 测试归档',`${d.name} / ${d.controllers.find(c=>c.id===a.controllerId)?.name} / ${program.name}`,`<div class="notice-inline">文件接收和摘要校验由后端实现。本原型只记录文件名和演示状态，不读取或上传文件内容。</div><div class="field-group"><label>归档对象<div class="readonly-field">${e(d.name)} / ${e(program.name)}</div></label><label>程序压缩包<input name="file" type="file" accept=".zip,.7z" required /></label><label>变更说明<textarea name="summary" required></textarea></label><label>演示生成版本<div class="readonly-field">${next} · 测试版</div></label></div>`,`${button('取消','close')}${submitButton('演示归档为测试版')}`,false,form=>{if(!can(state,'plc.upload',d.id))throw new Error('当前无上传权限');const file=form.get('file');if(!(file instanceof File)||!file.name)throw new Error('请选择归档文件');program.versions.unshift({version:next,state:'Test',file:file.name,size:(file.size/1024/1024).toFixed(2)+' MB',summary:String(form.get('summary')),time:'本次原型操作',conclusion:null});closeDialog();render();plcDetail(id);toast('原型：仅登记演示文件资料，未上传任何内容。');});
}
function accountMenu() {
  if(document.querySelector('.user-menu'))return document.querySelector('.user-menu').remove();
  const menu=document.createElement('section');menu.className='user-menu';menu.setAttribute('aria-label','账户菜单');menu.innerHTML=`<div class="user-heading"><strong>演示人员</strong><small>${roles[state.role].name} · ${roles[state.role].scope}</small></div>${button('本人密码','password','','','key')}${button('查看当前权限','my-permissions','','','shield')}`;root.append(menu);
}
document.addEventListener('click',event=>{
  const b=event.target.closest('[data-action]');if(!b||b.disabled)return;const a=b.dataset.action,id=b.dataset.id;
  if(a==='close')return closeDialog();
  if(a==='page'){closeDialog();if(id==='settings'&&!can(state,'identity.manage'))return;state.page=id;render();}
  else if(a==='reset'){closeDialog();catalogInput={query:'',kind:'all'};state=initialState();render();toast('原型已重置。真实系统及数据未受影响。');}
  else if(a==='device')focusScope({deviceId:id});
  else if(a==='process')focusScope({processId:id||null});
  else if(a==='focus-software'||a==='source-devices'||a==='update-product')focusScope({productId:id});
  else if(a==='back-scope')focusScope(state.previousScope??{});
  else if(a==='find-software')catalogDialog();
  else if(a==='expand'){state.expanded=state.expanded.includes(id)?state.expanded.filter(x=>x!==id):[...state.expanded,id];render();}
  else if(a==='list-page'){state.listPage=Number(id);render();}
  else if(a==='context-tasks')contextTasks();
  else if(a==='group-tasks')contextTasks(id.split('|'));
  else if(a==='confirm-update')confirmUpdate();
  else if(a==='hardware'){const d=visibleDevices(state).find(d=>d.id===id);if(d)showDialog('设备资料',d.name,hardwareView(d)+accessView(d),button('关闭','close'),true);}
  else if(a==='detail')detail(id);
  else if(a==='plc-test'||a==='plc-formal')plcDetail(id,a==='plc-test'?'Test':'Formal');
  else if(a==='publish')publishDialog(id);
  else if(a==='source')sourceDialog(id);
  else if(a==='bind-source')bindSource(id);
  else if(a==='source-state'){const [pid,next]=id.split('|'),p=visibleProducts().find(x=>x.id===pid);if(p){p.state=next;render();sourceDialog(pid);}}
  else if(a==='import'){try{importRelease(state,id);render();sourceDialog(id);toast('原型：新正式 Tag 已导入，所有设备实际安装版本保持原值。');}catch(error){toast(error.message);}}
  else if(a==='catalog-page'){state.catalog.page=Number(id);catalogDialog();}
  else if(a==='library-test'||a==='library-formal'){const [pid,archiveId]=id.split('|');plcLibrary(pid,archiveId,a==='library-test'?'Test':'Formal');}
  else if(a==='add-software')addSoftware(id);
  else if(a==='add-plc')addSoftware(id,'PLC');
  else if(a==='add-host'){const d=visibleDevices(state).find(d=>d.id===id);if(!d||!can(state,'software.add',id))return;showDialog('添加工控机',d.name,`<div class="field-group"><label>工控机名称<input name="name" required /></label><label>IP 地址（可选）<input name="ip" /></label></div>`,`${button('取消','close')}${submitButton('添加到当前设备')}`,false,form=>{if(!can(state,'software.add',id))throw new Error('无操作权限');d.hosts.push({id:'prototype-host-'+state.sequence++,name:String(form.get('name')),ip:String(form.get('ip')||'—')});closeDialog();render();});}
  else if(a==='create-device')newDevice();
  else if(a==='create-process'){if(!can(state,'asset.create'))return;showDialog('新增工序','虚构演示厂区',`<div class="field-group"><label>工序名称<input name="name" required /></label><label>内部代码<div class="readonly-field">自动生成，无需人工填写</div></label></div>`,`${button('取消','close')}${submitButton('创建工序')}`,false,form=>{if(!can(state,'asset.create'))throw new Error('无操作权限');const id='prototype-process-'+state.sequence++;state.processes.push({id,name:String(form.get('name'))});selectScope(state,{processId:id});closeDialog();render();toast('原型：工序已创建，可在该工序下新增设备。');});}
  else if(a==='update')updateDialog(id);
  else if(a==='task')taskDetail(id);
  else if(a==='task-control'){const t=state.tasks.find(t=>t.id===id);if(t&&can(state,'deployment.control',t.deviceId)){t.state=t.state==='已暂停'?'等待领取':'已暂停';render();taskDetail(id);}}
  else if(a==='access')accessDialog(id);
  else if(a==='upload-plc')uploadPlc(id);
  else if(a==='plc-api'){const archive=state.archives.find(x=>x.id===id);if(!archive||!can(state,'access.manage',archive.deviceId))return;showDialog('PLC API 上传',product(archive.productId).name,`<div class="notice-inline">接口契约待实现；本原型没有可调用的上传端点。</div><div class="steps"><div class="step">创建受限上传凭据<span>固定程序库与允许的验证对象</span></div><div class="step">工具上传程序归档<span>登记资料、传输、摘要校验</span></div><div class="step">进入测试版<span>凭据无发布权限</span></div><div class="step">人员现场验证<span>在网页转为正式归档</span></div></div><div class="muted-box">不使用人员 Cookie 或现场软件的上报凭据。原型不显示或生成真实 Token。</div>`,button('关闭','close'),true);}
  else if(a==='prototype-download')toast('原型没有真实程序文件，正式实现后下载需当前授权并记录审计。');
  else if(a==='settings-people'){render();}
  else if(a==='connections'){if(!can(state,'identity.manage'))return;showDialog('GitLab 连接','系统管理',`${fact('连接名称','厂内 GitLab（演示占位）')}${fact('服务地址','部署时配置')}${fact('凭据','不在原型中读取或保存')}<div class="notice-inline source-detail">真实连接由部署配置维护。网页连接管理尚待实现；软件关联从“版本与来源”进入。</div>`,button('关闭','close'),true);}
  else if(a==='role-detail'){selectedRole=id;render();}
  else if(a==='preview-role'){closeDialog();catalogInput={query:'',kind:'all'};selectRole(state,id);state.page='workbench';render();}
  else if(a==='account')accountMenu();
  else if(a==='password'){document.querySelector('.user-menu')?.remove();showDialog('修改本人密码','账户设置',`<div class="notice-inline">原型不会提交密码或修改真实账号。请勿填写真实密码。</div><div class="field-group"><label>当前密码<input type="password" name="current" autocomplete="off" /></label><label>新密码<input type="password" name="new" autocomplete="off" /></label><label>确认新密码<input type="password" name="confirmation" autocomplete="off" /></label></div>`,`${button('取消','close')}${submitButton('查看提交反馈')}`,false,()=>{closeDialog();toast('原型：密码操作位于账户菜单，未修改真实密码。');});}
  else if(a==='my-permissions'){document.querySelector('.user-menu')?.remove();showDialog('我的有效权限',roles[state.role].scope,`<div class="notice-inline">权限预览：实际后台授权模型尚待实现。</div>${actionRows.map(([label,action])=>fact(label,action==='read'||roles[state.role].actions.includes(action)?'允许':'未授予')).join('')}`,button('关闭','close'),true);}
});
document.addEventListener('change',event=>{
  const x=event.target;
  if(x.id==='role-select'){closeDialog();catalogInput={query:'',kind:'all'};selectRole(state,x.value);render();}
  else if(x.id==='scope-picker'){const [type,id]=x.value.split('|');focusScope(type==='device'?{deviceId:id}:{processId:id||null});}
  else if(x.id==='add-kind')addSoftware(x.dataset.device,x.value);
  else if(x.name==='workspace-target'){
    try{selectTarget(state,x.value,x.checked);const count=document.querySelector('#selected-count');if(count)count.textContent=`已选 ${state.selectedTargets.length} 个安装位置（含其他页）`;document.querySelector('[data-action="confirm-update"]').disabled=!state.selectedTargets.length;}
    catch(error){toast(error.message);render();}
  }
  else if(x.id==='product-choice'||x.id==='host-choice')refreshAddFields();
});
document.addEventListener('submit',event=>{
  if(event.target.id==='workspace-search'){
    event.preventDefault();const form=new FormData(event.target);state.query=String(form.get('query')??'');state.category=String(form.get('category')??'all');state.listPage=1;state.selectedTargets=[];state.expanded=[];render();
  }
});
document.addEventListener('input',event=>{if(overlay.querySelector('[aria-label="可选软件目录"]')&&['query','kind'].includes(event.target.name))catalogInput[event.target.name]=event.target.value;});
document.addEventListener('change',event=>{if(overlay.querySelector('[aria-label="可选软件目录"]')&&event.target.name==='kind')catalogInput.kind=event.target.value;});
document.addEventListener('keydown',event=>{if(event.key==='Escape')document.querySelector('.user-menu')?.remove();});
render();
