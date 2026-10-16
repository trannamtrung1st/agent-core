import { useEffect, useState } from 'react';
import { identityRequest } from '../../services/identity';
export function useIdentityQuery<T>(path: string | null, refreshKey = '') {
  const key = path === null ? null : `${path}::${refreshKey}`;
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<{ key: string | null; data?: T; error?: Error; loading: boolean }>({ key, loading: true });
  useEffect(() => {
    if (!path) return;
    const controller = new AbortController();
    setState({ key, loading: true });
    void identityRequest<T>(path, { signal: controller.signal })
      .then(data => { if (!controller.signal.aborted) setState({ key, data, loading: false }); })
      .catch((error: Error) => { if (!controller.signal.aborted) setState({ key, error, loading: false }); });
    return () => controller.abort();
  }, [path, attempt, refreshKey]);
  // A new scope never renders a previous scope's data before the effect runs.
  const current = state.key === key ? state : { loading: true, data: undefined, error: undefined };
  return { ...current, reload: () => setAttempt(value => value + 1) };
}
