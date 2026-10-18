import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { root } from './toolchain.mjs';
export function dockerArguments() {
  return process.env.SVM_DOCKER_CONTEXT ? ['--context', process.env.SVM_DOCKER_CONTEXT] : [];
}
export function localDockerBaseline(name) {
  const file = JSON.parse(readFileSync(join(root, 'build', name), 'utf8'));
  const run = args => {
    const r = spawnSync('docker', [...dockerArguments(), ...args], { encoding: 'utf8', timeout: 15000 });
    if (r.status !== 0) throw new Error('Local Docker is unavailable; no context was changed.');
    return r.stdout.trim();
  };
  const context = process.env.SVM_DOCKER_CONTEXT ?? run(['context', 'show']);
  const endpoint = run(['context', 'inspect', context, '--format', '{{.Endpoints.docker.Host}}']);
  if (!endpoint.startsWith('unix://') && !endpoint.startsWith('npipe://'))
    throw new Error('Local helpers require a local Unix socket or Windows named pipe.');
  const platform = process.env.SVM_DOCKER_PLATFORM ?? run(['version', '--format', '{{.Server.Os}}/{{.Server.Arch}}']);
  if (!file.images?.[platform]) throw new Error('No reviewed image for the selected Docker platform.');
  return { ...file, platform, image: file.images[platform], dockerContext: context };
}
