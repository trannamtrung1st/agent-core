import { useEffect, useRef, useState } from 'react';
import { IdentityCompletionError, IdentityError, identityRequest, tenantApi } from '../../services/identity';
import { freshTenantRevision, type EvaluationAttempt } from '../../services/tenantEvaluations';
export type EvaluationLaunch = { subjectKind: number; subjectId: string; subjectRevision: number; suiteId: string; suiteVersion: number; hook: number; dedupKey: string };

/** An uncertain POST is followed only by read-only recovery. It is never resent. */
export function useEvaluationLaunch(tenant: string, completed: (attempt: EvaluationAttempt) => void) {
  const controller = useRef(new AbortController()); const inFlight = useRef(false);
  const [busy, setBusy] = useState(false); const [pending, setPending] = useState<EvaluationLaunch>(); const [error, setError] = useState<unknown>();
  useEffect(() => { const current = new AbortController(); controller.current = current; return () => current.abort(); }, [tenant]);
  const accept = (request: EvaluationLaunch, result: EvaluationAttempt) => {
    if (result.tenantId !== tenant || result.subjectKind !== request.subjectKind || result.subjectId !== request.subjectId || result.subjectRevision !== request.subjectRevision || result.suiteId !== request.suiteId || result.suiteVersion !== request.suiteVersion || result.hook !== request.hook || result.dedupKey !== request.dedupKey) throw new IdentityCompletionError(false);
    setPending(undefined); setError(undefined); completed(result);
  };
  const read = (request: EvaluationLaunch, signal: AbortSignal) => identityRequest<{ attempt: EvaluationAttempt | null }>(tenantApi(tenant, `/evaluation-attempts/recovery?dedupKey=${encodeURIComponent(request.dedupKey)}`), { signal });
  async function submit(request: EvaluationLaunch) {
    if (inFlight.current || pending) return;
    inFlight.current = true; setBusy(true); setError(undefined); const signal = controller.current.signal;
    let posted = false;
    try {
      const expectedRevision = await freshTenantRevision(tenant, signal);
      signal.throwIfAborted(); setPending(request); posted = true;
      const result = await identityRequest<EvaluationAttempt>(tenantApi(tenant, '/evaluation-attempts'), { method: 'POST', body: { ...request, expectedRevision }, signal });
      if (!signal.aborted) accept(request, result);
    } catch (failure) {
      if (!signal.aborted) {
        if (!posted) { setPending(undefined); setError(failure); }
        else {
          try {
            const saved = await read(request, signal);
            if (saved.attempt) accept(request, saved.attempt);
            else if (failure instanceof IdentityError && failure.status >= 400 && failure.status < 500) { setPending(undefined); setError(failure); }
            else setError(new Error('The submission outcome is uncertain. Refresh its exact saved status; this action will not run the evaluator again.'));
          } catch { if (!signal.aborted) setError(new Error('The submission outcome is uncertain. Restore current access and refresh the exact saved status.')); }
        }
      }
    } finally { inFlight.current = false; if (!signal.aborted) setBusy(false); }
  }
  async function recover() {
    if (!pending || inFlight.current) return;
    inFlight.current = true; setBusy(true); setError(undefined); const signal = controller.current.signal;
    try { const saved = await read(pending, signal); if (!signal.aborted) { if (saved.attempt) accept(pending, saved.attempt); else setError(new Error('No saved attempt is visible yet. Refresh exact status again; no evaluation has been resent.')); } }
    catch (failure) { if (!signal.aborted) setError(failure); }
    finally { inFlight.current = false; if (!signal.aborted) setBusy(false); }
  }
  return { busy, pending, error, submit, recover };
}
