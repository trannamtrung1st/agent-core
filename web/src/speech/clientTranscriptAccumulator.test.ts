import { beforeEach, describe, expect, it } from "vitest";
import { ClientTranscriptAccumulator, mergeRestartContinuation } from "./clientTranscriptAccumulator";
import { resetSpeechObservations, snapshotSpeechObservations } from "./speechObservability";

const gate = { attachmentId: "att-1", epoch: 1, mode: "voice" as const, muted: false };

function create(now = { t: 0 }) {
  const sent: string[] = [];
  const texts: string[] = [];
  const errors: string[] = [];
  const acc = new ClientTranscriptAccumulator(
    (evidence) => {
      sent.push(evidence.kind);
      if (evidence.text) {
        texts.push(evidence.text);
      }
    },
    (error) => errors.push(error.code),
    { now: () => now.t, minPartialIntervalMs: 100, maxRestarts: 2, newUtteranceId: () => "utt-1" }
  );
  acc.setGate(gate);
  return { acc, sent, texts, errors, now };
}

describe("mergeRestartContinuation", () => {
  it("appends continuation-only finals and dedupes repeated prefixes", () => {
    expect(mergeRestartContinuation("I was saying", "something important")).toBe("I was saying something important");
    expect(mergeRestartContinuation("I was saying", "I was saying something important")).toBe(
      "I was saying something important"
    );
    expect(mergeRestartContinuation("I was saying", "saying something important")).toBe("I was saying something important");
  });

  it("does not treat single-character overlaps as continuation", () => {
    expect(mergeRestartContinuation("I like pizza", "and pasta")).toBe("I like pizza and pasta");
    expect(mergeRestartContinuation("the car", "runs fast")).toBe("the car runs fast");
  });

  it("does not confuse whole words with character-prefix substrings", () => {
    expect(mergeRestartContinuation("candy", "can")).toBe("candy can");
    expect(mergeRestartContinuation("station", "stat")).toBe("station stat");
    expect(mergeRestartContinuation("I like", "I liked")).toBe("I like I liked");
  });
});

describe("ClientTranscriptAccumulator normal accumulation", () => {
  beforeEach(() => {
    resetSpeechObservations();
  });

  it("concatenates stable chunks with spaces without restart stitching", () => {
    const { acc, texts } = create();
    acc.startUtterance();
    acc.ingestStableChunk("I like pizza");
    acc.ingestStableChunk("and pasta");
    acc.endUtterance();
    expect(texts).toContain("I like pizza and pasta");
  });
});

