import { beforeEach, expect, it, vi } from 'vitest';
import { tenantComposerChoices } from '../../services/tenantChat';
import { loadComposerChoices, resourceCategories } from './composerChoices';
vi.mock('../../services/tenantChat', () => ({ tenantComposerChoices: vi.fn() }));
const load = vi.mocked(tenantComposerChoices);
beforeEach(() => { load.mockReset(); });
it('combines all six tenant sources and preserves independent native cursors', async () => {
  load.mockImplementation(async (_id, params) => { const category = params.get('category')!; return { items: [{ id: category, category, label: category, description: '' }], nextCursor: `next-${category}` }; });
  const page = await loadComposerChoices('actual-instance', 'reference', 'review');
  expect(page.items.map(item => item.category)).toEqual(resourceCategories);
  expect(page.next).toEqual(Object.fromEntries(resourceCategories.map(category => [category, `next-${category}`])));
  expect(page.failed).toEqual({}); expect(load.mock.calls.every(([id]) => id === 'actual-instance')).toBe(true);
});
it('keeps authorized results and retries only failed sources at their prior position', async () => {
  load.mockImplementation(async (_id, params) => { if (params.get('category') === 'artifact') throw new Error('unavailable'); return { items: [{ id: 'chat', category: 'session', label: 'Chat', description: '' }], nextCursor: null }; });
  const page = await loadComposerChoices('instance', 'reference', '', { artifact: 'page2', session: 'page3' });
  expect(page.items).toHaveLength(1); expect(page.failed).toEqual({ artifact: 'page2' }); load.mockClear();
  load.mockResolvedValue({ items: [], nextCursor: 'page4' });
  const retry = await loadComposerChoices('instance', 'reference', '', page.failed);
  expect(load).toHaveBeenCalledTimes(1); expect(load.mock.calls[0][1].toString()).toContain('category=artifact&search=&cursor=page2'); expect(retry.next).toEqual({ artifact: 'page4' });
});
it('rejects a nonadvancing source cursor and retains retry', async () => {
  load.mockResolvedValue({ items: [], nextCursor: 'same' });
  const page = await loadComposerChoices('instance', 'invocation', '', { invocation: 'same' });
  expect(page.next).toEqual({}); expect(page.failed).toEqual({ invocation: 'same' });
});
it('discovers explicit Skill invocation independently from reference inspection', async () => {
  load.mockResolvedValue({ items: [], nextCursor: null }); await loadComposerChoices('instance', 'invocation', 'review');
  expect(load).toHaveBeenCalledTimes(1); expect(load.mock.calls[0][1].get('category')).toBe('invocation');
});
