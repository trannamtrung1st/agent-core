import { spawn } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
let child;
let stopping = false;
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { stopping = true; child?.kill(signal); });
async function run(args) {
  return await new Promise((resolve, reject) => {
    child = spawn('dotnet', args, { cwd: repo, env: process.env, stdio: 'inherit' });
    child.once('error', reject);
    child.once('exit', code => resolve(code ?? 1));
  });
}
const built = await run(['build', 'src/AgentCore.Api/AgentCore.Api.csproj', '--no-restore', '--nologo', '-m:1', '-nodeReuse:false', '-p:UseSharedCompilation=false']);
if (built !== 0 || stopping) process.exit(built);
const common = ['run', '--project', 'src/AgentCore.Api', '--no-restore', '--no-build', '--no-launch-profile'];
const bootstrapped = await run([...common, '--', 'bootstrap-identity']);
if (bootstrapped !== 0 || stopping) process.exit(bootstrapped);
process.exit(await run([...common, '--urls', process.env.TENANT_ACCEPTANCE_API]));
