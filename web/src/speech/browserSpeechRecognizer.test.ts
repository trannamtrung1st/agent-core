import { afterEach, describe, expect, it, vi } from "vitest";
import { BrowserSpeechRecognizer, browserSpeechRecognitionSupported } from "./browserSpeechRecognizer";
import { createSpeechTransportService } from "./speechTransport";
import { ClientTranscriptLifecycle } from "./clientTranscriptLifecycle";
import type { ClientSpeechEvidence } from "./clientSpeechRecognizer";

class MockRecognition {
  continuous = false;
  interimResults = false;
  lang = "";
  onstart: ((event: Event) => void) | null = null;
  onend: ((event: Event) => void) | null = null;
  onerror: ((event: { error?: string }) => void) | null = null;
  onresult: ((event: {
    resultIndex: number;
    results: ArrayLike<{ isFinal: boolean; 0?: { transcript?: string; confidence?: number } }>;
  }) => void) | null = null;
  onspeechstart: ((event: Event) => void) | null = null;
  onspeechend: ((event: Event) => void) | null = null;
  startCalls = 0;
  stopCalls = 0;
  abortCalls = 0;

  start(): void {
    this.startCalls += 1;
    this.onstart?.(new Event("start"));
  }

  stop(): void {
    this.stopCalls += 1;
    this.onend?.(new Event("end"));
  }

  abort(): void {
    this.abortCalls += 1;
    this.onend?.(new Event("end"));
  }
}

function installMock() {
  const holder: { current: MockRecognition | null; starts: number } = { current: null, starts: 0 };
  class TrackingRecognition extends MockRecognition {
    constructor() {
      super();
      holder.current = this;
    }

    override start(): void {
      holder.starts += 1;
      super.start();
    }
  }
  vi.stubGlobal("SpeechRecognition", TrackingRecognition);
  vi.stubGlobal("webkitSpeechRecognition", undefined);
  return holder;
}

