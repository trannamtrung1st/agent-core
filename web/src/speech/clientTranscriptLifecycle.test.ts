import { describe, expect, it } from "vitest";
import { FakeSpeechRecognizer } from "./fakeSpeechAdapters";
import { createSpeechTransportService } from "./speechTransport";
import { ClientTranscriptLifecycle } from "./clientTranscriptLifecycle";
import type { ClientSpeechEvidence } from "./clientSpeechRecognizer";

function setup() {
  const sent: ClientSpeechEvidence[] = [];
  const fake = new FakeSpeechRecognizer();
  const transport = createSpeechTransportService(fake);
  const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
  return { sent, fake, life };
}

describe("ClientTranscriptLifecycle", () => {
  it("mute abandons speculative partials without a final", async () => {
    const { sent, fake, life } = setup();
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    fake.emit({ kind: "started", utteranceId: "u1" });
    fake.emit({ kind: "partial", utteranceId: "u1", revision: 1, text: "hello" });
    await life.mute();
    expect(sent.some((item) => item.kind === "final")).toBe(false);
    expect(fake.isRunning).toBe(false);
  });

  it("unmute starts a fresh epoch and rejects late prior-epoch events", async () => {
    const { sent, fake, life } = setup();
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    const oldEpoch = life.currentEpoch();
    fake.emit({ kind: "started", utteranceId: "u1" });
    await life.mute();
    await life.unmute("a1");
    life.ingest({ kind: "partial", utteranceId: "u1", revision: 2, text: "stale" }, oldEpoch);
    expect(sent.some((item) => item.text === "stale")).toBe(false);
    fake.emit({ kind: "started", utteranceId: "u2" });
    fake.emit({ kind: "partial", utteranceId: "u2", revision: 1, text: "fresh" });
    fake.emit({ kind: "ended", utteranceId: "u2" });
    expect(sent.some((item) => item.text?.includes("fresh"))).toBe(true);
  });

  it("mode to text stops recognition and clears partials", async () => {
    const { sent, fake, life } = setup();
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    fake.emit({ kind: "started", utteranceId: "u1" });
    fake.emit({ kind: "partial", utteranceId: "u1", revision: 1, text: "draft" });
    await life.exitVoice();
    expect(fake.isRunning).toBe(false);
    expect(sent.some((item) => item.kind === "final")).toBe(false);
  });

  it("disconnect does not replay old partials after reconnect", async () => {
    const { sent, fake, life } = setup();
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    fake.emit({ kind: "started", utteranceId: "u1" });
    fake.emit({ kind: "partial", utteranceId: "u1", revision: 1, text: "old" });
    const previous = sent.length;
    await life.disconnect();
    await life.reconnect({ attachmentId: "a2", mode: "voice", muted: false });
    expect(sent.length).toBe(previous);
    expect(sent.some((item) => item.kind === "final" && item.text === "old")).toBe(false);
  });
});
