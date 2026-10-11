import { createReadStream } from 'node:fs';
import { stat, writeFile } from 'node:fs/promises';
import { basename, resolve } from 'node:path';
import { createHash } from 'node:crypto';
import { pathToFileURL } from 'node:url';

export async function generate(file, packageName, output, env = process.env) {
  const segment = x => typeof x === 'string' && /^[A-Za-z0-9._-]{1,200}$/.test(x) && !['.', '..'].includes(x);
  const positive = name => { const x = env[name]; if (!/^[1-9][0-9]*$/.test(x ?? '') || !Number.isSafeInteger(Number(x))) throw new Error('Invalid CI identifier'); return Number(x); };
  const tag = env.CI_COMMIT_TAG ?? '';
  const version = tag.replace(/^v/, ''); const parts = version.split('.');
  if (tag.length > 34 || parts.length !== 3 || parts.some(x => !/^(0|[1-9][0-9]*)$/.test(x) || Number(x) > 2147483647) || Number(parts[0]) < 1 ||
      !/^[a-fA-F0-9]{40}([a-fA-F0-9]{24})?$/.test(env.CI_COMMIT_SHA ?? '') || env.CI_COMMIT_REF_PROTECTED !== 'true' ||
      !segment(packageName) || !segment(basename(file)) || basename(file) === 'release-manifest.json' || resolve(file) === resolve(output)) throw new Error('Invalid protected release input');
  const info = await stat(file); if (!info.isFile() || info.size < 1 || !Number.isSafeInteger(info.size)) throw new Error('Invalid package file');
  const hash = createHash('sha256'); let size = 0;
  for await (const chunk of createReadStream(file, { highWaterMark: 1048576 })) { size += chunk.length; hash.update(chunk); }
  const after = await stat(file); if (size !== info.size || after.size !== size || after.mtimeMs !== info.mtimeMs) throw new Error('Package changed during hashing');
  const manifest = { schemaVersion: 1, projectId: positive('CI_PROJECT_ID'), tag, version, commitSha: env.CI_COMMIT_SHA,
    pipelineId: positive('CI_PIPELINE_ID'), jobId: positive('CI_JOB_ID'), packageName, packageVersion: tag, fileName: basename(file), sizeBytes: size, sha256: hash.digest('hex') };
  await writeFile(output, JSON.stringify(manifest, null, 2) + '\n', { flag: 'wx', mode: 0o600 }); return manifest;
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try { if (process.argv.length !== 5) throw new Error('Expected package file, package name and manifest output'); await generate(...process.argv.slice(2)); }
  catch { process.stderr.write('Release manifest generation failed; verify protected Tag, CI identifiers and immutable package input.\n'); process.exitCode = 1; }
}
