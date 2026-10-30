import { readFileSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

export function changeImpact(files) {
  const changed = files.filter(f => f && !f.endsWith('.md'));
  const code = changed.some(f => /^(src\/|eng\/|build\/|deploy\/|\.github\/|global\.json$|Directory\..*|SoftwareVersionManagement\.sln$|NuGet\.Config$)/.test(f));
  const tools = changed.some(f => /^(eng\/(bootstrap|toolchain|run|publish|test|delivery\.test|ci-changes)\.mjs$|Directory\.Build\.targets$|global\.json$|\.github\/)/.test(f));
  const web = changed.some(f => /^src\/ui\/svm-web\//.test(f));
  const browser = changed.some(f => /^src\/ui\/svm-web\/(src\/|tests\/browser\/)/.test(f) || /^deploy\/linux\/verify-(ui|hardware|roles|plc)\.mjs$/.test(f));
  return { code, tools, web, browser };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
  let impact = { code: true, tools: true, web: true, browser: true };
  if (process.env.GITHUB_EVENT_NAME === 'pull_request') {
    const before = event.action === 'synchronize' ? event.before : event.pull_request?.base?.sha;
    const after = event.pull_request?.head?.sha;
    if (/^[0-9a-f]{40}$/.test(before ?? '') && /^[0-9a-f]{40}$/.test(after ?? '')) {
      const files = execFileSync('git', ['diff', '--name-only', before, after], { encoding: 'utf8' }).trim().split('\n');
      impact = changeImpact(files);
    }
  }
  appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(impact).map(([key, value]) => key + '=' + value + '\n').join(''));
  console.log(JSON.stringify(impact));
}
