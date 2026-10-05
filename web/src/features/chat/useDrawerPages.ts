import { useCallback, useEffect, useRef, useState } from "react";
import type { DrawerPageQuery } from "../../services/api";

export const DRAWER_PAGE_SIZE = 20;

/** Paging lifecycle shared by the owner-scoped operational drawers. */
export function useDrawerPages<T>({ scope, open, refreshKey, pollIntervalMs, load, id }: {
  scope: string;
  open: boolean;
  refreshKey?: number;
  pollIntervalMs?: number;
  load: (scope: string, query?: DrawerPageQuery) => Promise<T[]>;
  id: (item: T) => string;
}) {
  const [items, setItems] = useState<T[]>([]);
  const [loading, setLoading] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [hasMore, setHasMore] = useState(false);
  const [refreshError, setRefreshError] = useState<string | null>(null);
  const [moreError, setMoreError] = useState<string | null>(null);
  const [actionError, setError] = useState<string | null>(null);
  const error = actionError ?? moreError ?? refreshError;
  const rows = useRef<T[]>([]);
  const pages = useRef(1);
  const generation = useRef(0);
  const morePending = useRef(false);
  const refreshPending = useRef(false);
  const active = useRef(false);
  const scopeEpoch = useRef(0);
  const attachedScope = useRef(scope);
  const queuedRefresh = useRef(false);
  const loader = useRef(load);
  loader.current = load;
  const lastRefreshKey = useRef(refreshKey);
  const getId = useRef(id);
  getId.current = id;

  const refresh = useCallback(async (initial = false) => {
    if (!active.current) return;
    if (morePending.current || refreshPending.current) {
      queuedRefresh.current = true;
      return;
    }
    queuedRefresh.current = false;
    const request = ++generation.current;
    refreshPending.current = true;
    if (initial) setLoading(true);
    setRefreshError(null);
    try {
      const next: T[] = [];
      let more = false;
      let before: string | undefined;
      for (let index = 0; index < pages.current; index++) {
        const batch = await loader.current(scope, { limit: DRAWER_PAGE_SIZE + 1, before });
        if (!active.current || request !== generation.current) return;
        more = batch.length > DRAWER_PAGE_SIZE;
        next.push(...batch.slice(0, DRAWER_PAGE_SIZE));
        if (!more) break;
        before = getId.current(next[next.length - 1]);
      }
      rows.current = next;
      setItems(next);
      setHasMore(more);
    } catch (reason) {
      if (active.current && request === generation.current) {
        setRefreshError(reason instanceof Error ? reason.message : "Unable to load items.");
      }
    } finally {
      if (active.current && request === generation.current) {
        refreshPending.current = false;
        setLoading(false);
        if (queuedRefresh.current) void refresh();
      }
    }
  }, [scope]);

  useEffect(() => {
    active.current = open;
    attachedScope.current = scope;
    scopeEpoch.current++;
    generation.current++;
    pages.current = 1;
    rows.current = [];
    morePending.current = false;
    refreshPending.current = false;
    setItems([]);
    setHasMore(false);
    setLoadingMore(false);
    setLoading(false);
    setError(null);
    setMoreError(null);
    setRefreshError(null);
    queuedRefresh.current = false;
    if (!open) return;
    void refresh(true);
    const timer = pollIntervalMs ? window.setInterval(() => void refresh(), pollIntervalMs) : undefined;
    return () => {
      active.current = false;
      scopeEpoch.current++;
      generation.current++;
      window.clearInterval(timer);
    };
  }, [open, scope, pollIntervalMs, refresh]);

  useEffect(() => {
    if (lastRefreshKey.current !== refreshKey && open) void refresh();
    lastRefreshKey.current = refreshKey;
  }, [refreshKey, open, refresh]);

  const loadMore = useCallback(async () => {
    if (!active.current || !hasMore || morePending.current || refreshPending.current) return;
    morePending.current = true;
    setMoreError(null);
    setLoadingMore(true);
    setError(null);
    const request = ++generation.current;
    try {
      const before = getId.current(rows.current[rows.current.length - 1]);
      const batch = await loader.current(scope, { limit: DRAWER_PAGE_SIZE + 1, before });
      if (!active.current || request !== generation.current) return;
      const existing = new Set(rows.current.map(getId.current));
      const next = [...rows.current, ...batch.slice(0, DRAWER_PAGE_SIZE).filter(row => !existing.has(getId.current(row)))];
      rows.current = next;
      pages.current++;
      setItems(next);
      setHasMore(batch.length > DRAWER_PAGE_SIZE);
    } catch (reason) {
      if (active.current && request === generation.current) {
        setMoreError(reason instanceof Error ? reason.message : "Unable to load more items.");
      }
    } finally {
      if (active.current && request === generation.current) {
        morePending.current = false;
        setLoadingMore(false);
        if (queuedRefresh.current) void refresh();
      }
    }
  }, [hasMore, scope, refresh]);

  function updateItems(update: (current: T[]) => T[]) {
    rows.current = update(rows.current);
    setItems(rows.current);
    generation.current++;
    refreshPending.current = false;
    morePending.current = false;
    setLoading(false);
    setLoadingMore(false);
  }

  function retry() {
    setError(null);
    if (moreError) void loadMore();
    else void refresh(items.length === 0);
  }

  function captureScope() {
    const epoch = scopeEpoch.current;
    return () => active.current && attachedScope.current === scope && scopeEpoch.current === epoch;
  }

  const sameScope = attachedScope.current === scope;
  return {
    items: sameScope ? items : [], updateItems,
    loading: loading || open && !sameScope, loadingMore: sameScope && loadingMore,
    hasMore: sameScope && hasMore, error: sameScope ? error : null,
    setError, loadMore, refresh, retry, captureScope
  };
}
