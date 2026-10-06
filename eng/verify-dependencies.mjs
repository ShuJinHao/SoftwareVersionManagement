import { createHash } from 'node:crypto'
import { readFile, readdir, mkdir, copyFile, writeFile } from 'node:fs/promises'
import { execFileSync } from 'node:child_process'
import { resolve, dirname, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const policy = JSON.parse(await readFile(resolve(root, 'build/dependency-policy.json'), 'utf8'))
const fail = message => { throw new Error(message) }
const nugetPolicy = new Map(policy.nuget.map(p => [`${p.name.toLowerCase()}@${p.version}`, p]))
const npmPolicy = new Map(policy.npm.map(p => [`${p.name}@${p.version}`, p]))
const materials = resolve(root, 'artifacts/licenses')
await mkdir(materials, { recursive: true })
const observedNuget = new Set(), observedNpm = new Set()
const notices = []
async function* walk(folder) {
  for (const entry of await readdir(folder, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules', 'dist'].includes(entry.name)) continue
    const path = resolve(folder, entry.name)
    if (entry.isDirectory()) yield* walk(path)
    else if (entry.isFile()) yield path
  }
}
const sourceFiles = []
for await (const file of walk(resolve(root, 'src'))) sourceFiles.push(file)
const projects = sourceFiles.filter(f => f.endsWith('.csproj'))
for (const project of projects) {
  const lockPath = resolve(dirname(project), 'packages.lock.json')
  const lock = JSON.parse(await readFile(lockPath, 'utf8'))
  for (const group of Object.values(lock.dependencies)) {
    for (const [name, dep] of Object.entries(group)) {
      if (dep.type === 'Project') continue
      const key = `${name.toLowerCase()}@${dep.resolved}`
      const expected = nugetPolicy.get(key)
      if (!expected) fail(`Unreviewed NuGet package ${key} in ${relative(root, project)}`)
      const cache = resolve(root, '.cache/nuget/packages', name.toLowerCase(), dep.resolved)
      const digest = (await readFile(resolve(cache, `${name.toLowerCase()}.${dep.resolved}.nupkg.sha512`), 'utf8')).trim()
      const metadata = JSON.parse(await readFile(resolve(cache, '.nupkg.metadata'), 'utf8'))
      if (metadata.contentHash !== dep.contentHash) fail(`NuGet content hash differs from the lock: ${key}`)
      if (metadata.source !== 'https://api.nuget.org/v3/index.json') fail(`Unapproved NuGet source: ${key}`)
      if (observedNuget.has(key)) continue
      const archive = await readFile(resolve(cache, `${name.toLowerCase()}.${dep.resolved}.nupkg`))
      if (createHash('sha512').update(archive).digest('base64') !== digest) fail(`NuGet archive checksum differs: ${key}`)
      observedNuget.add(key)
      const nuspec = await readFile(resolve(cache, `${name.toLowerCase()}.nuspec`), 'utf8')
      const declared = nuspec.match(/<license\s+type="expression"[^>]*>([^<]+)<\/license>/)?.[1]
      if (declared && declared !== expected.license) fail(`NuGet license changed: ${key}: ${declared}`)
      if (!declared) {
        if (expected.licenseFiles?.length) {
          for (const file of expected.licenseFiles) {
            const bytes = await readFile(resolve(cache, file.path))
            if (createHash('sha256').update(bytes).digest('hex') !== file.sha256) fail(`Reviewed legacy license differs: ${key}`)
          }
        } else {
          const url = nuspec.match(/<licenseUrl>([^<]+)<\/licenseUrl>/)?.[1]
          if (key !== 'xunit.abstractions@2.0.3' || url !== expected.licenseUrl)
            fail(`No reviewed license evidence: ${key}`)
          notices.push(`${key}: package supplies a reviewed upstream license URL (${url}); retain that notice with redistribution.`)
        }
      }
      for (const item of await readdir(cache, { withFileTypes: true })) {
        if (item.isFile() && /license|notice|third.party/i.test(item.name))
          await copyFile(resolve(cache, item.name), resolve(materials, `nuget-${key}-${item.name}`))
      }
    }
  }
}

const webRoot = resolve(root, 'src/ui/svm-web')
const lock = JSON.parse(await readFile(resolve(webRoot, 'package-lock.json'), 'utf8'))
if (lock.lockfileVersion !== 3) fail('npm lockfile v3 is required')
for (const [path, pkg] of Object.entries(lock.packages)) {
  if (!path) continue
  const name = pkg.name ?? path.split('node_modules/').at(-1)
  const key = `${name}@${pkg.version}`
  const expected = npmPolicy.get(key)
  if (!expected) fail(`Unreviewed npm package: ${key}`)
  if (pkg.inBundle) {
    const split = path.lastIndexOf('/node_modules/')
    const parentPath = path.slice(0, split)
    const parent = lock.packages[parentPath]
    const parentName = parent?.name ?? parentPath.split('node_modules/').at(-1)
    const parentExpected = npmPolicy.get(`${parentName}@${parent?.version}`)
    if (!parentExpected || parent.integrity !== parentExpected.integrity || !parent.bundleDependencies?.includes(name))
      fail(`Unreviewed bundle parent: ${key}`)
    if (expected.bundleIntegrity && expected.bundleIntegrity !== parent.integrity) fail(`Bundle integrity changed: ${key}`)
    if (pkg.license !== expected.license) fail(`Bundled license differs: ${key}`)
    const [algorithm, digest] = parent.integrity.split('-')
    const hex = Buffer.from(digest, 'base64').toString('hex')
    const archive = resolve(root, '.cache/npm/_cacache/content-v2', algorithm, hex.slice(0, 2), hex.slice(2, 4), hex.slice(4))
    let bytes
    try { bytes = await readFile(archive) } catch (error) { if (error.code !== 'ENOENT') throw error }
    if (bytes) {
      if (createHash(algorithm).update(bytes).digest('base64') !== digest) fail(`Bundle cache checksum differs: ${key}`)
      const member = `package/${path.slice(parentPath.length + 1)}/package.json`
      const metadata = execFileSync('tar', ['-xOf', archive, member])
      const actual = JSON.parse(metadata)
      if (actual.name !== name || actual.version !== pkg.version || actual.license !== expected.license) fail(`Bundled package differs: ${key}`)
      if (expected.bundleMetadataSha256 && createHash('sha256').update(metadata).digest('hex') !== expected.bundleMetadataSha256)
        fail(`Bundled metadata differs: ${key}`)
    } else notices.push(`${key}: optional bundle is not downloaded; lock is bound to the reviewed parent archive.`)
    observedNpm.add(key)
    continue
  }
  if (!pkg.resolved?.startsWith('https://registry.npmjs.org/')) fail(`Unapproved npm source: ${key}`)
  if (expected.integrity !== pkg.integrity) fail(`npm integrity differs from reviewed metadata: ${key}`)
  if (expected.license !== pkg.license) fail(`npm license differs from reviewed metadata: ${key}`)
  observedNpm.add(key)
  const installed = resolve(webRoot, path)
  let contents
  try { contents = JSON.parse(await readFile(resolve(installed, 'package.json'), 'utf8')) }
  catch (error) { if (error.code === 'ENOENT' && pkg.optional) continue; throw error }
  if (contents.version !== pkg.version) fail(`Installed npm version differs from lock: ${key}`)
  if (typeof contents.license === 'string' && contents.license !== expected.license) fail(`Installed npm license differs: ${key}`)
  for (const item of await readdir(installed, { withFileTypes: true })) {
    if (item.isFile() && /license|notice|third.party/i.test(item.name))
      await copyFile(resolve(installed, item.name), resolve(materials, `npm-${key.replaceAll('/', '_')}-${item.name}`))
  }
}
console.log(`Verified ${projects.length} NuGet locks, ${observedNuget.size} NuGet packages and ${observedNpm.size} npm package/version pairs.`)
await writeFile(resolve(root, 'artifacts/dependencies.verified.json'), JSON.stringify({
  nuget: [...observedNuget].sort(), npm: [...observedNpm].sort(), notices,
}, null, 2) + '\n')
for (const notice of notices) console.log(notice)
console.log('Package license materials: artifacts/licenses. SDK/native/browser/container asset audits are separate pending checks.')
