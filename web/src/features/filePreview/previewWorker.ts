import type { PreviewOptions } from './previewOptions';
export function processPreview<T>(
  kind: 'csv' | 'code',
  source: string,
  language: string,
  signal: AbortSignal,
  policy: PreviewOptions,
): Promise<T> {
  return new Promise((resolve, reject) => {
    const worker = new Worker(new URL('./preview.worker.ts', import.meta.url), {
      type: 'module',
    });
    const stop = () => {
      worker.terminate();
      clearTimeout(timer);
      signal.removeEventListener('abort', abort);
    };
    const abort = () => {
      stop();
      reject(new DOMException('Aborted', 'AbortError'));
    };
    const timer = setTimeout(() => {
      stop();
      reject(
        new Error(
          'Preview processing timed out. Read Text or download the original.',
        ),
      );
    }, policy.workerTimeoutMs);
    worker.onmessage = (event) => {
      stop();
      event.data.error
        ? reject(new Error(event.data.error))
        : resolve(event.data.value);
    };
    worker.onerror = () => {
      stop();
      reject(
        new Error(
          'Preview processing failed. Read Text or download the original.',
        ),
      );
    };
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) {
      abort();
      return;
    }
    worker.postMessage({ kind, source, language, policy });
  });
}
