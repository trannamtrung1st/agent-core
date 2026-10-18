import { useState, type RefObject } from 'react';
import { SessionSandboxFrame } from './SessionSandboxFrame';
import { TenantWorkspaceFiles } from '../files/TenantWorkspaceFiles';

export function TenantSessionFilesDrawer({ tenant, organization, sessionId, open, narrow, openerRef, onClose }: {
  tenant: string; organization: string; sessionId: string; open: boolean; narrow: boolean;
  openerRef: RefObject<HTMLButtonElement | null>; onClose: () => void;
}) {
  const [busy, setBusy] = useState(false);
  return <SessionSandboxFrame title="Session files" open={open} narrow={narrow} openerRef={openerRef} onClose={onClose} blocked={busy}>
    {open && <TenantWorkspaceFiles key={`${tenant}:${sessionId}`} owner={{ kind: 'working', tenantId: tenant, sessionId }} organization={organization} onBusy={setBusy} />}
  </SessionSandboxFrame>;
}
