import { act, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import { HighlightedCode } from './HighlightedCode';
import { loadPreviewOptions, type PreviewOptions } from './previewOptions';
import { processPreview } from './previewWorker';
import { testPolicy } from './previewTestPolicy';
vi.mock('./previewOptions', () => ({ loadPreviewOptions: vi.fn() }));
vi.mock('./previewWorker', () => ({ processPreview: vi.fn().mockImplementation(async (_kind, source) => [[{ content: source, color: '#abcdef' }]]) }));
beforeEach(() => { vi.clearAllMocks(); });
it('retires a pending policy read when the tenant changes and ignores its late result', async () => {
  let release!: (value: PreviewOptions) => void;
  vi.mocked(loadPreviewOptions).mockReturnValueOnce(new Promise(resolve => { release = resolve; })).mockResolvedValueOnce(testPolicy);
  const view = render(<HighlightedCode tenantId="alpha" source="const scope = 'current';" language="ts" />);
  const first = vi.mocked(loadPreviewOptions).mock.calls[0][1]!;
  view.rerender(<HighlightedCode tenantId="beta" source="const scope = 'current';" language="ts" />);
  expect(first.aborted).toBe(true);
  await waitFor(() => expect(processPreview).toHaveBeenCalledTimes(1));
  await act(async () => { release({ ...testPolicy, text: { ...testPolicy.text, highlightMaxBytes: 1 } }); });
  expect(processPreview).toHaveBeenCalledTimes(1);
  expect(loadPreviewOptions).toHaveBeenLastCalledWith('beta', expect.any(AbortSignal));
  expect(screen.getByText("const scope = 'current';")).toBeVisible();
});
it('renders escaped plain code without requesting any global policy when no scope is supplied', () => {
  render(<HighlightedCode source="<script>private source</script>" />);
  expect(screen.getByText('<script>private source</script>')).toBeVisible();
  expect(document.querySelector('script')).toBeNull(); expect(loadPreviewOptions).not.toHaveBeenCalled(); expect(processPreview).not.toHaveBeenCalled();
});
