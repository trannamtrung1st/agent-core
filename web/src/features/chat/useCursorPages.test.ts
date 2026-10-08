import { act, renderHook } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useCursorPages } from "./useCursorPages";

const initial = { items: ["new"], nextCursor: "older-cursor", hasMore: true };
const older = { items: ["old"], nextCursor: null, hasMore: false };
afterEach(() => vi.useRealTimers());
const flush = () => act(async () => { await Promise.resolve(); });

describe("cursor page lifecycle", () => {
  it("keeps an older-page failure visible through polling and retries the failed cursor", async () => {
    vi.useFakeTimers();
    const load = vi.fn().mockResolvedValueOnce(initial).mockRejectedValueOnce(new Error("Older runs unavailable"))
      .mockResolvedValueOnce(initial).mockResolvedValueOnce(older);
    const view = renderHook(() => useCursorPages("owner", true, load)); await flush();
    await act(() => view.result.current.loadMore());
    await act(() => vi.advanceTimersByTimeAsync(5000));
    expect(view.result.current.error).toBe("Older runs unavailable");
    await act(() => view.result.current.retry());
    expect(load).toHaveBeenLastCalledWith("owner", "older-cursor");
    expect(view.result.current.items).toEqual(["new", "old"]);
    expect(view.result.current.error).toBeNull();
  });
  it("drops pending reads when changing owner", async () => {
    let finish!: (value: typeof initial) => void;
    const load = vi.fn().mockReturnValueOnce(new Promise(resolve => { finish = resolve; })).mockResolvedValueOnce(older);
    const view = renderHook(({ owner }) => useCursorPages(owner, true, load), { initialProps: { owner: "old-owner" } });
    view.rerender({ owner: "new-owner" }); await flush();
    await act(() => finish(initial));
    expect(view.result.current.items).toEqual(["old"]);
  });
  it("does not overwrite a committed mutation with a slower read", async () => {
    let finish!: (value: typeof initial) => void;
    const load = vi.fn().mockReturnValue(new Promise(resolve => { finish = resolve; }));
    const view = renderHook(() => useCursorPages("owner", true, load));
    act(() => view.result.current.replace(() => ["committed"]));
    await act(() => finish(initial));
    expect(view.result.current.items).toEqual(["committed"]);
  });
  it("rejects a repeating cursor without duplicating rows", async () => {
    const load = vi.fn().mockResolvedValueOnce(initial).mockResolvedValueOnce({ ...older, nextCursor: "older-cursor", hasMore: true });
    const view = renderHook(() => useCursorPages("owner", true, load)); await flush();
    await act(() => view.result.current.loadMore());
    expect(view.result.current.items).toEqual(["new"]);
    expect(view.result.current.error).toContain("cursor is invalid");
  });
});
