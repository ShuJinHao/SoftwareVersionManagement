import { readFile, mkdir, writeFile, chmod } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const path = resolve(root, '.cache/site-local/site.json')
try {
  const [command, ...args] = process.argv.slice(2)
  if (command === 'status' && args.length === 0) {
    const value = JSON.parse(await readFile(path, 'utf8'))
    console.log(JSON.stringify({ siteId: value.siteId, siteName: value.siteName, siteTimeZone: value.siteTimeZone }))
  } else if (command === 'prepare') {
    const inputs = new Map()
    for (let i = 0; i < args.length; i += 2) {
      if (!['--id', '--name', '--time-zone'].includes(args[i]) || !args[i + 1] || inputs.has(args[i])) throw new Error('invalid arguments')
      inputs.set(args[i], args[i + 1])
    }
    const siteId = inputs.get('--id'), siteName = inputs.get('--name'), siteTimeZone = inputs.get('--time-zone')
    if (!siteId || !/^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i.test(siteId) || /^0{8}(-0{4}){3}-0{12}$/.test(siteId) || !siteName?.trim() || siteName.length > 128 || !siteTimeZone) throw new Error('explicit site required')
    new Intl.DateTimeFormat('en', { timeZone: siteTimeZone })
    let current
    try { current = JSON.parse(await readFile(path, 'utf8')) } catch (error) { if (error.code !== 'ENOENT') throw error }
    if (current && current.siteId.toLowerCase() !== siteId.toLowerCase()) throw new Error('cannot replace site identity')
    await mkdir(dirname(path), { recursive: true, mode: 0o700 })
    await writeFile(path, JSON.stringify({ siteId, siteName, siteTimeZone, defaultPageSize: 50, maximumPageSize: 200, cursorMinutes: 15 }, null, 2) + '\n', { mode: 0o600 })
    await chmod(path, 0o600)
    console.log('Explicit site configuration saved in ignored .cache/site-local/site.json; no database operation or ledger seeding was performed.')
  } else throw new Error('unknown command')
} catch {
  console.error('Site configuration unavailable. Use: eng/site prepare --id <stable-UUID> --name <actual-name> --time-zone <IANA-zone>, or eng/site status. Existing site identity cannot be replaced.')
  process.exitCode = 1
}
