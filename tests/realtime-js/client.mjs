import { HttpTransportType, HubConnectionBuilder } from "@microsoft/signalr";
import { MessagePackHubProtocol } from "@microsoft/signalr-protocol-msgpack";

const base = process.env.BASE_URL;
const scenario = process.argv[2];
if (!base || !scenario) {
  console.error("usage: node client.mjs <scenario>");
  process.exit(2);
}

const events = [];

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
  return body;
}

async function connect() {
  const connection = new HubConnectionBuilder()
    .withUrl(`${base}/hubs/session`, {
      skipNegotiation: true,
      transport: HttpTransportType.WebSockets
    })
    .withHubProtocol(new MessagePackHubProtocol())
    .build();
  connection.on("SessionEvent", (evt) => events.push(evt));
  await connection.start();
  return connection;
}

async function createSession() {
  const response = await fetch(`${base}/api/v1/sessions`, {
    method: "POST",
    headers: { "content-type": "application/json" },
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
      const attach = await connection.invoke(
        "Attach",
        command(session.sessionId, 0, "session.attach", { lastServerSequence: null })
      );
      if (!attach.accepted) {
        throw new Error(JSON.stringify(attach));
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
      const attach = await first.invoke(
        "Attach",
        command(session.sessionId, 0, "session.attach", { lastServerSequence: null })
      );
      if (!attach.accepted) {
        throw new Error(JSON.stringify(attach));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
      await connection.invoke("Attach", command(session.sessionId, 0, "session.attach", { lastServerSequence: null }));
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
    case "capacity": {
      const a = await createSession();
      const b = await createSession();
      const extra = await fetch(`${base}/api/v1/sessions`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ agentId: "examiner", mode: "text" })
      });
      if (!extra.ok) {
        throw new Error(`create should succeed, got ${extra.status}`);
      }
      const first = await connect();
      const attached = await first.invoke("Attach", command(a.sessionId, 0, "session.attach", { lastServerSequence: null }));
      if (!attached.accepted) {
        throw new Error(JSON.stringify(attached));
      }
      const second = await connect();
      const denied = await second.invoke("Attach", command(b.sessionId, 0, "session.attach", { lastServerSequence: null }));
      if (denied.accepted || denied.error?.code !== "SessionCapacityExceeded" || denied.error?.retryAfterMs !== 5000) {
        throw new Error(JSON.stringify(denied));
      }
      await second.stop();
      await first.stop();
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
