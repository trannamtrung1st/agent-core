import { afterEach, describe, expect, it, vi } from "vitest";
import { speechError } from "./errors";
import { BrowserSpeechRecognizer } from "./browserSpeechRecognizer";
import { FakeSpeechRecognizer } from "./fakeSpeechAdapters";
import { createSpeechTransportService } from "./speechTransport";
import { ClientTranscriptLifecycle } from "./clientTranscriptLifecycle";
import type { ClientSpeechEvidence } from "./clientSpeechRecognizer";

function setup() {
  const sent: ClientSpeechEvidence[] = [];
  const errors: string[] = [];
  const fake = new FakeSpeechRecognizer();
  const transport = createSpeechTransportService(fake);
  const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), (error) => errors.push(error.code));
  return { sent, fake, life, errors };
}

describe("ClientTranscriptLifecycle", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

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

  it("surfaces recognizer errors and stops listening", async () => {
    const { fake, life, errors } = setup();
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    expect(life.isListening()).toBe(true);
    fake.fail("SpeechPermissionDenied");
    expect(errors).toEqual(["SpeechPermissionDenied"]);
    expect(life.isListening()).toBe(false);
    expect(life.isBlocked()).toBe(true);
  });

  it("retryRecognition restarts listening after a block and stays blocked when retry fails", async () => {
    class FailOnStartRecognizer extends FakeSpeechRecognizer {
      async start(listener: Parameters<FakeSpeechRecognizer["start"]>[0]): Promise<void> {
        listener.onError(speechError("SpeechRecognitionUnavailable"));
      }
    }

    const errors: string[] = [];
    const fake = new FailOnStartRecognizer();
    const transport = createSpeechTransportService(fake);
    transport.setActiveInputTransport("clientTranscript");
    const life = new ClientTranscriptLifecycle(transport, () => undefined, (error) => errors.push(error.code));

    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    expect(life.isBlocked()).toBe(true);

    const recovered = await life.retryRecognition();
    expect(recovered).toBe(false);
    expect(life.isBlocked()).toBe(true);
    expect(life.isListening()).toBe(false);
  });

  it("retryRecognition clears blocked when recognition restarts", async () => {
    const { fake, life } = setup();
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    fake.fail("SpeechRecognitionUnavailable");
    expect(life.isBlocked()).toBe(true);

    const recovered = await life.retryRecognition();
    expect(recovered).toBe(true);
    expect(life.isBlocked()).toBe(false);
    expect(life.isListening()).toBe(true);
  });

  it("enterVoice leaves blocked when startInput throws after reporting an error", async () => {
    class ThrowOnStartRecognizer extends FakeSpeechRecognizer {
      async start(listener: Parameters<FakeSpeechRecognizer["start"]>[0]): Promise<void> {
        listener.onError(speechError("SpeechUnsupported"));
        throw new Error("start failed");
      }
    }

    const fake = new ThrowOnStartRecognizer();
    const transport = createSpeechTransportService(fake);
    transport.setActiveInputTransport("clientTranscript");
    const life = new ClientTranscriptLifecycle(transport, () => undefined, () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false });
    expect(life.isBlocked()).toBe(true);
    expect(life.isListening()).toBe(false);
  });

  it("blocks voice when the browser adapter hits the idle restart cap", async () => {
    vi.useFakeTimers();
    class MockRecognition {
      onend: ((event: Event) => void) | null = null;
      continuous = false;
      interimResults = false;
      lang = "";
      onstart: ((event: Event) => void) | null = null;
      onerror: ((event: { error?: string }) => void) | null = null;
      onresult = null;
      onspeechstart = null;
      onspeechend = null;
      start(): void {
        this.onstart?.(new Event("start"));
      }
      stop(): void {
        this.onend?.(new Event("end"));
      }
      abort(): void {
        this.onend?.(new Event("end"));
      }
    }
    const holder: { current: MockRecognition | null } = { current: null };
    class TrackingRecognition extends MockRecognition {
      constructor() {
        super();
        holder.current = this;
      }
    }
    vi.stubGlobal("SpeechRecognition", TrackingRecognition);
    const errors: string[] = [];
    const adapter = new BrowserSpeechRecognizer();
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, () => undefined, (error) => errors.push(error.code));
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onend?.(new Event("end"));
    await vi.advanceTimersByTimeAsync(0);
    holder.current?.onend?.(new Event("end"));
    await vi.advanceTimersByTimeAsync(250);
    holder.current?.onend?.(new Event("end"));
    expect(errors).toEqual(["SpeechRecognitionRestartLimit"]);
    expect(life.isListening()).toBe(false);
    expect(life.isBlocked()).toBe(true);
  });
});
