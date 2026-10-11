import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { generate } from './manifest.mjs';

test('manifest preserves build identity and hashes bytes across chunk boundaries without overwriting outputs', async () => {
  const root = await mkdtemp(join(tmpdir(), 'svm-gitlab-manifest-'));
  try {
    const file = join(root, 'app.zip'), out = join(root, 'release-manifest.json'); const bytes = Buffer.alloc(1048599, 71); await writeFile(file, bytes);
    const env = { CI_PROJECT_ID: '17', CI_PIPELINE_ID: '3', CI_JOB_ID: '4', CI_COMMIT_TAG: 'v2.10.0', CI_COMMIT_SHA: 'b'.repeat(40), CI_COMMIT_REF_PROTECTED: 'true' };
    const result = await generate(file, 'fixture', out, env);
    assert.equal(result.sha256, createHash('sha256').update(bytes).digest('hex')); assert.equal(result.version, '2.10.0'); assert.equal(result.packageVersion, 'v2.10.0');
    assert.equal(result.jobId, 4); assert.equal(result.sizeBytes, bytes.length); assert.deepEqual(JSON.parse(await readFile(out, 'utf8')), result);
    await assert.rejects(generate(file, 'fixture', out, env));
    for (const override of [{ CI_COMMIT_TAG: 'v2.1.0-rc1' }, { CI_COMMIT_TAG: 'v02.1.0' }, { CI_COMMIT_REF_PROTECTED: 'false' }, { CI_JOB_ID: '0' }])
      await assert.rejects(generate(file, 'fixture', join(root, 'invalid.json'), { ...env, ...override }));
  } finally { await rm(root, { recursive: true, force: true }); }
});
