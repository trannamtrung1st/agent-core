import assert from 'node:assert/strict';
import { HubConnectionBuilder, HttpTransportType, JsonHubProtocol } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';

const base = process.env.BASE_URL;
const protocol = process.env.HUB_PROTOCOL === 'json' ? new JsonHubProtocol() : new MessagePackHubProtocol();
const cookies = new Map(); let csrf = '';
const cookie = () => [...cookies].map(([k, v]) => `${k}=${v}`).join('; ');
async function json(path, body) {
  const response = await fetch(base + path, { method: body === undefined ? 'GET' : 'POST',
    headers: { Cookie: cookie(), Origin: base, 'Content-Type': 'application/json', ...(csrf ? { 'X-AgentCore-CSRF': csrf } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body) });
  for (const item of response.headers.getSetCookie()) {
    const raw = item.split(';')[0], at = raw.indexOf('='); cookies.set(raw.slice(0, at), raw.slice(at + 1));
  }
  assert.ok(response.ok, `${path}: ${response.status}`); return response.json();
}
async function until(read) {
  const end = Date.now() + 15000;
  while (Date.now() < end) { const value = await read(); if (value) return value; await new Promise(r => setTimeout(r, 20)); }
  throw new Error('Expected Hub diagnostic recovery did not arrive');
}
csrf = (await json('/api/v2/auth/csrf')).token;
await json('/api/v2/auth/login', { login: 'owner@example.test', password: 'explicit-synthetic-test-password' });
csrf = (await json('/api/v2/auth/csrf')).token;
const tenant = (await json('/api/v2/account/tenants')).items[0].tenantId, api = `/api/v2/tenants/${tenant}`;
const revision = async () => (await json(api + '/access/me')).revision;
const actor = (await json(api + '/access/me')).principalId;
const agent = (await json(api + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() })).instance.instanceId;
const session = (await json(api + '/sessions', { agentInstanceId: agent, expectedRevision: await revision(), expectedInstanceRevision: 1 })).sessionId;
const events = [];
const hub = new HubConnectionBuilder().withUrl(base + '/hubs/session', {
  skipNegotiation: true, transport: HttpTransportType.WebSockets, headers: { Cookie: cookie(), Origin: base }
}).withHubProtocol(protocol).configureLogging('None').build();
hub.on('SessionEvent', e => events.push(e));
const command = (type, payload, sequence = 0, extra = {}) => ({ protocolVersion: 1, type, sessionId: session,
  eventId: crypto.randomUUID(), sequence, timestamp: new Date().toISOString(), attachmentId: crypto.randomUUID(), payload, ...extra });
try {
  await hub.start();
  let failure = '';
  try { await hub.invoke('AttachTenant', command('session.attach', { tenantId: tenant, principalId: crypto.randomUUID() })); }
  catch (error) { failure = error.message; }
  assert.ok(failure, 'Unexpected storage failure must fail the invocation');
  assert.equal(events.length, 0, 'Failed attach must not publish a ready snapshot');
  assert.equal((await json(api + `/sessions/${session}`)).revision, 1);
  const attached = await hub.invoke('AttachTenant', command('session.attach', { tenantId: tenant }));
  assert.equal(attached.accepted, true);
  const ready = await until(() => events.find(e => e.type === 'session.ready'));
  assert.equal(ready.payload.tenantId, tenant);
  const send = command('user.text', { text: 'Recover after the exact unexpected Hub fault', expectedRevision: await revision(),
    expectedSessionRevision: ready.payload.sessionRevision }, 1, { attachmentId: ready.attachmentId });
  const accepted = await hub.invoke('SendText', send); assert.equal(accepted.accepted, true);
  await until(() => events.find(e => e.type === 'agent.response.completed' && e.responseId === accepted.result.responseId));
  assert.equal((await hub.invoke('SendText', send)).result.runId, accepted.result.runId);
  const history = await json(api + `/sessions/${session}/messages`);
  assert.equal(history.items.length, 2); assert.equal(history.items[1].text, 'Hello from synthetic.');
  console.log(JSON.stringify({ tenant, actor, session, failure }));
} finally { await hub.stop(); }
