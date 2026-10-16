import { act, renderHook } from '@testing-library/react';
import { IdentityCompletionError } from '../../services/identity';
import { useTenantCommand } from './useTenantCommand';
describe('tenant commands', () => {
  it('never replays a committed action when local completion fails', async () => {
    const { result } = renderHook(useTenantCommand); const action = vi.fn().mockResolvedValue({ id: 'saved' });
    await act(async () => { await result.current.run(action, () => { throw new Error('display failed'); }); });
    expect(result.current.error).toBeInstanceOf(IdentityCompletionError);
    act(() => result.current.clearError());
    expect(result.current.error).toBeInstanceOf(IdentityCompletionError);
    await act(async () => { await result.current.run(action, () => {}); });
    expect(action).toHaveBeenCalledTimes(1);
  });
  it('aborts an old owner and ignores a non-cooperative completion after unmount', async () => {
    const { result, unmount } = renderHook(useTenantCommand); let release!: (value: string) => void; let signal!: AbortSignal;
    const committed = vi.fn(); let pending!: Promise<void>;
    act(() => { pending = result.current.run(current => { signal = current; return new Promise(resolve => { release = resolve; }); }, committed); });
    unmount(); expect(signal.aborted).toBe(true); release('old private result'); await pending; expect(committed).not.toHaveBeenCalled();
  });
  it('retains retry after a genuine request failure and excludes duplicate submission', async () => {
    const { result } = renderHook(useTenantCommand); let release!: (value: string) => void; const committed = vi.fn();
    let pending!: Promise<void>; const action = vi.fn(() => new Promise<string>(resolve => { release = resolve; }));
    act(() => { pending = result.current.run(action, committed); });
    await act(async () => { await result.current.run(action, committed); }); expect(action).toHaveBeenCalledTimes(1);
    await act(async () => { release('saved'); await pending; }); expect(committed).toHaveBeenCalledWith('saved');
    await act(async () => { await result.current.run(async () => { throw new Error('offline'); }, committed); }); expect(result.current.error).toBeInstanceOf(Error);
    await act(async () => { await result.current.run(async () => 'retried', committed); }); expect(committed).toHaveBeenLastCalledWith('retried');
  });
});
