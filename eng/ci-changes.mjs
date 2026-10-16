import { readFileSync, appendFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
const event=JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH,'utf8'));
let code=true;
if(process.env.GITHUB_EVENT_NAME==='pull_request' && event.action==='synchronize' && /^[0-9a-f]{40}$/.test(event.before??'')) {
  const files=execFileSync('git',['diff','--name-only',event.before,event.pull_request.head.sha],{encoding:'utf8'}).trim().split('\n');
  code=files.some(f=>!f.endsWith('.md') && /^(src\/|eng\/|build\/|deploy\/|\.github\/|global\.json$|Directory\..*|SoftwareVersionManagement\.sln$|NuGet\.Config$)/.test(f));
}
appendFileSync(process.env.GITHUB_OUTPUT,'code='+code+'\n');
console.log(code?'Source/build/deployment changed; run affected platform checks.':'Documentation-only update; reuse existing code verification.');