describe("BrowserSpeechRecognizer", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("reports unsupported without constructing a native recognizer", async () => {
    vi.stubGlobal("SpeechRecognition", undefined);
    vi.stubGlobal("webkitSpeechRecognition", undefined);
    expect(browserSpeechRecognitionSupported()).toBe(false);
    const adapter = new BrowserSpeechRecognizer();
    const errors: string[] = [];
    await expect(
      adapter.start({
        onEvidence: () => undefined,
        onError: (error) => errors.push(error.code)
      })
    ).rejects.toMatchObject({ code: "SpeechUnsupported" });
    expect(errors).toEqual(["SpeechUnsupported"]);
  });

  it("maps speech boundaries and native finals without treating session start as an utterance", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    const kinds: string[] = [];
    const texts: string[] = [];
    await adapter.start({
      onEvidence: (evidence) => {
        kinds.push(evidence.kind);
        if (evidence.text) {
          texts.push(evidence.text);
        }
      },
      onError: () => undefined
    }, { language: "en" });
    expect(holder.current?.lang).toBe("en");
    expect(kinds).toEqual([]);
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "hello", confidence: 0.8 } }]
    });
    holder.current?.onspeechend?.(new Event("speechend"));
    expect(kinds).toEqual(["started", "partial"]);
    await vi.advanceTimersByTimeAsync(300);
    expect(kinds).toEqual(["started", "partial", "ended"]);
    await adapter.stop();
    expect(texts).toEqual(["hello"]);
    expect(holder.current?.continuous).toBe(true);
    expect(holder.current?.interimResults).toBe(true);
  });

  it("waits for a native final after speechend before closing the utterance", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    const events: ClientSpeechEvidence[] = [];
    await adapter.start({
      onEvidence: (evidence) => events.push(evidence),
      onError: () => undefined
    });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "hello" } }]
    });
    holder.current?.onspeechend?.(new Event("speechend"));
    expect(events.map((item) => item.kind)).toEqual(["started", "partial"]);
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: true, 0: { transcript: "hello world" } }]
    });
    expect(events.map((item) => item.kind)).toEqual(["started", "partial", "final", "ended"]);
    expect(events.find((item) => item.kind === "final")?.text).toBe("hello world");
    await vi.advanceTimersByTimeAsync(300);
    expect(events.filter((item) => item.kind === "ended")).toHaveLength(1);
    await adapter.cancel();
  });

  it("emits native finals from every changed result", async () => {
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    const events: ClientSpeechEvidence[] = [];
    await adapter.start({
      onEvidence: (evidence) => events.push(evidence),
      onError: () => undefined
    });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: true, 0: { transcript: "this is" } }]
    });
    holder.current?.onresult?.({
      resultIndex: 1,
      results: [
        { isFinal: true, 0: { transcript: "this is" } },
        { isFinal: true, 0: { transcript: "a long" } }
      ]
    });
    holder.current?.onresult?.({
      resultIndex: 2,
      results: [
        { isFinal: true, 0: { transcript: "this is" } },
        { isFinal: true, 0: { transcript: "a long" } },
        { isFinal: false, 0: { transcript: "sentence" } }
      ]
    });
    expect(events.filter((item) => item.kind === "final").map((item) => item.text)).toEqual(["this is", "a long"]);
    expect(events.filter((item) => item.kind === "partial").map((item) => item.text)).toEqual(["sentence"]);
  });

  it("restarts after unexpected native onend while still wanted", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    let recognitionEnded = 0;
    await adapter.start({
      onEvidence: () => undefined,
      onError: () => undefined,
      onRecognitionEnded: () => {
        recognitionEnded += 1;
      }
    });
    expect(holder.starts).toBe(1);
    holder.current?.onend?.(new Event("end"));
    expect(recognitionEnded).toBe(1);
    await vi.advanceTimersByTimeAsync(0);
    expect(holder.starts).toBe(2);
    await adapter.cancel();
  });

  it("does not end an open utterance on native onend without a pending speechend", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    const events: ClientSpeechEvidence[] = [];
    let recognitionEnded = 0;
    await adapter.start({
      onEvidence: (evidence) => events.push(evidence),
      onError: () => undefined,
      onRecognitionEnded: () => {
        recognitionEnded += 1;
      }
    });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    const utteranceId = events.find((item) => item.kind === "started")?.utteranceId;
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "I was saying" } }]
    });
    holder.current?.onend?.(new Event("end"));
    expect(recognitionEnded).toBe(1);
    expect(events.some((item) => item.kind === "ended")).toBe(false);
    await vi.advanceTimersByTimeAsync(0);
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: true, 0: { transcript: "I was saying something important" } }]
    });
    holder.current?.onspeechend?.(new Event("speechend"));
    await vi.advanceTimersByTimeAsync(300);
    expect(events.filter((item) => item.kind === "started")).toHaveLength(1);
    expect(events.at(-1)?.kind).toBe("ended");
    expect(events.filter((item) => item.kind === "started")[0]?.utteranceId).toBe(utteranceId);
    await adapter.cancel();
  });

  it("stops after three idle native onends without speech progress", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    const errors: string[] = [];
    await adapter.start({
      onEvidence: () => undefined,
      onError: (error) => errors.push(error.code),
      onRecognitionEnded: () => undefined
    });
    expect(holder.starts).toBe(1);
    holder.current?.onend?.(new Event("end"));
    await vi.advanceTimersByTimeAsync(0);
    expect(holder.starts).toBe(2);
    holder.current?.onend?.(new Event("end"));
    await vi.advanceTimersByTimeAsync(250);
    expect(holder.starts).toBe(3);
    holder.current?.onend?.(new Event("end"));
    expect(errors).toEqual(["SpeechRecognitionRestartLimit"]);
    await vi.advanceTimersByTimeAsync(500);
    expect(holder.starts).toBe(3);
    await adapter.cancel();
  });

  it("maps not-allowed to SpeechPermissionDenied", async () => {
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    const errors: string[] = [];
    const kinds: string[] = [];
    await adapter.start({
      onEvidence: (evidence) => kinds.push(evidence.kind),
      onError: (error) => errors.push(error.code)
    });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onerror?.({ error: "not-allowed" });
    expect(errors).toEqual(["SpeechPermissionDenied"]);
    expect(kinds).toContain("failed");
  });
});

describe("Browser STT native finals through the application accumulator", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("concatenates native finals into one application final", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer();
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: true, 0: { transcript: "this is" } }]
    });
    holder.current?.onresult?.({
      resultIndex: 1,
      results: [
        { isFinal: true, 0: { transcript: "this is" } },
        { isFinal: true, 0: { transcript: "a long" } }
      ]
    });
    holder.current?.onresult?.({
      resultIndex: 2,
      results: [
        { isFinal: true, 0: { transcript: "this is" } },
        { isFinal: true, 0: { transcript: "a long" } },
        { isFinal: false, 0: { transcript: "sentence" } }
      ]
    });
    holder.current?.onspeechend?.(new Event("speechend"));
    await vi.advanceTimersByTimeAsync(300);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    expect(sent.find((item) => item.kind === "final")?.text).toBe("this is a long sentence");
  });

  it("preserves one application final across mid-utterance native onend restart", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer();
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "I was saying" } }]
    });
    holder.current?.onend?.(new Event("end"));
    expect(sent.some((item) => item.kind === "ended")).toBe(false);
    await vi.advanceTimersByTimeAsync(0);
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: true, 0: { transcript: "I was saying something important" } }]
    });
    holder.current?.onspeechend?.(new Event("speechend"));
    await vi.advanceTimersByTimeAsync(300);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    expect(sent.find((item) => item.kind === "final")?.text).toBe("I was saying something important");
    await adapter.cancel();
  });

  it("commits the late native final when speechend precedes the final result", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer();
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "hello" } }]
    });
    holder.current?.onspeechend?.(new Event("speechend"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: true, 0: { transcript: "hello world" } }]
    });
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    expect(sent.find((item) => item.kind === "final")?.text).toBe("hello world");
    await adapter.cancel();
  });
});
