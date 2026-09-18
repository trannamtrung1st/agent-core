import { HttpTransportType, HubConnectionBuilder } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";

const base = process.env.BASE_URL;
const scenario = process.argv[2];
if (!base || !scenario) {
  console.error("usage: node client.mjs <scenario>");
  process.exit(2);
}

const events = [];
let ownerToken = "";

function uuid() {
  return crypto.randomUUID();
}

function command(sessionId, sequence, type, payload, extra = {}) {
  const body = {
    protocolVersion: extra.protocolVersion ?? 1,
    sessionId,
    eventId: extra.eventId ?? uuid(),
    sequence,
    timestamp: new Date().toISOString(),
    responseId: extra.responseId ?? null,
    attachmentId: extra.attachmentId ?? null,
    type,
    payload
  };
  if (extra.correlationId !== undefined) {
    body.correlationId = extra.correlationId;
  }
  if (extra.causationId !== undefined) {
    body.causationId = extra.causationId;
  }
  if (type === "session.attach") {
    body.payload = { ...payload, ownerCapability: ownerToken };
  }
  return body;
}

async function ensureOwner() {
  if (ownerToken) {
    return ownerToken;
  }
  const response = await fetch(`${base}/api/v1/local/owner-capability`, { method: "POST" });
  if (!response.ok) {
    throw new Error(`owner capability failed ${response.status}`);
  }
  const body = await response.json();
  ownerToken = body.token;
  return ownerToken;
}

