import type { PreviewOptions } from './previewOptions';
self.onmessage = async (
  event: MessageEvent<{
    kind: 'csv' | 'code';
    source: string;
    language: string;
    policy: PreviewOptions;
  }>,
) => {
  try {
    const { kind, source, language, policy } = event.data;
    const value =
      kind === 'csv'
        ? (await import('./parseDelimited')).parseDelimited(source, language, policy.spreadsheet)
        : await (await import('./highlight')).highlight(source, language);
    self.postMessage({ value });
  } catch (reason) {
    self.postMessage({
      error: reason instanceof Error ? reason.message :
        'This file could not be interpreted within the preview limits. Read Text or download the original.',
    });
  }
};
