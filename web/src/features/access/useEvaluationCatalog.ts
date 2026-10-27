import { useEffect, useState } from 'react';
import { identityRequest, tenantApi } from '../../services/identity';
import type { EvaluationPage } from '../../services/tenantEvaluations';

/** Catalogs are capped at 200 items. Read every authorized page, never a silent first-page subset. */
export async function readEvaluationCatalog<T>(tenant: string, family: string, signal: AbortSignal): Promise<T[]> {
  const items: T[] = [];
  for (let page = 0; page < 3; page++) {
    const result = await identityRequest<EvaluationPage<T>>(tenantApi(tenant, `/${family}?page=${page}&pageSize=100`), { signal });
    items.push(...result.items);
    if ((page + 1) * result.pageSize >= result.total) return items;
  }
  throw new Error('The catalog changed while loading. Reload the current catalog.');
}
export function useEvaluationCatalog<T>(tenant: string, family: string) {
  const key = `${tenant}:${family}`; const [revision, reload] = useState(0);
  const [state, setState] = useState<{ key: string; data?: { items: T[] }; loading: boolean; error?: Error }>({ key, loading: true });
  useEffect(() => {
    const controller = new AbortController(); setState({ key, loading: true });
    void readEvaluationCatalog<T>(tenant, family, controller.signal)
      .then(items => { if (!controller.signal.aborted) setState({ key, data: { items }, loading: false }); })
      .catch(error => { if (!controller.signal.aborted) setState({ key, error, loading: false }); });
    return () => controller.abort();
  }, [key, revision]);
  return { ...(state.key === key ? state : { loading: true, data: undefined, error: undefined }), reload: () => reload(v => v + 1) };
}
