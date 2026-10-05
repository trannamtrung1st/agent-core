import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useDrawerPages } from "./useDrawerPages";

const first = Array.from({ length: 21 }, (_, index) => ({ id: `item-${index}` }));
const tail = [first[20]];
const id = (item: { id: string }) => item.id;
const options = { scope: "owner-1", open: true, id };
async function flush() { await act(async () => { await Promise.resolve(); }); }

afterEach(() => vi.useRealTimers());

describe("operational drawer paging lifecycle", () => {
  it("keeps loaded pages when a parent supplies new callback identities", async () => {
    const load = vi.fn().mockResolvedValueOnce(first).mockResolvedValueOnce(tail);
    const view = renderHook(({ adapter }) => useDrawerPages({ ...options, load: adapter }), { initialProps: { adapter: load } });
    await flush();
    await act(() => view.result.current.loadMore());
    expect(view.result.current.items).toHaveLength(21);
    view.rerender({ adapter: vi.fn().mockResolvedValue(first) });
    await flush();
    expect(view.result.current.items).toHaveLength(21);
  });

  it("keeps a next-page failure through polling and retries that page", async () => {
    vi.useFakeTimers();
    const load = vi.fn().mockResolvedValueOnce(first).mockRejectedValueOnce(new Error("Older page unavailable"))
      .mockResolvedValueOnce(first).mockResolvedValueOnce(tail);
    const view = renderHook(() => useDrawerPages({ ...options, load, pollIntervalMs: 1000 }));
    await flush();
    await act(() => view.result.current.loadMore());
    expect(view.result.current.error).toBe("Older page unavailable");
    await act(() => vi.advanceTimersByTimeAsync(1000));
    expect(view.result.current.error).toBe("Older page unavailable");
    await act(async () => { view.result.current.retry(); });
    expect(load).toHaveBeenLastCalledWith("owner-1", { limit: 21, before: "item-19" });
    expect(view.result.current.items).toHaveLength(21);
    expect(view.result.current.error).toBeNull();
  });

  it("applies a refresh requested while the next page is pending", async () => {
    let release: (items: typeof tail) => void = () => undefined;
    const pending = new Promise<typeof tail>(resolve => { release = resolve; });
    const refreshed = [{ id: "new-item" }, ...first];
    const load = vi.fn().mockResolvedValueOnce(first).mockReturnValueOnce(pending)
      .mockResolvedValueOnce(refreshed.slice(0, 21)).mockResolvedValueOnce(first.slice(19));
    const view = renderHook(({ refreshKey }) => useDrawerPages({ ...options, load, refreshKey }), { initialProps: { refreshKey: 0 } });
    await flush();
    act(() => { void view.result.current.loadMore(); });
    view.rerender({ refreshKey: 1 });
    await act(async () => { release(tail); });
    expect(view.result.current.items[0].id).toBe("new-item");
    expect(load).toHaveBeenCalledTimes(4);
  });

  it("does not supersede a slow initial list on every poll", async () => {
    vi.useFakeTimers();
    let release: (items: typeof first) => void = () => undefined;
    const pending = new Promise<typeof first>(resolve => { release = resolve; });
    const load = vi.fn().mockReturnValueOnce(pending).mockReturnValue(pending);
    const view = renderHook(() => useDrawerPages({ ...options, load, pollIntervalMs: 1000 }));
    await act(() => vi.advanceTimersByTimeAsync(3000));
    expect(load).toHaveBeenCalledTimes(1);
    await act(async () => { release(first); });
    expect(view.result.current.items).toHaveLength(20);
    expect(view.result.current.loading).toBe(false);
    expect(load).toHaveBeenCalledTimes(2);
  });

  it("drops an old owner's pending page after switching scope", async () => {
    let release: (items: typeof tail) => void = () => undefined;
    const pending = new Promise<typeof tail>(resolve => { release = resolve; });
    const load = vi.fn().mockResolvedValueOnce(first).mockReturnValueOnce(pending).mockResolvedValueOnce([{ id: "other-owner-item" }]);
    const view = renderHook(({ scope }) => useDrawerPages({ ...options, scope, load }), { initialProps: { scope: "owner-1" } });
    await flush();
    act(() => { void view.result.current.loadMore(); });
    view.rerender({ scope: "owner-2" });
    await act(async () => { release(tail); });
    expect(view.result.current.items).toEqual([{ id: "other-owner-item" }]);
    expect(view.result.current.loadingMore).toBe(false);
  });
});
