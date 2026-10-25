import { act, renderHook } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { IdentityError } from '../../services/identity';
import { useTenantEditorCommand } from './useTenantEditorCommand';

it('keeps a conflicted mutation blocked until an explicit successful read and never repeats it during refresh', async () => {
  const action = vi.fn().mockRejectedValueOnce(new IdentityError(409, 'stale_revision')).mockResolvedValue('saved'); const read = vi.fn().mockResolvedValue({ revision: 2 }); const saved = vi.fn(); const observed = vi.fn();
  const hook = renderHook(() => useTenantEditorCommand());
  await act(async () => { await hook.result.current.save(action, saved); });
  expect(hook.result.current.blocked).toBe(true);
  await act(async () => { await hook.result.current.save(action, saved); });
  expect(action).toHaveBeenCalledTimes(1);
  await act(async () => { await hook.result.current.refresh(read, observed); });
  expect(action).toHaveBeenCalledTimes(1); expect(observed).toHaveBeenCalledWith({ revision: 2 }); expect(hook.result.current.blocked).toBe(false);
  await act(async () => { await hook.result.current.save(action, saved); });
  expect(action).toHaveBeenCalledTimes(2); expect(saved).toHaveBeenCalledWith('saved');
});
it.each([new TypeError('Connection lost after submission'), new IdentityError(500, 'request_failed')])('retains an uncertain outcome through refresh and refuses another mutation: %s', async failure => {
  const action = vi.fn().mockRejectedValue(failure); const saved = vi.fn(); const hook = renderHook(() => useTenantEditorCommand());
  await act(async () => { await hook.result.current.save(action, saved); });
  expect(hook.result.current.unknown).toBe(true);
  await act(async () => { await hook.result.current.refresh(async () => ({ revision: 2 }), () => {}); });
  await act(async () => { await hook.result.current.save(action, saved); });
  expect(action).toHaveBeenCalledTimes(1); expect(saved).not.toHaveBeenCalled(); expect(hook.result.current.blocked).toBe(true);
});
