import { useEffect, useRef, useState } from 'react';
import { IdentityCompletionError } from '../../services/identity';

/** One command at a time for an exact mounted owner. Retired scopes cannot show completion. */
export function useTenantCommand() {
  const controller = useRef(new AbortController());
  const pending = useRef(false);
  const acknowledgedFailure = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>();
  useEffect(() => { const current = new AbortController(); controller.current = current; return () => current.abort(); }, []);
  async function run<T>(action: (signal: AbortSignal) => Promise<T>, committed: (result: T) => void) {
    if (pending.current || acknowledgedFailure.current) return;
    const signal = controller.current.signal;
    pending.current = true; setBusy(true); setError(undefined);
    try {
      const result = await action(signal);
      if (!signal.aborted) {
        try { committed(result); }
        catch { acknowledgedFailure.current = true; setError(new IdentityCompletionError()); }
      }
    }
    catch (failure) { if (!signal.aborted) setError(failure); }
    finally { pending.current = false; if (!signal.aborted) setBusy(false); }
  }
  return { busy, error, run, clearError: () => { if (!acknowledgedFailure.current) setError(undefined); } };
}
