import { act, renderHook, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
const api = vi.hoisted(() => ({ call: vi.fn() }));
vi.mock('../../services/identity', async importOriginal => ({ ...await importOriginal<typeof import('../../services/identity')>(), identityRequest: api.call }));
import { IdentityError } from '../../services/identity';
import { useApprovalUpdates } from './useApprovalUpdates';
beforeEach(() => { api.call.mockReset(); vi.spyOn(document, 'hidden', 'get').mockReturnValue(false); });
afterEach(() => vi.restoreAllMocks());
it('aborts and discards a former scope when its delayed response returns', async () => {
  let resolve!: (value: unknown) => void;
  api.call.mockImplementationOnce(() => new Promise(done => { resolve = done; })).mockResolvedValueOnce({ total: 2 });
  const hook = renderHook(({ path }) => useApprovalUpdates<{ total: number }>(path, 60000), { initialProps: { path: '/alpha' } });
  const signal = api.call.mock.calls[0][1].signal;
  hook.rerender({ path: '/beta' }); await waitFor(() => expect(hook.result.current.data?.total).toBe(2));
  await act(async () => resolve({ total: 99 }));
  expect(signal.aborted).toBe(true); expect(hook.result.current.data?.total).toBe(2); hook.unmount();
});
it('keeps known status after a service failure but clears it after authorization loss', async () => {
  api.call.mockResolvedValueOnce({ total: 1 }).mockRejectedValueOnce(new IdentityError(503, 'unavailable')).mockRejectedValueOnce(new IdentityError(403, 'permission_denied'));
  const hook = renderHook(() => useApprovalUpdates<{ total: number }>('/scope', 60000));
  await waitFor(() => expect(hook.result.current.data?.total).toBe(1));
  act(() => hook.result.current.reload()); await waitFor(() => expect(hook.result.current.error).toBeDefined());
  expect(hook.result.current.data?.total).toBe(1);
  act(() => hook.result.current.reload()); await waitFor(() => expect(hook.result.current.data).toBeUndefined()); hook.unmount();
});
it('pauses hidden reads and retires pending work on pagehide', async () => {
  const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(true); api.call.mockResolvedValue({ total: 1 });
  const hook = renderHook(() => useApprovalUpdates<{ total: number }>('/scope', 60000)); expect(api.call).not.toHaveBeenCalled();
  hidden.mockReturnValue(false); act(() => document.dispatchEvent(new Event('visibilitychange')));
  await waitFor(() => expect(hook.result.current.data?.total).toBe(1));
  const signal = api.call.mock.calls[0][1].signal;
  act(() => window.dispatchEvent(new Event('pagehide'))); expect(signal.aborted).toBe(true); expect(hook.result.current.data).toBeUndefined();
  act(() => window.dispatchEvent(new Event('agentcore:approval-updated'))); expect(api.call).toHaveBeenCalledTimes(1); hook.unmount();
});
