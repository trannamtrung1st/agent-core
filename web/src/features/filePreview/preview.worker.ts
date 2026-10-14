self.onmessage = async (
  event: MessageEvent<{
    kind: 'csv' | 'code';
    source: string;
    language: string;
  }>,
) => {
  try {
    const { kind, source, language } = event.data;
    const value =
      kind === 'csv'
        ? (await import('./parseDelimited')).parseDelimited(source, language)
        : await (await import('./highlight')).highlight(source, language);
    self.postMessage({ value });
  } catch {
    self.postMessage({
      error:
        'This file could not be interpreted within the preview limits. Read Text or download the original.',
    });
  }
};