describe("ClientTranscriptAccumulator", () => {
  beforeEach(() => {
    resetSpeechObservations();
  });

  it("streams interims and concatenates multiple browser-final chunks into one application final", () => {
    const { acc, sent, texts, now } = create();
    acc.startUtterance();
    acc.ingestInterim("hel");
    now.t += 100;
    acc.ingestInterim("hello there");
    acc.ingestStableChunk("hello there");
    now.t += 100;
    acc.ingestStableChunk("friend");
    acc.endUtterance(400);
    expect(sent.filter((kind) => kind === "final")).toEqual(["final"]);
    expect(texts.at(-1)).toBe("hello there friend");
    expect(sent.filter((kind) => kind === "partial").length).toBeGreaterThan(0);
    expect(sent.at(-1)).toBe("ended");
    const observations = snapshotSpeechObservations();
    expect(observations.some((item) => item.name === "speech.partial.count")).toBe(true);
    expect(JSON.stringify(observations)).not.toContain("hello there");
  });

  it("emits one application final before ended without a second turn", () => {
    const { acc, sent } = create();
    acc.startUtterance();
    acc.ingestStableChunk("done");
    acc.commitApplicationFinal();
    acc.endUtterance();
    expect(sent.filter((kind) => kind === "final")).toHaveLength(1);
    expect(sent.filter((kind) => kind === "ended")).toHaveLength(1);
  });

  it("does not consume the restart budget when no transcript has been recognized", () => {
    const { acc, errors } = create();
    acc.startUtterance();
    for (let index = 0; index < 6; index += 1) {
      expect(acc.unexpectedRestart()).toBe(true);
    }
    expect(errors).toEqual([]);
  });

  it("preserves the accumulator across bounded restarts without a second final", () => {
    const { acc, sent, texts } = create();
    acc.startUtterance();
    acc.ingestStableChunk("keep me");
    expect(acc.unexpectedRestart()).toBe(true);
    expect(acc.unexpectedRestart()).toBe(true);
    acc.endUtterance();
    expect(sent.filter((kind) => kind === "started")).toHaveLength(1);
    expect(sent.filter((kind) => kind === "final")).toHaveLength(1);
    expect(texts).toContain("keep me");
  });

  it("stitches interim prefix across unexpected restart onto continuation-only finals", () => {
    const { acc, sent, texts, now } = create();
    acc.startUtterance();
    acc.ingestInterim("I was saying");
    now.t += 100;
    expect(acc.unexpectedRestart()).toBe(true);
    acc.ingestStableChunk("something important");
    acc.endUtterance();
    expect(sent.filter((kind) => kind === "started")).toHaveLength(1);
    expect(sent.filter((kind) => kind === "final")).toHaveLength(1);
    expect(texts).toContain("I was saying something important");
  });

  it("surfaces SpeechRecognitionRestartLimit without a duplicate turn", () => {
    const { acc, sent, errors } = create();
    acc.startUtterance();
    acc.ingestStableChunk("partial thought");
    expect(acc.unexpectedRestart()).toBe(true);
    expect(acc.unexpectedRestart()).toBe(true);
    expect(acc.unexpectedRestart()).toBe(false);
    expect(errors).toEqual(["SpeechRecognitionRestartLimit"]);
    expect(sent.filter((kind) => kind === "final")).toHaveLength(0);
    expect(sent.filter((kind) => kind === "failed")).toHaveLength(1);
    expect(snapshotSpeechObservations().some((item) => item.name === "speech.error.code" && item.code === "SpeechRecognitionUnavailable")).toBe(true);
    expect(JSON.stringify(snapshotSpeechObservations())).not.toContain("partial thought");
  });

  it("does not replay stale partials after a reconnect epoch", () => {
    const { acc, sent, now } = create();
    acc.startUtterance();
    acc.ingestInterim("stale");
    acc.beginSession({ ...gate, epoch: 2 });
    acc.ingestInterim("stale", 1);
    now.t += 100;
    acc.ingestInterim("stale", 1);
    expect(sent.filter((kind) => kind === "partial")).toHaveLength(1);
    acc.startUtterance("utt-2");
    now.t += 100;
    acc.ingestInterim("fresh", 2);
    expect(sent.filter((kind) => kind === "partial").at(-1)).toBe("partial");
    expect(acc.sent.at(-1)?.text).toBe("fresh");
    expect(acc.sent.at(-1)?.utteranceId).toBe("utt-2");
  });

  it("coalesces partials and ignores mute", () => {
    const { acc, sent, now } = create();
    acc.startUtterance();
    acc.ingestInterim("a");
    acc.ingestInterim("ab");
    expect(sent.filter((kind) => kind === "partial")).toHaveLength(1);
    now.t += 100;
    acc.ingestInterim("abc");
    expect(sent.filter((kind) => kind === "partial")).toHaveLength(2);
    acc.setGate({ ...gate, muted: true });
    now.t += 100;
    acc.ingestInterim("secret");
    expect(acc.sent.some((item) => item.text === "secret")).toBe(false);
  });

  it("finalizes open utterance on mute instead of failing", () => {
    const { acc, sent, texts } = create();
    acc.startUtterance();
    acc.ingestInterim("I think it's beautiful");
    acc.closeForMute();
    expect(sent).toContain("final");
    expect(sent).toContain("ended");
    expect(sent).not.toContain("failed");
    expect(texts.at(-1)).toBe("I think it's beautiful");
  });

  it("ends empty utterance on mute without failed", () => {
    const { acc, sent } = create();
    acc.startUtterance();
    acc.closeForMute();
    expect(sent).toEqual(["started", "ended"]);
  });
});
