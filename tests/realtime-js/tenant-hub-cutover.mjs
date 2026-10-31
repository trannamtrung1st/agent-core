import assert from 'node:assert/strict';
import { HubConnectionBuilder, HttpTransportType, JsonHubProtocol } from '@microsoft/signalr';
import { MessagePackHubProtocol } from '@microsoft/signalr-protocol-msgpack';

const base = process.env.BASE_URL;
const protocol = process.env.HUB_PROTOCOL === 'json' ? new JsonHubProtocol() : new MessagePackHubProtocol();
const oldToken = process.env.OLD_OWNER_TOKEN;
assert.ok(base && oldToken);
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(read) {
  const end = Date.now() + 15000;
  while (Date.now() < end) { const result = await read(); if (result) return result; await pause(20); }
  throw new Error('Expected canonical Hub outcome did not arrive');
}
const cookies = new Map(); let csrf = '';
const cookie = () => [...cookies].map(([key, value]) => `${key}=${value}`).join('; ');
async function request(path, body) {
  const response = await fetch(base + path, { method: body === undefined ? 'GET' : 'POST',
    headers: { Cookie: cookie(), Origin: base, 'Content-Type': 'application/json', ...(csrf ? { 'X-AgentCore-CSRF': csrf } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body) });
  for (const value of response.headers.getSetCookie()) {
    const raw = value.split(';')[0], at = raw.indexOf('='); cookies.set(raw.slice(0, at), raw.slice(at + 1));
  }
  return response;
}
async function json(path, body) {
  const response = await request(path, body);
  assert.ok(response.ok, `${path}: ${response.status} ${await response.clone().text()}`);
  return response.json();
}
const connections = [];
async function connect(headers = { Cookie: cookie(), Origin: base }) {
  const events = []; let closed = false;
  const hub = new HubConnectionBuilder().withUrl(base + '/hubs/session', {
    skipNegotiation: true, transport: HttpTransportType.WebSockets, headers
  }).withHubProtocol(protocol).configureLogging('None').build();
  hub.on('SessionEvent', event => events.push(event)); hub.onclose(() => { closed = true; });
  connections.push(hub); await hub.start(); return { hub, events, closed: () => closed };
}
try {
  for (const headers of [{}, { 'X-AgentCore-Owner-Capability': oldToken }]) {
    const negotiation = await fetch(base + '/hubs/session/negotiate?negotiateVersion=1', { method: 'POST', headers });
    assert.equal(negotiation.status, 401);
    await assert.rejects(connect({ ...headers, Origin: base }), /WebSocket|401|Unauthorized|Unexpected server response/i);
  }
  csrf = (await json('/api/v2/auth/csrf')).token;
  await json('/api/v2/auth/login', { login: process.env.TENANT_UI_LOGIN ?? 'owner@example.test', password: process.env.TENANT_UI_PASSWORD ?? 'explicit-synthetic-test-password' });
  csrf = (await json('/api/v2/auth/csrf')).token;
  const tenant = (await json('/api/v2/account/tenants')).items[0].tenantId, api = `/api/v2/tenants/${tenant}`;
  const revision = async () => (await json(api + '/access/me')).revision;
  const creator = (await json(api + '/access/me')).principalId;
  const agent = await json(api + '/agents', { templateId: 'examiner', version: 1, expectedRevision: await revision() });
  const instance = agent.instance.instanceId;
  const space = await json(api + `/agents/${instance}/private-space`);
  const role = await json(api + '/roles', { name: 'Exact canonical Hub context', permissions: ['spaces.read'], expectedRevision: await revision() });
  await json(api + '/role-assignments', { principalId: agent.principalId, roleId: role.roleId,
    resourceType: 'space', resourceId: space.spaceId, expectedRevision: await revision() });
  const session = (await json(api + '/sessions', { retentionKind: 'saved', agentInstanceId: instance, expectedRevision: await revision(), expectedInstanceRevision: 1 })).sessionId;
  const before = await json(api + `/sessions/${session}`);
  const command = (type, payload, sequence = 1, extra = {}) => ({ protocolVersion: 1, type, sessionId: session,
    eventId: crypto.randomUUID(), sequence, timestamp: new Date().toISOString(), attachmentId: crypto.randomUUID(), payload, ...extra });
  const live = await connect({ Cookie: cookie(), Origin: base, 'X-AgentCore-Owner-Capability': oldToken });
  for (const [method, type, payload] of [
    ['SendText', 'user.text', { text: 'No attachment authority', expectedRevision: await revision(), expectedSessionRevision: before.revision }],
    ['CancelResponse', 'agent.response.cancel', {}], ['SetMode', 'session.mode.set', { mode: 'voice' }],
    ['SetMuted', 'session.mute', { muted: true }],
    ['SpeechStarted', 'user.speech.started', { streamId: crypto.randomUUID(), utteranceId: crypto.randomUUID(), sampleOffset: 0, activityScore: .9 }],
    ['SpeechEnded', 'user.speech.ended', { streamId: crypto.randomUUID(), utteranceId: crypto.randomUUID(), sampleOffset: 0, durationMs: 10 }],
    ['SpeechEvidence', 'client.speech.evidence', { kind: 'final', utteranceId: crypto.randomUUID(), text: 'No attachment authority' }],
    ...['Started', 'Progress', 'Completed', 'Stopped'].map(kind => ['Playback' + kind, 'playback.' + kind.toLowerCase(), { consumedSamples: 0, textEndExclusive: 0 }]),
    ['ResponseReceived', 'response.received', { textEndExclusive: 0 }]
  ]) {
    const ack = await live.hub.invoke(method, command(type, payload));
    assert.equal(ack.accepted, false, method); assert.equal(ack.error.code, 'StaleAttachment', method);
  }
  await live.hub.invoke('SendAudio', { protocolVersion: 1, sessionId: session, attachmentId: crypto.randomUUID(),
    streamId: crypto.randomUUID(), frameSequence: 1, sampleOffset: 0,
    data: process.env.HUB_PROTOCOL === 'json' ? '' : new Uint8Array() });
  for (const method of ['Attach', 'RespondApproval', 'EndSession']) {
    await assert.rejects(live.hub.invoke(method, command('session.attach', { ownerCapability: oldToken }, 0)), /does not exist/i);
  }
  assert.equal(live.events.length, 0);
  const unchanged = await json(api + `/sessions/${session}`);
  assert.equal(unchanged.revision, before.revision); assert.equal(unchanged.status, 'created'); assert.equal(unchanged.mode, 'text');
  assert.equal((await json(api + `/sessions/${session}/messages`)).items.length, 0);

  // Fatal envelopes are answered safely and then close the connection, before attachment or work.
  for (const [patch, code] of [
    [{ protocolVersion: 99 }, 'ProtocolVersionMismatch'], [{ type: 'user.text' }, 'ProtocolError'],
    [{ sequence: 1 }, 'ProtocolError'], [{ correlationId: crypto.randomUUID() }, 'ProtocolError'],
    [{ causationId: crypto.randomUUID() }, 'ProtocolError'], [{ sessionId: 'not-a-uuid' }, 'ProtocolError']
  ]) {
    const invalid = await connect();
    const ack = await invalid.hub.invoke('AttachTenant', command('session.attach', { tenantId: tenant }, 0, patch));
    assert.equal(ack.accepted, false); assert.equal(ack.error.code, code); assert.equal(ack.error.fatal, true);
    if (code === 'ProtocolVersionMismatch') assert.deepEqual(ack.error.extensions.supportedVersions, [1]);
    await until(invalid.closed); assert.equal(invalid.events.length, 0);
  }
  const empty = await connect();
  const nullAck = await empty.hub.invoke('AttachTenant', null);
  assert.equal(nullAck.accepted, false); assert.equal(nullAck.error.code, 'ProtocolError'); await until(empty.closed);

  const attached = await live.hub.invoke('AttachTenant', command('session.attach', { tenantId: tenant, ownerCapability: oldToken,
    principalId: crypto.randomUUID(), accountId: crypto.randomUUID() }, 0));
  assert.equal(attached.accepted, true);
  const ready = await until(() => live.events.find(event => event.type === 'session.ready'));
  assert.equal(ready.payload.tenantId, tenant); assert.equal(ready.payload.streamId, null);
  for (const method of ['Attach', 'RespondApproval', 'EndSession'])
    await assert.rejects(live.hub.invoke(method, command('session.attach', {}, 0)), /does not exist/i);
  const send = command('user.text', { text: 'Canonical Hub survives retired-method refusal', expectedRevision: await revision(),
    expectedSessionRevision: ready.payload.sessionRevision }, 1, { attachmentId: ready.attachmentId });
  const admitted = await live.hub.invoke('SendText', send); assert.equal(admitted.accepted, true);
  await until(() => live.events.find(event => event.type === 'agent.response.completed' && event.responseId === admitted.result.responseId));
  const run = await until(async () => { const value = await json(api + `/agent-runs/${admitted.result.runId}`); return value.status === 'completed' ? value : null; });
  assert.equal(run.context.originalInitiatorPrincipalId, creator);
  assert.equal((await live.hub.invoke('SendText', send)).result.runId, admitted.result.runId);
  const history = await json(api + `/sessions/${session}/messages`);
  assert.equal(history.items.length, 2); assert.equal(history.items[1].text, 'Hello from synthetic.');
  console.log('Tenant Hub cutover passed: ' + (process.env.HUB_PROTOCOL || 'messagepack'));
} finally { await Promise.all(connections.map(connection => connection.stop())); }
