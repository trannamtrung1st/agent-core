import { useCallback, useEffect, useRef, useState } from "react";
import type { CursorPage } from "../../services/api";

/** Bounded, cursor-based reads shared by Background Sessions and AgentRun history. */
export function useCursorPages<T>(scope: string, open: boolean, load: (scope: string, cursor?: string) => Promise<CursorPage<T>>) {
  const [items, setItems] = useState<T[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const state = useRef({ generation: 0, pages: 1, busy: false, active: false, moreError: null as string | null });
  const loader = useRef(load); loader.current = load;
  const refresh = useCallback(async () => {
    const current = state.current;
    if (!current.active || current.busy) return;
    current.busy = true;
    const generation = ++current.generation;
    try {
      const rows: T[] = []; let next: string | undefined;
      const seen = new Set<string>();
      for (let index = 0; index < current.pages; index++) {
        const page = await loader.current(scope, next);
        if (generation !== current.generation || !current.active) return;
        rows.push(...page.items); next = page.hasMore ? page.nextCursor ?? undefined : undefined;
        if (page.hasMore && (!next || seen.has(next))) throw new Error("The page cursor is invalid. Refresh and try again.");
        if (next) seen.add(next);
        if (!page.hasMore || !next) break;
      }
      setItems(rows); setCursor(next ?? null); setError(current.moreError);
    } catch (reason) {
      if (generation === current.generation && current.active) setError(reason instanceof Error ? reason.message : "Unable to load items. Try again.");
    } finally {
      if (generation === current.generation) { current.busy = false; setLoading(false); }
    }
  }, [scope]);
  useEffect(() => {
    state.current = { generation: state.current.generation + 1, pages: 1, busy: false, active: open, moreError: null };
    setItems([]); setError(null); setCursor(null); setLoading(open); setLoadingMore(false);
    if (!open) return;
    void refresh();
    const timer = window.setInterval(() => void refresh(), 5_000);
    return () => { state.current.active = false; state.current.generation++; window.clearInterval(timer); };
  }, [open, scope, refresh]);
  async function loadMore() {
    const current = state.current;
    if (!cursor || !current.active || current.busy) return;
    current.busy = true; const generation = ++current.generation; setLoadingMore(true);
    try {
      const page = await loader.current(scope, cursor);
      if (generation !== current.generation || !current.active) return;
      if (page.hasMore && (!page.nextCursor || page.nextCursor === cursor)) throw new Error("The page cursor is invalid. Refresh and try again.");
      setItems(rows => [...rows, ...page.items]); setCursor(page.hasMore ? page.nextCursor : null); current.pages++; current.moreError = null; setError(null);
    } catch (reason) {
      if (generation === current.generation && current.active) {
        current.moreError = reason instanceof Error ? reason.message : "Unable to load more. Try again.";
        setError(current.moreError);
      }
    } finally { if (generation === current.generation) { current.busy = false; setLoadingMore(false); } }
  }
  function replace(update: (rows: T[]) => T[]) {
    state.current.generation++; state.current.busy = false; setLoading(false); setLoadingMore(false); setItems(update);
  }
  function retry() { return state.current.moreError && cursor ? loadMore() : refresh(); }
  return { items, loading, loadingMore, error, setError, hasMore: !!cursor, loadMore, refresh, retry, replace };
}
