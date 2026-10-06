import { createHash } from 'node:crypto'
import { createReadStream } from 'node:fs'
import { access, mkdir, readFile, rename } from 'node:fs/promises'
import { dirname, basename, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { execFileSync } from 'node:child_process'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const manifest = JSON.parse(await readFile(resolve(root, 'build/toolchain.lock.json'), 'utf8'))
const platform = `${process.platform}-${process.arch}`
const archives = manifest.platforms[platform]
if (!archives) throw new Error(`No reviewed toolchain for ${platform}`)
const env = { ...process.env, DOTNET_ROOT: resolve(root, '.tools/dotnet'),
  DOTNET_CLI_HOME: resolve(root, '.cache/dotnet-home'), DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1', DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false',
  DOTNET_NOLOGO: '1', DOTNET_MULTILEVEL_LOOKUP: '0' }

async function hash(file, algorithm) {
  const digest = createHash(algorithm)
  for await (const data of createReadStream(file)) digest.update(data)
  return digest.digest('hex')
}
async function exists(file) { try { await access(file); return true } catch { return false } }

await mkdir(resolve(root, '.tools'), { recursive: true })
await mkdir(resolve(root, '.cache/tool-downloads'), { recursive: true })
for (const [name, artifact] of Object.entries(archives)) {
  const target = resolve(root, '.tools', name)
  if (await exists(target)) continue
  const archive = resolve(root, '.cache/tool-downloads', basename(new URL(artifact.url).pathname))
  if (!await exists(archive)) {
    for (let attempt = 1; attempt <= 8; attempt++) {
      try {
        execFileSync('curl', ['--fail', '--show-error', '--location', '--continue-at', '-', '--connect-timeout', '20',
          '--max-time', '180', artifact.url, '-o', `${archive}.partial`], { stdio: 'inherit' })
        break
      } catch (error) {
        if (attempt === 8 || ![18, 28, 35, 52, 55, 56].includes(error.status)) throw error
        console.error(`${name}: connection interrupted; continuing partial download (${attempt}/8)`)
      }
    }
    if (await hash(`${archive}.partial`, artifact.algorithm) !== artifact.hash)
      throw new Error(`${name}: downloaded archive checksum mismatch`)
    await rename(`${archive}.partial`, archive)
  }
  if (await hash(archive, artifact.algorithm) !== artifact.hash)
    throw new Error(`${name}: cached archive checksum mismatch; replace the incomplete archive before retrying`)
  const staging = `${target}.staging-${process.pid}`
  await mkdir(staging)
  execFileSync('tar', ['-xzf', archive, '-C', staging, ...(name === 'node' ? ['--strip-components=1'] : [])], { stdio: 'inherit' })
  await rename(staging, target)
}

const dotnet = resolve(root, '.tools/dotnet/dotnet')
const node = resolve(root, '.tools/node/bin/node')
const npm = resolve(root, '.tools/node/lib/node_modules/npm/bin/npm-cli.js')
const version = (command, args) => execFileSync(command, args, { cwd: root, env, encoding: 'utf8' }).trim()
if (version(dotnet, ['--version']) !== manifest.dotnetSdk) throw new Error('SDK version mismatch')
const runtimes = version(dotnet, ['--list-runtimes'])
for (const name of ['Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'])
  if (!runtimes.includes(`${name} ${manifest.runtime} [`)) throw new Error(`${name}: runtime version mismatch`)
if (version(node, ['--version']) !== `v${manifest.node}`) throw new Error('Node version mismatch')
if (version(node, [npm, '--version']) !== manifest.npm) throw new Error('Bundled npm differs from the reviewed version; review its installation before continuing')
console.log(`Verified ${platform}: SDK ${manifest.dotnetSdk}, runtime ${manifest.runtime}, Node ${manifest.node}, npm ${manifest.npm}`)
