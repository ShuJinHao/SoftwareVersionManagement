import { describe, expect, it } from 'vitest'
import {
  initialState, roles, visibleDevices, can, selectRole, rowsForDevice,
  createDevice, publishArchive, importRelease, catalogPage, relatedDevices, relatedTargets, createUpdates, latestVersion,
  selectScope, workspaceDevices, workspaceRows, workspaceTasks, selectTarget,
} from '../../../../design/workspace/model.js'

// Tests the isolated review prototype only; this is not production authorization evidence.
describe('设备工作区原型事实边界', () => {
  it('导入共享正式版本后两台设备仍保留各自安装事实', () => {
    const state = initialState()
    const installations = structuredClone(state.installations)
    importRelease(state, 'upper')
    expect(rowsForDevice(state, 'device-01').find(x => x.productId === 'upper')?.latest).toBe('1.3.1')
    expect(rowsForDevice(state, 'device-02').find(x => x.productId === 'upper')?.latest).toBe('1.3.1')
    expect(state.installations).toEqual(installations)
  })

  it('PLC 转正式保留确认版本并隔离同设备的另一台 PLC', () => {
    const state = initialState()
    const other = structuredClone(state.archives[1])
    publishArchive(state, { archiveId: 'archive-1', version: '1.1.0', conclusion: '演示通过', verification: '演示联锁检查', confirmed: true })
    const row = rowsForDevice(state, 'device-01').find(x => x.id === 'archive-1')
    expect(row?.latest).toBe('1.1.0')
    expect(row?.version).toBe('1.0.0')
    expect(row?.update).toBe(false)
    expect(state.archives[1]).toEqual(other)
    expect(rowsForDevice(state, 'device-02').find(x => x.id === 'archive-3')?.latest).toBe('1.1.0')
    expect(state.archives[2].confirmed).toBeNull()
  })

  it('没有现场确认或重复发布时不推进 PLC 归档', () => {
    const state = initialState()
    const input = { archiveId: 'archive-1', version: '1.1.0', conclusion: '演示通过', verification: '演示检查', confirmed: false }
    expect(() => publishArchive(state, input)).toThrow('请填写现场验证记录')
    expect(state.products.find(p => p.id === 'plc-main')!.versions[0].state).toBe('Test')
    publishArchive(state, { ...input, confirmed: true })
    expect(() => publishArchive(state, { ...input, confirmed: true })).toThrow('测试版本已变化')
  })

  it('现场范围收窄时清除原对象选择并过滤清单与数量', () => {
    const state = initialState()
    state.page = 'settings'
    selectRole(state, 'viewer')
    expect(visibleDevices(state).map(x => x.id)).toEqual(['device-03', 'device-04'])
    expect(state.deviceId).toBeNull()
    expect(state.processId).toBe('inspection')
    expect(state.page).toBe('workbench')
    expect(rowsForDevice(state, 'device-01')).toEqual([])
    expect(can(state, 'software.add', 'device-03')).toBe(false)
  })

  it('工程师的设备权限不隐含共享来源管理或越工序发布', () => {
    const state = initialState()
    selectRole(state, 'engineer')
    expect(can(state, 'software.add', 'device-01')).toBe(true)
    expect(can(state, 'software.add', 'device-03')).toBe(false)
    expect(() => importRelease(state, 'upper')).toThrow('无权管理发布来源')
    state.archives[0].deviceId = 'device-03'
    expect(() => publishArchive(state, { archiveId: 'archive-1', version: '1.1.0', conclusion: '演示通过', verification: '演示检查', confirmed: true })).toThrow('无权发布')
  })

  it('运维仅可管理指定设备更新不能发布 PLC', () => {
    const state = initialState()
    selectRole(state, 'operator')
    expect(visibleDevices(state).map(x => x.id)).toEqual(['device-01'])
    expect(can(state, 'deployment.create', 'device-01')).toBe(true)
    expect(can(state, 'deployment.control', 'device-02')).toBe(false)
    expect(() => publishArchive(state, { archiveId: 'archive-1', version: '1.1.0', conclusion: '演示通过', verification: '演示检查', confirmed: true })).toThrow('无权发布')
  })

  it('设备编号自动生成并保留所选工序且不新增人员权限', () => {
    const state = initialState()
    const grants = structuredClone(roles)
    const first = createDevice(state, { processId: 'inspection', name: '演示新设备', assetNumber: '厂内示例号' })
    const second = createDevice(state, { processId: 'assembly', name: '演示另一设备' })
    expect(first.processId).toBe('inspection')
    expect(first.assetNumber).toBe('厂内示例号')
    expect(first.number).not.toBe(second.number)
    expect(first.number).toMatch(/^EQ-\d{6}$/)
    expect(roles).toEqual(grants)
    selectRole(state, 'viewer')
    expect(() => createDevice(state, { processId: 'inspection', name: '越权设备' })).toThrow('无权')
  })

  it('120 款软件分为六页且筛选与角色范围作用于总数和每页', () => {
    const state = initialState()
    const pages = Array.from({ length: 6 }, (_, i) => catalogPage(state, { page: i + 1 }).items)
    expect(pages.every(p => p.length === 20)).toBe(true)
    expect(new Set(pages.flat().map(p => p.id)).size).toBe(120)
    expect(catalogPage(state, { query: '119' }).items.map(p => p.name)).toEqual(['演示视觉软件 119'])
    expect(catalogPage(state, { kind: 'PLC' }).total).toBe(40)
    expect(catalogPage(state, { query: '不存在' }).items).toEqual([])
    selectRole(state, 'operator')
    expect(catalogPage(state).total).toBe(5)
    expect(catalogPage(state).items.some(p => p.id === 'pending')).toBe(false)
  })

  it('同设备包含多个上位机和 PLC，同产品关联多台且机台数量去重', () => {
    const state = initialState()
    expect(rowsForDevice(state, 'device-01').filter(r => r.kind === 'UpperComputer')).toHaveLength(2)
    expect(rowsForDevice(state, 'device-01').filter(r => r.kind === 'PLC')).toHaveLength(2)
    expect(relatedDevices(state, 'upper')).toHaveLength(2)
    expect(relatedDevices(state, 'plc-main')).toHaveLength(2)
    state.installations.push({ ...state.installations[0], id: 'another-installation', hostId: 'ipc-2' })
    expect(relatedTargets(state, 'upper')).toHaveLength(3)
    expect(relatedDevices(state, 'upper')).toHaveLength(2)
  })

  it('按安装标识更新勾选机台，不重复下发也不改变上报事实', () => {
    const state = initialState(), original = structuredClone(state.installations)
    importRelease(state, 'upper')
    const version = latestVersion(state.products.find(p => p.id === 'upper')!)
    const tasks = createUpdates(state, { productId: 'upper', version, role: 'admin', installationIds: ['installation-1', 'installation-1', 'installation-3'], reason: '演示升级' })
    expect(tasks.map(t => t.deviceId)).toEqual(['device-01', 'device-02'])
    expect(state.installations).toEqual(original)
    expect(() => createUpdates(state, { productId: 'upper', version, role: 'admin', installationIds: ['installation-1'], reason: '重复任务' })).toThrow('目标不可更新')
  })

  it('选择不可见或其他软件对象时整组拒绝，原选择不扩大到其他机台', () => {
    const state = initialState()
    importRelease(state, 'upper')
    selectRole(state, 'operator')
    const before = state.tasks.length
    const input = { productId: 'upper', version: '1.3.1', role: 'operator', installationIds: ['installation-1', 'installation-3'], reason: '演示升级' }
    expect(() => createUpdates(state, input)).toThrow('目标不可更新')
    expect(state.tasks).toHaveLength(before)
    expect(() => createUpdates(state, { ...input, installationIds: ['installation-2'] })).toThrow('目标不可更新')
    expect(createUpdates(state, { ...input, installationIds: ['installation-1'] }).map(t => t.deviceId)).toEqual(['device-01'])
  })

  it('身份或目标版本变化后拒绝旧草稿，PLC 不生成更新任务', () => {
    const state = initialState()
    const input = { productId: 'upper', version: '1.3.0', role: 'admin', installationIds: ['installation-1'], reason: '演示升级' }
    importRelease(state, 'upper')
    expect(() => createUpdates(state, input)).toThrow('目标正式版本已变化')
    expect(() => createUpdates(state, { ...input, productId: 'plc-main', version: '1.0.0' })).toThrow('目标正式版本已变化')
    selectRole(state, 'operator')
    expect(() => createUpdates(state, { ...input, version: '1.3.1' })).toThrow('人员已变化')
  })

  it('从工序和设备选择软件后显示全部授权机台，不沿用旧设备筛选', () => {
    const state = initialState()
    expect(workspaceDevices(state).map(d => d.id)).toEqual(['device-01', 'device-02'])
    selectScope(state, { deviceId: 'device-01' })
    state.query = '旧查询'
    state.listPage = 3
    selectScope(state, { productId: 'upper' })
    expect(state.previousScope).toEqual({ deviceId: 'device-01', processId: 'assembly' })
    expect(state.deviceId).toBeNull()
    expect(state.processId).toBeNull()
    expect(state.query).toBe('')
    expect(state.listPage).toBe(1)
    expect(workspaceRows(state).map(r => r.id)).toEqual(['installation-1', 'installation-3'])
    selectScope(state, state.previousScope)
    expect(workspaceDevices(state).map(d => d.id)).toEqual(['device-01'])
  })

  it('同设备同软件多处安装逐个选择，切换对象清除选择但保留任务', () => {
    const state = initialState()
    state.installations.push({ ...state.installations[0], id: 'second-position', hostId: 'ipc-2' })
    selectScope(state, { productId: 'upper' })
    selectTarget(state, 'second-position', true)
    expect(state.selectedTargets).toEqual(['second-position'])
    createUpdates(state, { productId: 'upper', version: '1.3.0', role: 'admin', installationIds: state.selectedTargets, reason: '指定位置更新' })
    selectScope(state, { deviceId: 'device-03' })
    expect(state.selectedTargets).toEqual([])
    expect(workspaceTasks(state)).toEqual([])
    selectScope(state, { deviceId: 'device-01' })
    expect(workspaceTasks(state).map(t => t.installationId)).toEqual(['second-position'])
    selectScope(state, { productId: 'upper' })
    expect(workspaceTasks(state).map(t => t.installationId)).toEqual(['second-position', 'installation-3'])
    expect(workspaceRows(state).find(t => t.id === 'second-position')?.version).toBe('1.2.4')
  })

  it('角色变化清除隐藏对象与更新选择，任务与软件候选同样按权限收窄', () => {
    const state = initialState()
    selectScope(state, { productId: 'upper' })
    selectTarget(state, 'installation-1', true)
    selectRole(state, 'viewer')
    expect(state.selectedTargets).toEqual([])
    expect(state.previousScope).toBeNull()
    expect(state.productId).toBeNull()
    expect(workspaceTasks(state)).toEqual([])
    expect(() => selectScope(state, { productId: 'upper' })).toThrow('无权')
    expect(() => selectScope(state, { deviceId: 'device-01' })).toThrow('无权')
    selectRole(state, 'operator')
    selectScope(state, { productId: 'upper' })
    expect(workspaceDevices(state).map(d => d.id)).toEqual(['device-01'])
    expect(() => selectTarget(state, 'installation-3', true)).toThrow('不可更新')
  })

  it('可见但未关联的软件保留版本入口，不能伪造机台或选择其他软件安装', () => {
    const state = initialState()
    selectScope(state, { productId: 'catalog-7' })
    expect(workspaceDevices(state)).toEqual([])
    expect(workspaceTasks(state)).toEqual([])
    expect(() => selectTarget(state, 'installation-1', true)).toThrow('不可更新')
    selectScope(state, { productId: 'plc-main' })
    expect(() => selectTarget(state, 'archive-1', true)).toThrow('不可更新')
  })

})
