import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { testPolicy } from './previewTestPolicy';
import { loadPreviewOptions } from './previewOptions';
const fetchMock = vi.fn();
beforeEach(() => { fetchMock.mockReset(); vi.stubGlobal('fetch', fetchMock); });
afterEach(() => { vi.unstubAllGlobals(); });
describe('current tenant preview policy', () => {
  it('reads current identity for every caller and never shares policy across organization or account changes', async () => {
    fetchMock.mockImplementation(async () => new Response(JSON.stringify(testPolicy)));
    const [first, second] = await Promise.all([loadPreviewOptions('alpha'), loadPreviewOptions('alpha')]);
    expect(first).toEqual(testPolicy); expect(second).toEqual(testPolicy); expect(second).not.toBe(first);
    await loadPreviewOptions('beta'); await loadPreviewOptions('alpha');
    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual(['alpha', 'alpha', 'beta', 'alpha'].map(id => `/api/v2/tenants/${id}/file-preview/options`));
    for (const [, options] of fetchMock.mock.calls) expect(options).toMatchObject({ credentials: 'same-origin', cache: 'no-store' });
  });
  it('rejects invalid or unavailable policy and permits explicit retry without defaults', async () => {
    fetchMock.mockResolvedValueOnce(new Response('{}')).mockResolvedValueOnce(new Response('', {status:503})).mockResolvedValueOnce(new Response(JSON.stringify(testPolicy)));
    await expect(loadPreviewOptions('alpha')).rejects.toMatchObject({code:'preview_policy_unavailable'});
    await expect(loadPreviewOptions('alpha')).rejects.toMatchObject({code:'preview_policy_unavailable'});
    expect(await loadPreviewOptions('alpha')).toEqual(testPolicy); expect(fetchMock).toHaveBeenCalledTimes(3);
  });
  it('aborts a retired preview owner without poisoning the next authorized request', async () => {
    fetchMock.mockImplementationOnce((_path, options) => new Promise((_resolve, reject) => options.signal.addEventListener('abort', () => reject(options.signal.reason), {once:true}))).mockResolvedValueOnce(new Response(JSON.stringify(testPolicy)));
    const owner = new AbortController(); const pending = loadPreviewOptions('alpha', owner.signal); const rejected = expect(pending).rejects.toMatchObject({name:'AbortError'});
    owner.abort(); await rejected;
    expect(await loadPreviewOptions('beta')).toEqual(testPolicy); expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
