import { beforeEach, describe, expect, it } from "vitest";
import { ClientTranscriptAccumulator } from "./clientTranscriptAccumulator";
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
});
