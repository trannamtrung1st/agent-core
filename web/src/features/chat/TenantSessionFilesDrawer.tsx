import { useState, type ReactNode, type RefObject } from 'react';
import { Tabs } from 'antd';
import { SessionArtifacts } from './SessionArtifacts';
import { SessionSandboxFrame } from './SessionSandboxFrame';
import { TenantWorkspaceFiles } from '../files/TenantWorkspaceFiles';

export function TenantSessionFilesDrawer({ tenant, organization, sessionId, open, narrow, openerRef, onClose, children, externalBusy = false, refreshKey = 0 }: {
  tenant: string; organization: string; sessionId: string; open: boolean; narrow: boolean;
  openerRef: RefObject<HTMLButtonElement | null>; onClose: () => void; children?: ReactNode; externalBusy?: boolean; refreshKey?: number;
}) {
  const [busy, setBusy] = useState(false);
  return <SessionSandboxFrame title="Session files" open={open} narrow={narrow} openerRef={openerRef} onClose={onClose} blocked={busy || externalBusy}>
    {children}
    {open && <Tabs key={`${tenant}:${sessionId}:${refreshKey}`} items={[
      { key: 'working', label: 'Working files', children: <TenantWorkspaceFiles key={`${tenant}:${sessionId}`} owner={{ kind: 'working', tenantId: tenant, sessionId }} organization={organization} onBusy={setBusy} /> },
      { key: 'artifacts', label: 'Artifacts', children: <SessionArtifacts sessionId={sessionId} tenantId={tenant} open /> },
    ]} />}
  </SessionSandboxFrame>;
}
