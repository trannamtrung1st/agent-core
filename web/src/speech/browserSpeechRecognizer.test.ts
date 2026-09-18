import { afterEach, describe, expect, it, vi } from "vitest";
import { BrowserSpeechRecognizer, browserSpeechRecognitionSupported } from "./browserSpeechRecognizer";

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

describe("BrowserSpeechRecognizer", () => {
  afterEach(() => {
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

  it("maps mock recognition events without a speech cloud", async () => {
    const holder: { current: MockRecognition | null } = { current: null };
    class TrackingRecognition extends MockRecognition {
      constructor() {
        super();
        holder.current = this;
      }
    }
    vi.stubGlobal("SpeechRecognition", TrackingRecognition);
    vi.stubGlobal("webkitSpeechRecognition", undefined);
    expect(browserSpeechRecognitionSupported()).toBe(true);
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
    });
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "hello", confidence: 0.8 } }]
    });
    await adapter.stop();
    expect(kinds).toEqual(["started", "partial", "ended"]);
    expect(texts).toEqual(["hello"]);
    expect(holder.current?.continuous).toBe(true);
    expect(holder.current?.interimResults).toBe(true);
  });

  it("maps not-allowed to SpeechPermissionDenied", async () => {
    const holder: { current: MockRecognition | null } = { current: null };
    class TrackingRecognition extends MockRecognition {
      constructor() {
        super();
        holder.current = this;
      }
    }
    vi.stubGlobal("SpeechRecognition", TrackingRecognition);
    const adapter = new BrowserSpeechRecognizer();
    const errors: string[] = [];
    const kinds: string[] = [];
    await adapter.start({
      onEvidence: (evidence) => kinds.push(evidence.kind),
      onError: (error) => errors.push(error.code)
    });
    holder.current?.onerror?.({ error: "not-allowed" });
    expect(errors).toEqual(["SpeechPermissionDenied"]);
    expect(kinds).toContain("failed");
  });
});
