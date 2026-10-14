import { beforeEach, describe, expect, it, vi } from 'vitest';
import { testPolicy } from './previewTestPolicy';
import { ownerFetch } from '../../services/api';
vi.mock('../../services/api', () => ({ ownerFetch: vi.fn() }));
beforeEach(() => { vi.resetModules(); vi.mocked(ownerFetch).mockReset(); });
describe('Effective host policy lifecycle', () => {
  it('deduplicates concurrent callers and caches one successful policy for the application load', async () => {
    vi.mocked(ownerFetch).mockResolvedValue(new Response(JSON.stringify(testPolicy)));
    const { loadPreviewOptions } = await import('./previewOptions');
    const [first, second] = await Promise.all([loadPreviewOptions(), loadPreviewOptions()]);
    expect(first).toEqual(testPolicy); expect(second).toBe(first);
    expect(await loadPreviewOptions()).toBe(first); expect(ownerFetch).toHaveBeenCalledTimes(1);
  });
  it('rejects invalid or unavailable policy and allows explicit retry without defaults', async () => {
    vi.mocked(ownerFetch).mockResolvedValueOnce(new Response('{}')).mockResolvedValueOnce(new Response('', {status:503})).mockResolvedValueOnce(new Response(JSON.stringify(testPolicy)));
    const { loadPreviewOptions } = await import('./previewOptions');
    await expect(loadPreviewOptions()).rejects.toMatchObject({code:'preview_policy_unavailable'});
    await expect(loadPreviewOptions()).rejects.toMatchObject({code:'preview_policy_unavailable'});
    expect(await loadPreviewOptions()).toEqual(testPolicy); expect(ownerFetch).toHaveBeenCalledTimes(3);
  });
});
