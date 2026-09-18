export type SpeechObservation = {
  name: string;
  value?: number;
  reason?: string;
  code?: string;
};

const events: SpeechObservation[] = [];

export function resetSpeechObservations(): void {
  events.length = 0;
}

export function snapshotSpeechObservations(): SpeechObservation[] {
  return [...events];
}

export function recordSpeechObservation(event: SpeechObservation): void {
  events.push({
    name: event.name,
    value: event.value,
    reason: event.reason,
    code: event.code
  });
}
