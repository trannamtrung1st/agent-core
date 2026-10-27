import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { IdentityError, identityRequest } from '../../services/identity';
import { useEvaluationLaunch, type EvaluationLaunch } from './useEvaluationLaunch';
import { readEvaluationCatalog } from './useEvaluationCatalog';
vi.mock('../../services/identity', async original => ({ ...await original<typeof import('../../services/identity')>(), identityRequest: vi.fn() }));
const request: EvaluationLaunch = { subjectKind: 1, subjectId: 'run', subjectRevision: 5, suiteId: 'suite', suiteVersion: 1, hook: 3, dedupKey: 'one-exact-request' };
const attempt = { ...request, tenantId: 'tenant', attemptId: 'attempt' };
const api = vi.mocked(identityRequest);
beforeEach(() => { api.mockReset(); });
describe('evaluation submission recovery', () => {
  it('restores editing after a definite rejection and readback proves no reservation', async () => {
    api.mockImplementation(async (path, options) => { if (path.endsWith('/access/me')) return { revision: 1 }; if (options?.method === 'POST') throw new IdentityError(409, 'stale_revision'); return { attempt: null }; });
    const done = vi.fn(); const { result } = renderHook(() => useEvaluationLaunch('tenant', done));
    await act(() => result.current.submit(request));
    expect(result.current.pending).toBeUndefined(); expect(result.current.busy).toBe(false); expect(result.current.error).toBeInstanceOf(IdentityError); expect(done).not.toHaveBeenCalled();
  });
  it('recovers an uncertain submission using GET only, without a locked recovery action or another POST', async () => {
    let reads = 0;
    api.mockImplementation(async (path, options) => { if (path.endsWith('/access/me')) return { revision: 1 }; if (options?.method === 'POST') throw new Error('reply lost'); if (++reads === 1) throw new Error('offline'); return { attempt }; });
    const done = vi.fn(); const { result } = renderHook(() => useEvaluationLaunch('tenant', done));
    await act(() => result.current.submit(request));
    expect(result.current.pending?.dedupKey).toBe(request.dedupKey); expect(result.current.busy).toBe(false);
    await act(() => result.current.recover());
    await waitFor(() => expect(done).toHaveBeenCalledOnce()); expect(result.current.pending).toBeUndefined();
    expect(api.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
    expect(api.mock.calls.filter(([path]) => path.includes('/recovery?'))).toHaveLength(2);
  });
  it('does not replay a rejected mutation that already reserved a durable attempt', async () => {
    api.mockImplementation(async (path, options) => { if (path.endsWith('/access/me')) return { revision: 1 }; if (options?.method === 'POST') throw new IdentityError(409, 'evaluation_source_changed'); return { attempt }; });
    const done = vi.fn(); const { result } = renderHook(() => useEvaluationLaunch('tenant', done));
    await act(() => result.current.submit(request)); expect(done).toHaveBeenCalledWith(attempt);
    expect(api.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
  });
});
describe('complete authorized catalogs', () => {
  it('includes pickable entries after the first hundred', async () => {
    api.mockImplementation(async path => ({ items: path.includes('page=0') ? Array.from({ length: 100 }, (_, id) => ({ id })) : [{ id: 100 }], total: 101, pageSize: 100 }));
    const all = await readEvaluationCatalog<{ id: number }>('tenant', 'scripts', new AbortController().signal);
    expect(all).toHaveLength(101); expect(all[100].id).toBe(100);
  });
  it('rejects partial catalog data when a later page is denied', async () => {
    api.mockImplementation(async path => { if (path.includes('page=1')) throw new IdentityError(403, 'permission_denied'); return { items: [{ id: 1 }], total: 101, pageSize: 100 }; });
    await expect(readEvaluationCatalog('tenant', 'evaluation-suites', new AbortController().signal)).rejects.toBeInstanceOf(IdentityError);
  });
});
