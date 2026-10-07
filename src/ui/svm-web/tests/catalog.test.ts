import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, nextTick, ref } from 'vue'
import { createPinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import SitePage from '../src/pages/SitePage.vue'
import SoftwarePage from '../src/pages/SoftwarePage.vue'
import SoftwareGrantEditor from '../src/components/SoftwareGrantEditor.vue'
import { useSession } from '../src/session'
import { pendingWriteKey } from '../src/mutation'
import type { Permission } from '../src/api'
const disposals: (() => void)[] = []
afterEach(() => { disposals.splice(0).forEach(dispose => dispose()); vi.unstubAllGlobals(); document.body.innerHTML = '' })
const subject = '11111111-1111-4111-8111-111111111111', software = '22222222-2222-4222-8222-222222222222'
const snapshot = { authenticated: true, subjectId: subject, employeeNo: 'FIXTURE', displayName: '夹具人员', mustChangePassword: false,
  permissions: [{ softwareId: null, operation: 'asset.read' }, { softwareId: null, operation: 'software.create' }], csrfToken: 'fixture', serverTime: '2026-10-07T00:00:00Z' }
function label(text: string) { return [...(document.querySelector('dialog') ?? document).querySelectorAll('label')].find(x => x.textContent?.startsWith(text))! }
function fill(text: string, value: string) { const input = label(text).querySelector('input,select') as HTMLInputElement; input.value = value; input.dispatchEvent(new Event(input.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })) }
async function click(text: string) { const button = [...document.querySelectorAll('button')].find(x => x.textContent?.trim() === text)!; expect(button).toBeTruthy(); button.click(); await nextTick() }
async function mountPage(component: typeof SitePage | typeof SoftwarePage) {
  vi.spyOn(HTMLDialogElement.prototype, 'showModal').mockImplementation(function () { this.open = true })
  vi.spyOn(HTMLDialogElement.prototype, 'close').mockImplementation(function () { this.open = false })
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/page', component }, { path: '/other', component: { template: '<p>other</p>' } }] })
  await router.push('/page'); const pinia = createPinia(); useSession(pinia).current = snapshot
  const pending = ref(false), root = document.createElement('div'); document.body.appendChild(root)
  const app = createApp({ template: '<RouterView />' }).use(pinia).use(router).provide(pendingWriteKey, pending); app.mount(root); disposals.push(() => app.unmount())
  return { root, router, pending }
}
describe('catalog interactions', () => {
  it('requires explicit factory configuration and never fabricates a hierarchy', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ code: 'CONFIGURATION_INVALID' }, { status: 500 })))
    const { root } = await mountPage(SitePage)
    await vi.waitFor(() => expect(root.textContent).toContain('服务配置不可用'))
    expect(root.textContent).toContain('现场台账暂不可用'); expect(root.textContent).not.toContain('宜宾'); expect(root.querySelector('.site-layout')).toBeNull()
  })
  it('keeps the exact catalog write and blocks navigation until manual verification', async () => {
    const fetch = vi.fn(async (path: string, input?: RequestInit) => {
      if (input?.method === 'POST') { if (fetch.mock.calls.filter(c => c[1]?.method === 'POST').length === 1) throw new Error('fixture disconnected'); return Response.json({ id: software, code: 'S', name: '夹具软件', category: 'Vision', revision: 1 }) }
      return Response.json(path.includes('/session') ? snapshot : { items: [], nextCursor: null })
    }); vi.stubGlobal('fetch', fetch)
    const { root, router, pending } = await mountPage(SoftwarePage)
    await click('登记软件'); fill('软件代码', 'S'); fill('软件名称', '夹具软件'); fill('软件分类', 'Vision'); await click('保存')
    await vi.waitFor(() => expect(root.textContent).toContain('核实原操作')); expect(pending.value).toBe(true)
    await router.push('/other'); expect(router.currentRoute.value.path).toBe('/page')
    expect(label('软件名称').closest('fieldset')!.disabled).toBe(true)
    const first = fetch.mock.calls.find(c => c[1]?.method === 'POST')!; await click('核实原操作')
    await vi.waitFor(() => expect(root.querySelector('dialog')).toBeNull())
    const writes = fetch.mock.calls.filter(c => c[1]?.method === 'POST'); expect(writes).toHaveLength(2)
    expect(writes[1]?.[0]).toBe(first[0]); expect(writes[1]?.[1]?.body).toBe(first[1]?.body); expect(writes[1]?.[1]?.headers).toEqual(first[1]?.headers)
    expect(pending.value).toBe(false); expect(localStorage.length + sessionStorage.length).toBe(0)
  })
  it('does not implicitly grant all operations when selecting software', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ items: [{ id: software, code: 'S', name: '夹具软件', category: 'Vision' }], nextCursor: null, softwareOperations: ['software.read', 'instance.manage'] })))
    const grants = ref<Permission[]>([{ softwareId: 'legacy', operation: 'software.read' }])
    const component = defineComponent(() => () => h(SoftwareGrantEditor, { modelValue: grants.value, 'onUpdate:modelValue': value => { grants.value = value } }))
    const root = document.createElement('div'); document.body.appendChild(root); const app = createApp(component); app.mount(root); disposals.push(() => app.unmount())
    await vi.waitFor(() => expect(root.querySelectorAll('option')).toHaveLength(2))
    fill('选择授权软件', software); await nextTick(); expect(grants.value).toHaveLength(1)
    const check = label('查看软件').querySelector('input')!; check.checked = true; check.dispatchEvent(new Event('change', { bubbles: true })); await nextTick()
    expect(grants.value).toEqual([{ softwareId: 'legacy', operation: 'software.read' }, { softwareId: software, operation: 'software.read' }])
    expect(label('维护实例与设备映射').querySelector('input')!.checked).toBe(false)
  })
  it('retains existing software grants when candidate loading fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ code: 'CONFIGURATION_INVALID' }, { status: 500 })))
    const grants = ref<Permission[]>([{ softwareId: software, operation: 'software.read' }]); const emit = vi.fn()
    const root = document.createElement('div'); document.body.appendChild(root); const app = createApp(SoftwareGrantEditor, { modelValue: grants.value, 'onUpdate:modelValue': emit }); app.mount(root); disposals.push(() => app.unmount())
    await vi.waitFor(() => expect(root.textContent).toContain('既有软件授权仍保留'))
    expect(emit).not.toHaveBeenCalled(); expect(root.textContent).toContain(software); expect(root.querySelector('select')).toBeNull()
  })
})
