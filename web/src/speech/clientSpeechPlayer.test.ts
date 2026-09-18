import { beforeEach, describe, expect, it } from "vitest";
import { FakeSpeechSynthesizer } from "./fakeSpeechAdapters";
import { createClientSpeechPlayer, type ClientSpeechPlaybackAck } from "./clientSpeechPlayer";
import { resetSpeechObservations, snapshotSpeechObservations } from "./speechObservability";

function collect() {
  const acks: { responseId: string; report: ClientSpeechPlaybackAck }[] = [];
  const synth = new FakeSpeechSynthesizer();
  const player = createClientSpeechPlayer(synth, (responseId, report) => acks.push({ responseId, report }));
  return { player, acks, synth };
}

describe("clientSpeechPlayer", () => {
  beforeEach(() => {
    resetSpeechObservations();
  });

  it("queues ordered segments and ACKs started 0, clamped progress, then completed after output.completed", async () => {
    const { player, acks } = collect();
    player.enqueue({ responseId: "r1", segmentIndex: 0, textStart: 0, text: "Hello" });
    player.enqueue({ responseId: "r1", segmentIndex: 1, textStart: 5, text: " there" });
    await Promise.resolve();
    player.markOutputCompleted("r1", 11);
    await Promise.resolve();
    expect(acks.map((item) => item.report.kind)).toEqual(["started", "progress", "progress", "completed"]);
    expect(acks[0]?.report).toEqual({ kind: "started", consumedSamples: 0, textEndExclusive: 0 });
    expect(acks[1]?.report.textEndExclusive).toBe(5);
    expect(acks[2]?.report.textEndExclusive).toBe(11);
    expect(acks[3]?.report).toEqual({ kind: "completed", consumedSamples: 0, textEndExclusive: 11 });
    expect(acks.every((item) => item.report.consumedSamples === 0)).toBe(true);
    expect(player.activeResponseId()).toBeNull();
    expect(snapshotSpeechObservations().some((item) => item.name === "speech.playback.complete")).toBe(true);
    expect(JSON.stringify(snapshotSpeechObservations())).not.toContain("Hello");
  });

  it("does not complete before speech.output.completed", async () => {
    const { player, acks } = collect();
    player.enqueue({ responseId: "r1", segmentIndex: 0, textStart: 0, text: "Hi" });
    await Promise.resolve();
    expect(acks.map((item) => item.report.kind)).toEqual(["started", "progress"]);
  });

  it("ignores stale response segments and callbacks", async () => {
    const { player, acks } = collect();
    player.enqueue({ responseId: "r1", segmentIndex: 0, textStart: 0, text: "One" });
    player.enqueue({ responseId: "r2", segmentIndex: 0, textStart: 0, text: "Two" });
    player.markOutputCompleted("r2", 3);
    await Promise.resolve();
    expect(acks.every((item) => item.responseId === "r1")).toBe(true);
  });

  it("cancels immediately on Stop without crediting the unheard tail", async () => {
    const acks: { responseId: string; report: ClientSpeechPlaybackAck }[] = [];
    const synth = new FakeSpeechSynthesizer();
    const gate = new Promise<void>(() => undefined);
    synth.hold = gate;
    const player = createClientSpeechPlayer(synth, (responseId, report) => acks.push({ responseId, report }));
    player.enqueue({ responseId: "r1", segmentIndex: 0, textStart: 0, text: "Hello" });
    player.enqueue({ responseId: "r1", segmentIndex: 1, textStart: 5, text: " unheard" });
    await player.cancel("r1");
    expect(synth.cancelled).toBe(true);
    expect(acks.at(-1)?.report).toEqual({ kind: "stopped", consumedSamples: 0, textEndExclusive: 0 });
    expect(snapshotSpeechObservations().some((item) => item.name === "speech.cancel.reason" && item.reason === "clientCancel")).toBe(true);
    expect(JSON.stringify(snapshotSpeechObservations())).not.toContain("unheard");
  });

  it("reports SpeechVoiceUnavailable instead of speaking a mismatched default voice", async () => {
    const acks: { responseId: string; report: ClientSpeechPlaybackAck }[] = [];
    const errors: string[] = [];
    const synth = new FakeSpeechSynthesizer();
    const player = createClientSpeechPlayer(
      synth,
      (responseId, report) => acks.push({ responseId, report }),
      (error) => errors.push(error.code)
    );
    player.enqueue({ responseId: "r1", segmentIndex: 0, textStart: 0, text: "Hola", language: "es-ES" });
    await Promise.resolve();
    expect(synth.spoken).toEqual([]);
    expect(errors).toEqual(["SpeechVoiceUnavailable"]);
    expect(acks.at(-1)?.report.kind).toBe("stopped");
  });

  it("passes language and speakingRate through to the synthesizer", async () => {
    const { player, synth } = collect();
    player.enqueue({
      responseId: "r1",
      segmentIndex: 0,
      textStart: 0,
      text: "Hello",
      language: "en",
      speakingRate: 1.2
    });
    await Promise.resolve();
    expect(synth.spoken[0]).toMatchObject({ text: "Hello", language: "en", speakingRate: 1.2 });
  });
});