async function connect() {
  await ensureOwner();
  const connection = new HubConnectionBuilder()
    .withUrl(`${base}/hubs/session`, {
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets,
      headers: { "X-AgentCore-Owner-Capability": ownerToken }
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .build();
  connection.on("SessionEvent", (evt) => events.push(evt));
  await connection.start();
  return connection;
}

async function ownerHeaders(extra = {}) {
  await ensureOwner();
  return { ...extra, "X-AgentCore-Owner-Capability": ownerToken };
}

async function reopenIfPaused(sessionId) {
  const view = await fetch(`${base}/api/v2/sessions/${sessionId}`, { headers: await ownerHeaders() });
  if (!view.ok) {
    throw new Error(`session view failed ${view.status}`);
  }
  const body = await view.json();
  if (body.status !== "paused") {
    return;
  }
  const reopen = await fetch(`${base}/api/v2/sessions/${sessionId}/reopen`, {
    method: "POST",
    headers: await ownerHeaders()
  });
  if (reopen.status === 409) {
    return;
  }
  if (!reopen.ok) {
    throw new Error(`reopen failed ${reopen.status}`);
  }
}

async function attachSession(connection, sessionId, sequence = 0, extra = {}) {
  await reopenIfPaused(sessionId);
  return connection.invoke("Attach", command(sessionId, sequence, "session.attach", { lastServerSequence: null }, extra));
}

async function createSession() {
  await ensureOwner();
  const response = await fetch(`${base}/api/v1/sessions`, {
    method: "POST",
    headers: await ownerHeaders({ "content-type": "application/json" }),
    body: JSON.stringify({ agentId: "examiner", mode: "text" })
  });
  if (!response.ok) {
    throw new Error(`create failed ${response.status}`);
  }
  return response.json();
}

async function run() {
  switch (scenario) {
    case "text-roundtrip": {
      const session = await createSession();
      const connection = await connect();
      const attachAck = await attachSession(connection, session.sessionId);
      if (!attachAck.accepted) {
        throw new Error(JSON.stringify(attachAck));
      }
      await waitFor((evt) => evt.type === "session.ready");
      const send = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId: events[0].attachmentId })
      );
      if (!send.accepted) {
        throw new Error(JSON.stringify(send));
      }
      await waitFor((evt) => evt.type === "agent.response.completed");
      const types = events.map((evt) => evt.type);
      if (!types.includes("agent.text.delta") || !types.includes("agent.text.completed")) {
        throw new Error(`missing text events: ${types.join(",")}`);
      }
      if (events.some((evt) => evt.correlationId == null || evt.sequence < 1)) {
        throw new Error("server envelopes missing correlation or sequence");
      }
      await connection.stop();
      break;
    }
    case "protocol-version": {
      const session = await createSession();
      const connection = await connect();
      const ack = await connection.invoke(
        "Attach",
        command(session.sessionId, 0, "session.attach", { lastServerSequence: null }, { protocolVersion: 2 })
      );
      if (ack.accepted || ack.error?.code !== "ProtocolVersionMismatch") {
        throw new Error(JSON.stringify(ack));
      }
      await connection.stop();
      break;
    }
    case "client-correlation": {
      const session = await createSession();
      const connection = await connect();
      const ack = await connection.invoke(
        "Attach",
        command(session.sessionId, 0, "session.attach", { lastServerSequence: null }, { correlationId: uuid() })
      );
      if (ack.accepted || ack.error?.code !== "ProtocolError") {
        throw new Error(JSON.stringify(ack));
      }
      await connection.stop();
      break;
    }
    case "second-connection": {
      const session = await createSession();
      const first = await connect();
      const attachAck = await attachSession(first, session.sessionId);
      if (!attachAck.accepted) {
        throw new Error(JSON.stringify(attachAck));
      }
      const second = await connect();
      const denied = await second.invoke(
        "Attach",
        command(session.sessionId, 0, "session.attach", { lastServerSequence: null })
      );
      if (denied.accepted || denied.error?.code !== "SessionInUse") {
        throw new Error(JSON.stringify(denied));
      }
      await second.stop();
      await first.stop();
      break;
    }
    case "stale-sequence": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const stale = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Again" }, { attachmentId })
      );
      if (stale.accepted || stale.error?.code !== "StaleCommand") {
        throw new Error(JSON.stringify(stale));
      }
      await connection.stop();
      break;
    }
    case "exact-retry": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const eventId = uuid();
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId, eventId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const retry = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId, eventId })
      );
      if (!retry.accepted || retry.eventId !== first.eventId) {
        throw new Error(JSON.stringify(retry));
      }
      await connection.stop();
      break;
    }
    case "eventid-reuse": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const eventId = uuid();
      await connection.invoke("SendText", command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId, eventId }));
      const changed = await connection.invoke(
        "SendText",
        command(session.sessionId, 2, "user.text", { text: "Other" }, { attachmentId, eventId })
      );
      if (changed.accepted || changed.error?.code !== "ProtocolError") {
        throw new Error(JSON.stringify(changed));
      }
      await connection.stop();
      break;
    }
    case "missing-attachment": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const denied = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" })
      );
      if (denied.accepted || denied.error?.code !== "StaleCommand") {
        throw new Error(JSON.stringify(denied));
      }
      await connection.stop();
      break;
    }
    case "method-type-mismatch": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const denied = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "session.mute", { muted: true }, { attachmentId })
      );
      if (denied.accepted || denied.error?.code !== "ProtocolError" || !denied.error?.fatal) {
        throw new Error(JSON.stringify(denied));
      }
      await connection.stop();
      break;
    }
    case "command-gap": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const send = await connection.invoke(
        "SendText",
        command(session.sessionId, 3, "user.text", { text: "Hello" }, { attachmentId })
      );
      if (!send.accepted) {
        throw new Error(JSON.stringify(send));
      }
      await connection.stop();
      break;
    }
    case "fatal-close": {
      const session = await createSession();
      const connection = await connect();
      const ack = await connection.invoke(
        "Attach",
        command(session.sessionId, 0, "session.attach", { lastServerSequence: null }, { protocolVersion: 2 })
      );
      if (ack.accepted || ack.error?.code !== "ProtocolVersionMismatch") {
        throw new Error(JSON.stringify(ack));
      }
      await new Promise((resolve) => setTimeout(resolve, 400));
      if (connection.state === "Connected") {
        throw new Error("fatal protocol mismatch must close the connection");
      }
      try {
        await connection.stop();
      } catch {
        // already closed
      }
      break;
    }
    case "response-received": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      await connection.invoke("SendText", command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId }));
      await waitFor((evt) => evt.type === "agent.response.completed");
      const started = events.find((evt) => evt.type === "agent.response.started");
      const receipt = await connection.invoke(
        "ResponseReceived",
        command(session.sessionId, 2, "response.received", { textEndExclusive: 21 }, { attachmentId, responseId: started.responseId })
      );
      if (!receipt.accepted) {
        throw new Error(JSON.stringify(receipt));
      }
      await connection.stop();
      break;
    }
    case "audio-session-mismatch": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const mode = await connection.invoke(
        "SetMode",
        command(session.sessionId, 1, "session.mode.set", { mode: "voice" }, { attachmentId })
      );
      if (!mode.accepted) {
        throw new Error(JSON.stringify(mode));
      }
      await waitFor((evt) => evt.type === "session.state.changed" && evt.payload?.mode === "voice");
      try {
        await connection.invoke("SendAudio", {
          protocolVersion: 1,
          sessionId: uuid(),
          attachmentId,
          streamId: uuid(),
          frameSequence: 1,
          sampleOffset: 0,
          data: new Uint8Array(960)
        });
      } catch {
        // abort may cancel the invoke
      }
      await new Promise((resolve) => setTimeout(resolve, 400));
      if (connection.state === "Connected") {
        throw new Error("mismatched audio sessionId must abort the connection");
      }
      try {
        await connection.stop();
      } catch {
        // already closed
      }
      break;
    }
    case "speech-boundary-order": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const mode = await connection.invoke(
        "SetMode",
        command(session.sessionId, 1, "session.mode.set", { mode: "voice" }, { attachmentId })
      );
      if (!mode.accepted) {
        throw new Error(JSON.stringify(mode));
      }
      const state = await waitForEvent((evt) => evt.type === "session.state.changed" && evt.payload?.mode === "voice");
      const streamId = state.payload.streamId;
      const utteranceId = uuid();
      const ended = await connection.invoke(
        "SpeechEnded",
        command(
          session.sessionId,
          2,
          "user.speech.ended",
          { streamId, utteranceId, sampleOffset: 480, durationMs: 20, activityScore: 0.2 },
          { attachmentId }
        )
      );
      if (!ended.accepted) {
        throw new Error(JSON.stringify(ended));
      }
      const started = await connection.invoke(
        "SpeechStarted",
        command(
          session.sessionId,
          3,
          "user.speech.started",
          { streamId, utteranceId, sampleOffset: 0, activityScore: 0.9 },
          { attachmentId }
        )
      );
      if (!started.accepted) {
        throw new Error(JSON.stringify(started));
      }
      if (events.some((evt) => evt.type === "transcript.final")) {
        throw new Error("ended boundary must wait for preceding audio samples");
      }
      await connection.invoke("SendAudio", {
        protocolVersion: 1,
        sessionId: session.sessionId,
        attachmentId,
        streamId,
        frameSequence: 1,
        sampleOffset: 0,
        data: new Uint8Array(960)
      });
      await waitFor((evt) => evt.type === "transcript.final");
      await connection.stop();
      break;
    }
    case "older-retry": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const eventId = uuid();
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId, eventId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const muted = await connection.invoke(
        "SetMuted",
        command(session.sessionId, 2, "session.mute", { muted: true }, { attachmentId })
      );
      if (!muted.accepted) {
        throw new Error(JSON.stringify(muted));
      }
      const retry = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId, eventId })
      );
      if (!retry.accepted || retry.eventId !== first.eventId) {
        throw new Error(JSON.stringify(retry));
      }
      await connection.stop();
      break;
    }
    case "reconnect-retry": {
      const session = await createSession();
      const first = await connect();
      await attachSession(first, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const firstAttachment = events[0].attachmentId;
      const eventId = uuid();
      const send = await first.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId: firstAttachment, eventId })
      );
      if (!send.accepted) {
        throw new Error(JSON.stringify(send));
      }
      await waitFor((evt) => evt.type === "agent.response.completed");
      await first.stop();
      events.length = 0;
      const second = await connect();
      await attachSession(second, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const retry = await second.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId: events[0].attachmentId, eventId })
      );
      if (!retry.accepted) {
        throw new Error(JSON.stringify(retry));
      }
      const page = await fetch(`${base}/api/v1/sessions/${session.sessionId}/messages?after=0`, {
        headers: await ownerHeaders()
      });
      if (!page.ok) {
        throw new Error(`history ${page.status}`);
      }
      const body = await page.json();
      const users = body.items.filter((item) => item.role === "user");
      if (users.length !== 1) {
        throw new Error(`expected one user entry, got ${users.length}`);
      }
      await second.stop();
      break;
    }
    case "stale-attachment": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const denied = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId: uuid() })
      );
      if (denied.accepted || denied.error?.code !== "StaleCommand") {
        throw new Error(JSON.stringify(denied));
      }
      await connection.stop();
      break;
    }
    case "oversized-audio": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const mode = await connection.invoke(
        "SetMode",
        command(session.sessionId, 1, "session.mode.set", { mode: "voice" }, { attachmentId })
      );
      if (!mode.accepted) {
        throw new Error(JSON.stringify(mode));
      }
      const state = await waitForEvent((evt) => evt.type === "session.state.changed" && evt.payload?.mode === "voice");
      try {
        await connection.invoke("SendAudio", {
          protocolVersion: 1,
          sessionId: session.sessionId,
          attachmentId,
          streamId: state.payload.streamId,
          frameSequence: 1,
          sampleOffset: 0,
          data: new Uint8Array(1921)
        });
      } catch {
        // abort may cancel the invoke
      }
      await new Promise((resolve) => setTimeout(resolve, 400));
      if (connection.state === "Connected") {
        throw new Error("oversized PCM must abort the connection");
      }
      try {
        await connection.stop();
      } catch {
        // already closed
      }
      break;
    }
    case "playback-invalid": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const mode = await connection.invoke(
        "SetMode",
        command(session.sessionId, 1, "session.mode.set", { mode: "voice" }, { attachmentId })
      );
      if (!mode.accepted) {
        throw new Error(JSON.stringify(mode));
      }
      await connection.invoke("SendText", command(session.sessionId, 2, "user.text", { text: "Hello" }, { attachmentId }));
      const startedEvt = await waitForEvent((evt) => evt.type === "agent.response.started");
      const started = await connection.invoke(
        "PlaybackStarted",
        command(
          session.sessionId,
          3,
          "playback.started",
          { consumedSamples: 480, textEndExclusive: 0 },
          { attachmentId, responseId: startedEvt.responseId }
        )
      );
      if (started.accepted || started.error?.code !== "ValidationError") {
        throw new Error(JSON.stringify(started));
      }
      const zero = await connection.invoke(
        "PlaybackStarted",
        command(
          session.sessionId,
          4,
          "playback.started",
          { consumedSamples: 0, textEndExclusive: 0 },
          { attachmentId, responseId: startedEvt.responseId }
        )
      );
      if (!zero.accepted) {
        throw new Error(JSON.stringify(zero));
      }
      const backwards = await connection.invoke(
        "PlaybackProgress",
        command(
          session.sessionId,
          5,
          "playback.progress",
          { consumedSamples: 999999, textEndExclusive: 0 },
          { attachmentId, responseId: startedEvt.responseId }
        )
      );
      if (backwards.accepted || backwards.error?.code !== "ValidationError") {
        throw new Error(JSON.stringify(backwards));
      }
      const textHigh = await connection.invoke(
        "PlaybackProgress",
        command(
          session.sessionId,
          6,
          "playback.progress",
          { consumedSamples: 0, textEndExclusive: 999999 },
          { attachmentId, responseId: startedEvt.responseId }
        )
      );
      if (textHigh.accepted || textHigh.error?.code !== "ValidationError") {
        throw new Error(JSON.stringify(textHigh));
      }
      await connection.stop();
      break;
    }
    case "parallel-controls": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const [first, second] = await Promise.all([
        connection.invoke("SetMuted", command(session.sessionId, 1, "session.mute", { muted: true }, { attachmentId })),
        connection.invoke("SetMuted", command(session.sessionId, 2, "session.mute", { muted: false }, { attachmentId }))
      ]);
      const accepted = [first, second].filter((ack) => ack.accepted);
      if (accepted.length < 1) {
        throw new Error(JSON.stringify({ first, second }));
      }
      if (!first.accepted && first.error?.code !== "StaleCommand") {
        throw new Error(JSON.stringify(first));
      }
      if (!second.accepted && second.error?.code !== "StaleCommand") {
        throw new Error(JSON.stringify(second));
      }
      for (const ack of [first, second]) {
        if (!ack.accepted) {
          continue;
        }
        const sequence = ack === first ? 1 : 2;
        const muted = ack === first;
        const retry = await connection.invoke(
          "SetMuted",
          command(session.sessionId, sequence, "session.mute", { muted }, { attachmentId, eventId: ack.eventId })
        );
        if (!retry.accepted || retry.eventId !== ack.eventId) {
          throw new Error(JSON.stringify({ ack, retry }));
        }
      }
      await connection.stop();
      break;
    }
    case "receipt-backwards": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      await connection.invoke("SendText", command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId }));
      await waitFor((evt) => evt.type === "agent.response.completed");
      const started = events.find((evt) => evt.type === "agent.response.started");
      const receipt = await connection.invoke(
        "ResponseReceived",
        command(session.sessionId, 2, "response.received", { textEndExclusive: 21 }, { attachmentId, responseId: started.responseId })
      );
      if (!receipt.accepted) {
        throw new Error(JSON.stringify(receipt));
      }
      const lower = await connection.invoke(
        "ResponseReceived",
        command(session.sessionId, 3, "response.received", { textEndExclusive: 0 }, { attachmentId, responseId: started.responseId })
      );
      if (lower.accepted || lower.error?.code !== "ValidationError") {
        throw new Error(JSON.stringify(lower));
      }
      await connection.stop();
      break;
    }
    case "dual-attach": {
      const firstSession = await createSession();
      const secondSession = await createSession();
      const connection = await connect();
      const first = await attachSession(connection, firstSession.sessionId);
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      await waitFor((evt) => evt.type === "session.ready");
      const denied = await connection.invoke(
        "Attach",
        command(secondSession.sessionId, 0, "session.attach", { lastServerSequence: null })
      );
      if (denied.accepted || denied.error?.code !== "SessionInUse") {
        throw new Error(JSON.stringify(denied));
      }
      const ping = await connection.invoke(
        "SendText",
        command(firstSession.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId: events[0].attachmentId })
      );
      if (!ping.accepted) {
        throw new Error(JSON.stringify(ping));
      }
      await connection.stop();
      break;
    }
    case "user-text-unknown-behavior": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const denied = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello", behavior: "drop" }, { attachmentId })
      );
      if (denied.accepted || denied.error?.code !== "ValidationError") {
        throw new Error(JSON.stringify(denied));
      }
      await connection.stop();
      break;
    }
    case "user-text-behavior-retry": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const eventId = uuid();
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId, eventId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const changed = await connection.invoke(
        "SendText",
        command(session.sessionId, 2, "user.text", { text: "Hello", behavior: "queue" }, { attachmentId, eventId })
      );
      if (changed.accepted || changed.error?.code !== "ProtocolError") {
        throw new Error(JSON.stringify(changed));
      }
      const retry = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId, eventId })
      );
      if (!retry.accepted || retry.eventId !== first.eventId) {
        throw new Error(JSON.stringify(retry));
      }
      await connection.stop();
      break;
    }
    case "user-text-queue": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Please hold the line" }, { attachmentId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const started = await waitForEvent((evt) => evt.type === "agent.response.started");
      const queued = await connection.invoke(
        "SendText",
        command(session.sessionId, 2, "user.text", { text: "queued later", behavior: "queue" }, { attachmentId })
      );
      if (!queued.accepted) {
        throw new Error(JSON.stringify(queued));
      }
      const stillLive = events.some(
        (evt) => evt.type === "agent.response.completed" && evt.responseId === started.responseId
      );
      if (stillLive) {
        throw new Error("queue superseded the live response");
      }
      await connection.stop();
      break;
    }
    case "user-text-interrupt-live": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Please hold the line" }, { attachmentId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const started = await waitForEvent((evt) => evt.type === "agent.response.started");
      const interrupt = await connection.invoke(
        "SendText",
        command(session.sessionId, 2, "user.text", { text: "Wait, stop", behavior: "interrupt" }, { attachmentId })
      );
      if (!interrupt.accepted) {
        throw new Error(JSON.stringify(interrupt));
      }
      const interrupted = await waitForEvent(
        (evt) => evt.type === "agent.response.interrupted" && evt.responseId === started.responseId
      );
      if (interrupted.payload?.reason !== "newText") {
        throw new Error(JSON.stringify(interrupted));
      }
      await connection.stop();
      break;
    }
    case "user-text-omit-interrupt-live": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Please hold the line" }, { attachmentId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const started = await waitForEvent((evt) => evt.type === "agent.response.started");
      const omitted = await connection.invoke(
        "SendText",
        command(session.sessionId, 2, "user.text", { text: "Wait, stop" }, { attachmentId })
      );
      if (!omitted.accepted) {
        throw new Error(JSON.stringify(omitted));
      }
      const interrupted = await waitForEvent(
        (evt) => evt.type === "agent.response.interrupted" && evt.responseId === started.responseId
      );
      if (interrupted.payload?.reason !== "newText") {
        throw new Error(JSON.stringify(interrupted));
      }
      await connection.stop();
      break;
    }
    case "user-text-interrupt-after-queue": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const first = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Please hold the line" }, { attachmentId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const started = await waitForEvent((evt) => evt.type === "agent.response.started");
      const queued = await connection.invoke(
        "SendText",
        command(session.sessionId, 2, "user.text", { text: "queued later", behavior: "queue" }, { attachmentId })
      );
      if (!queued.accepted) {
        throw new Error(JSON.stringify(queued));
      }
      const interrupt = await connection.invoke(
        "SendText",
        command(session.sessionId, 3, "user.text", { text: "take over", behavior: "interrupt" }, { attachmentId })
      );
      if (!interrupt.accepted) {
        throw new Error(JSON.stringify(interrupt));
      }
      await waitForEvent(
        (evt) => evt.type === "agent.response.interrupted" && evt.responseId === started.responseId
      );
      const page = await fetch(`${base}/api/v1/sessions/${session.sessionId}/messages?after=0`, {
        headers: await ownerHeaders()
      });
      if (!page.ok) {
        throw new Error(`history ${page.status}`);
      }
      const history = await page.json();
      const users = history.items.filter((item) => item.role === "user").map((item) => item.text);
      if (
        users[0] !== "Please hold the line" ||
        users[1] !== "queued later" ||
        users[2] !== "take over"
      ) {
        throw new Error(`queue order lost: ${JSON.stringify(users)}`);
      }
      await connection.stop();
      break;
    }
    case "cancel-response-unknown": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const missing = await connection.invoke(
        "CancelResponse",
        command(session.sessionId, 1, "agent.response.cancel", {}, { attachmentId })
      );
      if (missing.accepted || missing.error?.code !== "ValidationError") {
        throw new Error(JSON.stringify(missing));
      }
      const unknown = await connection.invoke(
        "CancelResponse",
        command(session.sessionId, 2, "agent.response.cancel", {}, { attachmentId, responseId: uuid() })
      );
      if (unknown.accepted || unknown.error?.code !== "ValidationError") {
        throw new Error(JSON.stringify(unknown));
      }
      await connection.stop();
      break;
    }
    case "cancel-response-idempotent": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const send = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId })
      );
      if (!send.accepted) {
        throw new Error(JSON.stringify(send));
      }
      const completed = await waitForEvent((evt) => evt.type === "agent.response.completed");
      const first = await connection.invoke(
        "CancelResponse",
        command(session.sessionId, 2, "agent.response.cancel", {}, { attachmentId, responseId: completed.responseId })
      );
      if (!first.accepted) {
        throw new Error(JSON.stringify(first));
      }
      const second = await connection.invoke(
        "CancelResponse",
        command(session.sessionId, 3, "agent.response.cancel", {}, { attachmentId, responseId: completed.responseId })
      );
      if (!second.accepted) {
        throw new Error(JSON.stringify(second));
      }
      await connection.stop();
      break;
    }
    case "cancel-response-stale": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const firstSend = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Hello" }, { attachmentId })
      );
      if (!firstSend.accepted) {
        throw new Error(JSON.stringify(firstSend));
      }
      const firstCompleted = await waitForEvent((evt) => evt.type === "agent.response.completed");
      const secondSend = await connection.invoke(
        "SendText",
        command(session.sessionId, 2, "user.text", { text: "Next" }, { attachmentId })
      );
      if (!secondSend.accepted) {
        throw new Error(JSON.stringify(secondSend));
      }
      const secondStarted = await waitForEvent(
        (evt) => evt.type === "agent.response.started" && evt.responseId !== firstCompleted.responseId
      );
      const stale = await connection.invoke(
        "CancelResponse",
        command(session.sessionId, 3, "agent.response.cancel", {}, { attachmentId, responseId: firstCompleted.responseId })
      );
      if (stale.accepted) {
        if (stale.error) {
          throw new Error(JSON.stringify(stale));
        }
      } else if (stale.error?.code !== "StaleCommand") {
        throw new Error(JSON.stringify(stale));
      }
      const secondTerminal = await waitForEvent(
        (evt) =>
          (evt.type === "agent.response.completed" || evt.type === "agent.response.interrupted") &&
          evt.responseId === secondStarted.responseId
      );
      if (stale.accepted && secondTerminal.type === "agent.response.interrupted") {
        throw new Error("stale cancel of R1 interrupted R2");
      }
      await connection.stop();
      break;
    }
    case "cancel-response-active": {
      const session = await createSession();
      const connection = await connect();
      await attachSession(connection, session.sessionId);
      await waitFor((evt) => evt.type === "session.ready");
      const attachmentId = events[0].attachmentId;
      const send = await connection.invoke(
        "SendText",
        command(session.sessionId, 1, "user.text", { text: "Please hold the line" }, { attachmentId })
      );
      if (!send.accepted) {
        throw new Error(JSON.stringify(send));
      }
      const started = await waitForEvent((evt) => evt.type === "agent.response.started");
      const cancel = await connection.invoke(
        "CancelResponse",
        command(session.sessionId, 2, "agent.response.cancel", {}, { attachmentId, responseId: started.responseId })
      );
      if (!cancel.accepted) {
        throw new Error(JSON.stringify(cancel));
      }
      await waitForEvent(
        (evt) => evt.type === "agent.response.interrupted" && evt.responseId === started.responseId
      );
      await connection.stop();
      break;
    }
    case "capacity": {
      const a = await createSession();
      const b = await createSession();
      const extra = await fetch(`${base}/api/v1/sessions`, {
        method: "POST",
        headers: await ownerHeaders({ "content-type": "application/json" }),
        body: JSON.stringify({ agentId: "examiner", mode: "text" })
      });
      if (!extra.ok) {
        throw new Error(`create should succeed, got ${extra.status}`);
      }
      const first = await connect();
      const attached = await attachSession(first, a.sessionId);
      if (!attached.accepted) {
        throw new Error(JSON.stringify(attached));
      }
      const second = await connect();
      const denied = await attachSession(second, b.sessionId);
      if (denied.accepted || denied.error?.code !== "SessionCapacityExceeded" || denied.error?.retryAfterMs !== 5000) {
        throw new Error(JSON.stringify(denied));
      }
      await second.stop();
      await first.stop();
      break;
    }
    case "ready-transports": {
      const session = await createSession();
      const connection = await connect();
      const attachAck = await attachSession(connection, session.sessionId);
      if (!attachAck.accepted) {
        throw new Error(JSON.stringify(attachAck));
      }
      const ready = await waitForEvent((evt) => evt.type === "session.ready");
      const encoded = JSON.stringify(ready);
      if (encoded.includes("ApiKey") || encoded.includes("OPENAI") || encoded.includes("OPENROUTER")) {
        throw new Error("ready payload leaked provider secrets");
      }
      const stt = ready.payload?.capabilities?.stt;
      const tts = ready.payload?.capabilities?.tts;
      if (stt?.transport !== "serverAudio" || tts?.transport !== "serverAudio") {
        throw new Error(`missing additive transports: ${encoded}`);
      }
      if (typeof stt.streamingAudio !== "boolean" || typeof tts.timingMarks !== "boolean") {
        throw new Error(`legacy capability booleans missing: ${encoded}`);
      }
      if (ready.payload?.agent?.voiceAvailable !== true) {
        throw new Error(`voiceAvailable expected true: ${encoded}`);
      }
      await connection.stop();
      break;
    }
    default:
      throw new Error(`unknown scenario ${scenario}`);
  }
}

function waitFor(match, timeoutMs = 8000) {
  return waitForEvent(match, timeoutMs).then(() => undefined);
}

function waitForEvent(match, timeoutMs = 8000) {
  const started = Date.now();
  return new Promise((resolve, reject) => {
    const timer = setInterval(() => {
      const found = events.find(match);
      if (found) {
        clearInterval(timer);
        resolve(found);
      } else if (Date.now() - started > timeoutMs) {
        clearInterval(timer);
        reject(new Error(`timeout waiting; saw ${events.map((evt) => evt.type).join(",")}`));
      }
    }, 20);
  });
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
