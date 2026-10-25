import { useRef, useState } from 'react';
import { IdentityCompletionError, IdentityError } from '../../services/identity';
import { useTenantCommand } from './useTenantCommand';

/** Space/Secret drafts keep user edits through conflicts; only an explicit read reopens a known failed save. */
export function useTenantEditorCommand() {
  const command = useTenantCommand(); const attempted = useRef(false); const uncertain = useRef(false);
  const [blocked, setBlocked] = useState(false); const [unknown, setUnknown] = useState(false);
  async function save<T>(action: (signal: AbortSignal) => Promise<T>, committed: (result: T) => void) {
    if (attempted.current || command.busy || command.requiresReload) return;
    attempted.current = true; setBlocked(true);
    await command.run(async signal => {
      try { return await action(signal); }
      catch (failure) {
        if (!signal.aborted && (!(failure instanceof IdentityError) || failure instanceof IdentityCompletionError || failure.status >= 500)) { uncertain.current = true; setUnknown(true); }
        throw failure;
      }
    }, committed);
  }
  async function refresh<T>(read: (signal: AbortSignal) => Promise<T>, observed: (result: T) => void) {
    if (command.busy || command.requiresReload) return;
    await command.run(read, result => { observed(result); if (!uncertain.current) { attempted.current = false; setBlocked(false); } });
  }
  return { ...command, save, refresh, blocked: blocked || command.requiresReload, unknown };
}
