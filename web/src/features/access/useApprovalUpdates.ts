import { useEffect, useRef, useState } from 'react';
import { identityRequest, IdentityError } from '../../services/identity';

// Durable requests are the source of truth. Notifications only prompt a fresh scoped read.
export function useApprovalUpdates<T>(path: string, cadence = 2000) {
  const [state, setState] = useState<{ path: string; data?: T; error?: unknown; loading: boolean }>({ path, loading: true });
  const refresh = useRef<() => void>(() => undefined);
  useEffect(() => {
    const abort = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined; let running = false; let again = false;
    setState({ path, loading: true });
    const load = async () => {
      clearTimeout(timer);
      if (abort.signal.aborted || document.hidden) return;
      if (running) { again = true; return; }
      running = true;
      try {
        const data = await identityRequest<T>(path, { signal: abort.signal });
        if (!abort.signal.aborted) setState({ path, data, loading: false });
      } catch (error) {
        if (!abort.signal.aborted) setState(previous => ({ path, loading: false, error,
          data: error instanceof IdentityError && [401, 403, 404].includes(error.status) ? undefined : previous.path === path ? previous.data : undefined }));
      } finally {
        running = false;
        if (!abort.signal.aborted && !document.hidden) { timer = setTimeout(() => void load(), again ? 0 : cadence); again = false; }
      }
    };
    const request = () => void load(); const visibility = () => { if (document.hidden) clearTimeout(timer); else request(); };
    const retire = () => { abort.abort(); clearTimeout(timer); setState({ path, loading: false }); };
    refresh.current = request; request();
    document.addEventListener('visibilitychange', visibility); window.addEventListener('agentcore:approval-updated', request); window.addEventListener('pagehide', retire);
    return () => { abort.abort(); clearTimeout(timer); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('agentcore:approval-updated', request); window.removeEventListener('pagehide', retire); };
  }, [path, cadence]);
  return { ...(state.path === path ? state : { loading: true, data: undefined, error: undefined }), reload: () => refresh.current() };
}
