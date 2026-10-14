import { createRequire } from 'node:module';
import { readFileSync, writeFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
const require = createRequire(new URL('../tests/realtime-js/package.json', import.meta.url));
const { HubConnectionBuilder, HttpTransportType } = require('@microsoft/signalr');
const statePath = process.argv[2] ?? '/tmp/agent-core-drive-survival.json';
const state = JSON.parse(readFileSync(statePath, 'utf8'));
const token = JSON.parse(readFileSync('/tmp/agent-core-owner.json', 'utf8')).token;
const base = `http://127.0.0.1:${process.env.AGENTCORE_COMPOSE_PORT ?? '5080'}`;
const connection = new HubConnectionBuilder().withUrl(`${base}/hubs/session`, { skipNegotiation: true, transport: HttpTransportType.WebSockets, headers: { 'X-AgentCore-Owner-Capability': token } }).build();
let attachment; let text = '';
let finish, fail;
const completed = new Promise((resolve, reject) => { finish = resolve; fail = reject; });
const deadline = setTimeout(() => fail(new Error('Compose Drive transfer did not complete within 60 seconds.')), 60000);
connection.on('SessionEvent', event => {
  if (event.type === 'session.ready') attachment = event.attachmentId;
  if (event.type === 'agent.text.delta') text += event.payload.text;
  if (event.type === 'agent.text.completed') finish(text);
  if (event.type === 'error') fail(new Error('Compose Drive fixture received a protocol error.'));
});
function command(sequence, type, payload) {
  return { protocolVersion: 1, sessionId: state.sessionId, eventId: randomUUID(), sequence, timestamp: new Date().toISOString(), responseId: null, attachmentId: attachment ?? null, type, payload };
}
try {
  await connection.start();
  await connection.invoke('Attach', command(0, 'session.attach', { lastServerSequence: null, ownerCapability: token }));
  if (!attachment) throw new Error('Compose session did not publish its attachment identity.');
  await connection.invoke('SendText', command(1, 'user.text', { text: 'synthetic-drive: compose-upload', attachmentIds: [], behavior: 'queue' }));
  const outcome = JSON.parse(await completed);
  if (outcome.receipt?.status !== 'Succeeded' || outcome.receipt.synthetic !== true) throw new Error('Synthetic Drive upload did not return a verified receipt.');
  state.operationId = outcome.receipt.operationId; state.file = outcome.receipt.file;
  writeFileSync(statePath, JSON.stringify(state));
  console.log('Synthetic Drive separate grant, exact bytes and verified conversational receipt created');
} finally { clearTimeout(deadline); await connection.stop(); }
