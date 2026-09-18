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

  it("keeps restarting through many idle native onends without surfacing restart-limit errors", async () => {
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
    for (let index = 0; index < 20; index += 1) {
      holder.current?.onend?.(new Event("end"));
      await vi.advanceTimersByTimeAsync(2000);
    }
    expect(errors).toEqual([]);
    expect(holder.starts).toBe(21);
    await adapter.cancel();
  });

  it("backs off idle native restarts until the delay cap is reached", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const adapter = new BrowserSpeechRecognizer();
    await adapter.start({
      onEvidence: () => undefined,
      onError: () => undefined,
      onRecognitionEnded: () => undefined
    });
    const advanceForNextRestart = async (expectedDelayMs: number) => {
      holder.current?.onend?.(new Event("end"));
      const startsBefore = holder.starts;
      if (expectedDelayMs === 0) {
        await vi.advanceTimersByTimeAsync(0);
      } else {
        await vi.advanceTimersByTimeAsync(expectedDelayMs - 1);
        expect(holder.starts).toBe(startsBefore);
        await vi.advanceTimersByTimeAsync(1);
      }
      expect(holder.starts).toBe(startsBefore + 1);
    };
    await advanceForNextRestart(0);
    await advanceForNextRestart(250);
    await advanceForNextRestart(500);
    await advanceForNextRestart(1000);
    await advanceForNextRestart(2000);
    await advanceForNextRestart(2000);
    await adapter.cancel();
  });

  it("does not surface restart-limit for noise-only speechstart with repeated native onends", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const errors: string[] = [];
    const adapter = new BrowserSpeechRecognizer();
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), (error) => errors.push(error.code));
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    for (let index = 0; index < 6; index += 1) {
      holder.current?.onend?.(new Event("end"));
      await vi.advanceTimersByTimeAsync(2000);
    }
    expect(errors).toEqual([]);
    expect(life.isBlocked()).toBe(false);
    await vi.advanceTimersByTimeAsync(4000);
    expect(sent.filter((item) => item.kind === "failed")).toHaveLength(1);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(0);
    await adapter.cancel();
  });

  it("blocks voice through lifecycle after repeated mid-utterance onends with recognized text", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const errors: string[] = [];
    const adapter = new BrowserSpeechRecognizer({ transcriptInactivityMs: 60_000 });
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, () => undefined, (error) => errors.push(error.code));
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "I was saying" } }]
    });
    for (let index = 0; index < 4; index += 1) {
      holder.current?.onend?.(new Event("end"));
      await vi.advanceTimersByTimeAsync(500);
    }
    expect(errors).toEqual(["SpeechRecognitionRestartLimit"]);
    expect(life.isListening()).toBe(false);
    expect(life.isBlocked()).toBe(true);
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

describe("Browser STT application endpointing through lifecycle", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("commits two turns without native speechend when transcript inactivity closes each utterance", async () => {
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
      results: [{ isFinal: false, 0: { transcript: "first answer" } }]
    });
    await vi.advanceTimersByTimeAsync(1800);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    expect(sent.find((item) => item.kind === "final")?.text).toBe("first answer");
    const firstUtterance = sent.find((item) => item.kind === "started")?.utteranceId;

    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "second answer" } }]
    });
    const started = sent.filter((item) => item.kind === "started");
    expect(started).toHaveLength(2);
    expect(started[1]?.utteranceId).not.toBe(firstUtterance);

    await vi.advanceTimersByTimeAsync(1800);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(2);
    expect(sent.filter((item) => item.kind === "final")[1]?.text).toBe("second answer");
    await adapter.cancel();
  });

  it("discards a noise-only utterance with failed evidence after no recognized text", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer();
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    await vi.advanceTimersByTimeAsync(4000);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(0);
    expect(sent.filter((item) => item.kind === "failed")).toHaveLength(1);
    expect(sent.filter((item) => item.kind === "ended")).toHaveLength(0);
    await adapter.cancel();
  });
  it("finalizes after inactivity when Chrome repeats identical interim text", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer({ transcriptInactivityMs: 1800 });
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    for (let index = 0; index < 6; index += 1) {
      holder.current?.onresult?.({
        resultIndex: 0,
        results: [{ isFinal: false, 0: { transcript: "it" } }]
      });
      await vi.advanceTimersByTimeAsync(500);
    }
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    expect(sent.find((item) => item.kind === "final")?.text).toBe("it");
    await adapter.cancel();
  });

  it("still finalizes after native restart without clearing inactivity timing", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer({ transcriptInactivityMs: 1800 });
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "it" } }]
    });
    await vi.advanceTimersByTimeAsync(900);
    holder.current?.onend?.(new Event("end"));
    await vi.advanceTimersByTimeAsync(0);
    await vi.advanceTimersByTimeAsync(900);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    expect(sent.find((item) => item.kind === "final")?.text).toBe("it");
    await adapter.cancel();
  });
});

describe("Browser STT agent-output suspension", () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("does not ingest user evidence while suspended for agent output", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer();
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(
      transport,
      (evidence) => sent.push(evidence),
      () => undefined,
      undefined,
      () => undefined
    );
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "hello" } }]
    });
    await life.suspendForAgentOutput();
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "agent echo" } }]
    });
    expect(sent.some((item) => item.text === "agent echo")).toBe(false);
    await adapter.cancel();
  });

  it("resumes with a fresh epoch and recognizes the next user turn", async () => {
    vi.useFakeTimers();
    const holder = installMock();
    const sent: ClientSpeechEvidence[] = [];
    const adapter = new BrowserSpeechRecognizer({ transcriptInactivityMs: 1800 });
    const transport = createSpeechTransportService(adapter);
    const life = new ClientTranscriptLifecycle(transport, (evidence) => sent.push(evidence), () => undefined);
    await life.enterVoice({ attachmentId: "a1", mode: "voice", muted: false, language: "en" });
    holder.current?.onspeechstart?.(new Event("speechstart"));
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "first" } }]
    });
    await vi.advanceTimersByTimeAsync(1800);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    await life.suspendForAgentOutput();
    await life.resumeAfterAgentOutput();
    holder.current?.onresult?.({
      resultIndex: 0,
      results: [{ isFinal: false, 0: { transcript: "second turn" } }]
    });
    const started = sent.filter((item) => item.kind === "started");
    expect(started.length).toBeGreaterThanOrEqual(2);
    await vi.advanceTimersByTimeAsync(1800);
    expect(sent.filter((item) => item.kind === "final").map((item) => item.text)).toContain("second turn");
    await adapter.cancel();
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
      results: [{ isFinal: true, 0: { transcript: "something important" } }]
    });
    holder.current?.onspeechend?.(new Event("speechend"));
    await vi.advanceTimersByTimeAsync(300);
    expect(sent.filter((item) => item.kind === "final")).toHaveLength(1);
    expect(sent.find((item) => item.kind === "final")?.text).toBe("I was saying something important");
    await adapter.cancel();
  });

  it("dedupes a repeated-prefix final after mid-utterance native onend restart", async () => {
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
