import type { ClientSpeechSynthesizer } from "./clientSpeechSynthesizer";

export type ClientSpeechPlaybackAck = {
  kind: "started" | "progress" | "completed" | "stopped";
  consumedSamples: 0;
  textEndExclusive: number;
};

export type ClientSpeechSegment = {
  responseId: string;
  segmentIndex: number;
  textStart: number;
  text: string;
  voiceHint?: string;
};

export type ClientSpeechPlayer = {
  enqueue: (segment: ClientSpeechSegment) => void;
  markOutputCompleted: (responseId: string, textEndExclusive: number) => void;
  cancel: (responseId?: string) => Promise<void>;
  reset: () => Promise<void>;
  activeResponseId: () => string | null;
  trustedEnd: () => number;
};

export function createClientSpeechPlayer(
  synthesizer: ClientSpeechSynthesizer,
  ack: (responseId: string, report: ClientSpeechPlaybackAck) => void
): ClientSpeechPlayer {
  let responseId: string | null = null;
  let generation = 0;
  let trusted = 0;
  let outputCompleted = false;
  let outputEnd = 0;
  let started = false;
  let completedSent = false;
  let busy = false;
  const queue: ClientSpeechSegment[] = [];

  function ignore(id: string | null | undefined): boolean {
    return Boolean(id && responseId && id !== responseId);
  }

  function report(id: string, kind: ClientSpeechPlaybackAck["kind"], textEndExclusive: number): void {
    ack(id, { kind, consumedSamples: 0, textEndExclusive });
  }

  async function pump(): Promise<void> {
    if (busy) {
      return;
    }

    const current = responseId;
    const gen = generation;
    while (current && current === responseId && gen === generation) {
      const next = queue.shift();
      if (!next) {
        maybeComplete();
        return;
      }

      busy = true;
      await synthesizer.speak(
        { text: next.text, hint: next.voiceHint ? { name: next.voiceHint } : undefined },
        {
          onEnd: () => {
            if (gen !== generation || responseId !== current) {
              return;
            }

            trusted = Math.max(trusted, next.textStart + next.text.length);
            report(current, "progress", trusted);
          }
        }
      );
      busy = false;
      if (gen !== generation) {
        return;
      }
    }
  }

  function maybeComplete(): void {
    if (!responseId || completedSent || !outputCompleted || busy || queue.length > 0) {
      return;
    }

    completedSent = true;
    report(responseId, "completed", outputEnd);
    responseId = null;
    started = false;
    outputCompleted = false;
  }

  function begin(id: string): void {
    responseId = id;
    trusted = 0;
    outputCompleted = false;
    outputEnd = 0;
    started = false;
    completedSent = false;
    queue.length = 0;
  }

  const player: ClientSpeechPlayer = {
    enqueue(segment) {
      if (ignore(segment.responseId)) {
        return;
      }

      if (!responseId) {
        begin(segment.responseId);
      }

      if (!started) {
        started = true;
        report(segment.responseId, "started", 0);
      }

      queue.push(segment);
      void pump();
    },
    markOutputCompleted(id, textEndExclusive) {
      if (ignore(id) || !responseId) {
        if (!responseId) {
          begin(id);
        } else {
          return;
        }
      }

      outputCompleted = true;
      outputEnd = textEndExclusive;
      void pump();
    },
    async cancel(id) {
      if (ignore(id)) {
        return;
      }

      const active = responseId;
      generation += 1;
      queue.length = 0;
      busy = false;
      await synthesizer.cancel();
      if (active && started && !completedSent) {
        report(active, "stopped", trusted);
      }

      responseId = null;
      started = false;
      completedSent = false;
      outputCompleted = false;
    },
    async reset() {
      await player.cancel(responseId ?? undefined);
    },
    activeResponseId: () => responseId,
    trustedEnd: () => trusted
  };
  return player;
}
