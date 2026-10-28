import { useEffect, useRef, useState } from 'react';
import { IdentityCompletionError, IdentityError, identityRequest } from '../../services/identity';
import { freshTenantRevision } from '../../services/tenantEvaluations';
import { workflowApi, type WorkflowReceipt } from '../../services/tenantWorkflows';

type Intent = { workflow: string; key: string; method: 'POST' | 'PUT'; suffix: string; body: Record<string, unknown>; expectedRevision: number };
/** Read back a lost reply first. Only an explicit action can resume the exact idempotent catalog command. */
export function useWorkflowCatalogCommand(tenant: string, committed: (receipt: WorkflowReceipt) => void) {
  const controller = useRef(new AbortController()); const active = useRef(false); const intent = useRef<Intent | null>(null);
  const [busy, setBusy] = useState(false); const [pending, setPending] = useState(false); const [error, setError] = useState<unknown>(); const [stale, setStale] = useState(false); const [canResume, setCanResume] = useState(false);
  useEffect(() => { const owner = new AbortController(); controller.current = owner; return () => owner.abort(); }, [tenant]);
  function accept(receipt: WorkflowReceipt, current: Intent) {
    if (receipt.definition.tenantId !== tenant || receipt.definition.workflowId !== current.workflow || receipt.requestKey !== current.key
      || receipt.definition.revision !== current.expectedRevision + 1) throw new IdentityCompletionError();
    try { committed(receipt); } catch { throw new IdentityCompletionError(); }
    intent.current = null; setPending(false); setCanResume(false);
  }
  async function transmit(current: Intent, signal: AbortSignal) {
    setCanResume(false);
    try {
      const receipt = await identityRequest<WorkflowReceipt>(workflowApi(tenant, current.suffix), { method: current.method, body: current.body, signal });
      if (!signal.aborted) accept(receipt, current);
    } catch (failure) {
      if (!signal.aborted) {
        setError(failure);
        if (failure instanceof IdentityError && failure.status < 500) { intent.current = null; setPending(false); if (failure.status === 409) setStale(true); }
      }
    }
  }
  async function submit(current: Intent) {
    if (active.current || intent.current || stale) return;
    const signal = controller.current.signal; active.current = true; setBusy(true); setError(undefined); intent.current = current; setPending(true);
    try { await transmit(current, signal); }
    finally { active.current = false; if (!signal.aborted) setBusy(false); }
  }
  async function recover() {
    const current = intent.current; if (!current || active.current) return;
    const signal = controller.current.signal; active.current = true; setBusy(true); setError(undefined); setCanResume(false);
    try {
      const receipt = await identityRequest<WorkflowReceipt | null>(workflowApi(tenant, `/requests/${encodeURIComponent(current.key)}`), { signal });
      if (!signal.aborted) { if (receipt) accept(receipt, current); else setCanResume(true); }
    } catch (failure) { if (!signal.aborted) setError(failure); }
    finally { active.current = false; if (!signal.aborted) setBusy(false); }
  }
  async function resumeOriginal() {
    const current = intent.current; if (!current || active.current || !canResume) return;
    const signal = controller.current.signal; active.current = true; setBusy(true); setError(undefined);
    try {
      // Security revision is a current authorization fence, outside the immutable command fingerprint.
      const expectedSecurityRevision = await freshTenantRevision(tenant, signal); signal.throwIfAborted();
      const resumed = { ...current, body: { ...current.body, expectedSecurityRevision } }; intent.current = resumed;
      await transmit(resumed, signal);
    } catch (failure) { if (!signal.aborted) setError(failure); }
    finally { active.current = false; if (!signal.aborted) setBusy(false); }
  }
  return { submit, recover, resumeOriginal, canResume, busy, pending, error, stale, reviewedLatest: () => { if (!intent.current) { setStale(false); setError(undefined); } } };
}
