import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { resetIdentityCsrf } from '../../services/identity';
import { initialWorkflow, type WorkflowDefinition } from '../../services/tenantWorkflows';
import { workflowDraftFields } from './WorkflowDraftComparison';
import { useWorkflowCatalogCommand } from './useWorkflowCatalogCommand';

const fetchMock = vi.fn<typeof fetch>();
const definition = { tenantId: 'tenant', workflowId: 'workflow', revision: 2 } as WorkflowDefinition;
const intent = { workflow: 'workflow', key: 'original-key', method: 'PUT' as const, suffix: '/workflow/draft', body: { name: 'Exact original draft' }, expectedRevision: 1 };
const reply = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
beforeEach(() => { resetIdentityCsrf(); vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
it('recovers a lost save reply by its exact read-only receipt without repeating the write', async () => {
  const saved = vi.fn(); let committed = false;
  fetchMock.mockImplementation(async (path, options) => {
    if (String(path).endsWith('/csrf')) return reply({ token: 'csrf' });
    if (options?.method === 'PUT') { committed = true; throw new TypeError('Reply lost after commit'); }
    expect(String(path)).toContain('/requests/original-key');
    return reply(committed ? { definition, requestKey: 'original-key', securityRevision: 9 } : null);
  });
  const { result } = renderHook(() => useWorkflowCatalogCommand('tenant', saved));
  await act(async () => result.current.submit(intent)); expect(result.current.pending).toBe(true); expect(saved).not.toHaveBeenCalled();
  await act(async () => result.current.submit({ ...intent, key: 'new-key' }));
  await act(async () => result.current.recover()); expect(result.current.pending).toBe(false); expect(saved).toHaveBeenCalledOnce();
  expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'PUT')).toHaveLength(1);
});
it('keeps a mismatched receipt pending and requires explicit comparison after a stale draft', async () => {
  let stale = false; const saved = vi.fn(); fetchMock.mockImplementation(async path => String(path).endsWith('/csrf') ? reply({ token: 'csrf' }) : stale ? reply({ reasonCode: 'stale_revision' }, 409) : reply({ definition: { ...definition, workflowId: 'other' }, requestKey: 'original-key' }));
  const { result } = renderHook(() => useWorkflowCatalogCommand('tenant', saved));
  await act(async () => result.current.submit(intent)); expect(result.current.pending).toBe(true); expect(saved).not.toHaveBeenCalled();
  cleanup(); stale = true;
  const next = renderHook(() => useWorkflowCatalogCommand('tenant', saved));
  await act(async () => next.result.current.submit(intent)); expect(next.result.current.stale).toBe(true); expect(next.result.current.pending).toBe(false);
  const count = fetchMock.mock.calls.length; await act(async () => next.result.current.submit(intent)); expect(fetchMock.mock.calls).toHaveLength(count);
  act(() => next.result.current.reviewedLatest()); expect(next.result.current.stale).toBe(false);
});
it('includes changed objectives, proof bindings, destinations and process settings in comparison', () => {
  const graph = initialWorkflow('human'); const before = workflowDraftFields('Name', '', graph);
  const after = workflowDraftFields('Name', '', { ...graph, allowedOrigins: [0, 2], nodes: [...graph.nodes, { id: 'task', name: 'Task', kind: 1, requirement: 1, task: { objective: 'New objective', targetInstanceId: 'worker', spaceId: 'space' } }] });
  expect(after.get('Step task · task · objective')).toBe('New objective'); expect(before.has('Step task · task · objective')).toBe(false); expect(after.get('Process · allowedOrigins 2')).toBe('2');
});
it('resumes an interrupted publication only after an empty receipt read and keeps its exact original command key', async () => {
  const saved = vi.fn(); let writes = 0;
  fetchMock.mockImplementation(async (path, options) => {
    const url = String(path); if (url.endsWith('/csrf')) return reply({ token: 'csrf' });
    if (url.endsWith('/access/me')) return reply({ revision: 99 });
    if (url.includes('/requests/')) return reply(null);
    writes++; if (writes === 1) throw new TypeError('Interrupted publication after engine compile');
    expect(JSON.parse(String(options?.body))).toEqual({ name: 'Exact original draft', expectedSecurityRevision: 99 });
    return reply({ definition, requestKey: 'original-key', securityRevision: 100 });
  });
  const { result } = renderHook(() => useWorkflowCatalogCommand('tenant', saved));
  await act(async () => result.current.submit(intent));
  await act(async () => result.current.resumeOriginal()); expect(writes).toBe(1); expect(result.current.canResume).toBe(false);
  await act(async () => result.current.recover()); expect(writes).toBe(1); expect(result.current.canResume).toBe(true);
  await act(async () => result.current.resumeOriginal()); expect(writes).toBe(2); expect(result.current.pending).toBe(false); expect(saved).toHaveBeenCalledOnce();
});
