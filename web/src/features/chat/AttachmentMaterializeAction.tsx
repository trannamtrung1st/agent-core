import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Flex, Grid, Spin, Tooltip, Typography, theme } from 'antd';
import { CopyOutlined } from '@ant-design/icons';
import { confirmAction } from '../../app/confirmAction';
import { copyAttachment, readAttachmentCopyAccess, type AttachmentCopyAccess, type AttachmentCopyResult } from '../../services/attachmentMaterialization';
import { IdentityCompletionError, IdentityError } from '../../services/identity';
import { listArtifactPage } from '../../services/artifacts';
import { listTenantWorkspace } from '../../services/tenantWorkspace';
import { TenantSessionFilesDrawer } from './TenantSessionFilesDrawer';

/** Exact original source action; reads and callbacks retire with the mounted tenant/Session/source. */
export function AttachmentMaterializeAction({ tenantId, sessionId, attachmentId, displayName }: { tenantId: string; sessionId: string; attachmentId: string; displayName: string }) {
  const [open, setOpen] = useState(false); const [access, setAccess] = useState<AttachmentCopyAccess>();
  const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false); const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string>(); const [result, setResult] = useState<AttachmentCopyResult>();
  const [mustReview, setMustReview] = useState(false); const [blocked, setBlocked] = useState(false); const [epoch, setEpoch] = useState(0);
  const opener = useRef<HTMLButtonElement>(null); const request = useRef<AbortController | null>(null); const mounted = useRef(true);
  const sending = useRef(false); const confirmation = useRef<ReturnType<ReturnType<typeof App.useApp>['modal']['confirm']> | null>(null);
  const { modal } = App.useApp(); const { token } = theme.useToken(); const screens = Grid.useBreakpoint();
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; request.current?.abort(); confirmation.current?.destroy(); }; }, []);
  async function reload(review = false) {
    request.current?.abort(); const controller = new AbortController(); request.current = controller; setLoading(true); setAccess(undefined);
    try {
      if (review) await Promise.all([
        listTenantWorkspace({ kind: 'working', tenantId, sessionId }, '/working', controller.signal),
        listArtifactPage(sessionId, undefined, undefined, tenantId, controller.signal),
      ]);
      const value = await readAttachmentCopyAccess(tenantId, sessionId, attachmentId, controller.signal); controller.signal.throwIfAborted();
      setAccess(value); if (!mustReview || review) setError(undefined); setBlocked(false); if (review) { setMustReview(false); setEpoch(n => n + 1); }
    } catch (failure) {
      if (!controller.signal.aborted) setError(review ? 'Current working files or Artifacts could not be checked. Ask someone with read access to confirm the copies before trying again.'
        : failure instanceof IdentityError && [401, 403, 404].includes(failure.status) ? 'This source file is no longer available to you.' : 'Current file access could not be loaded. Reload files to try again.');
    } finally { if (!controller.signal.aborted) setLoading(false); }
  }
  function start() { setOpen(true); void reload(); }
  function close() { if (sending.current || confirming) return; request.current?.abort(); setOpen(false); }
  function confirmCopy() {
    if (!access?.canMaterialize || busy || confirming || blocked || mustReview || loading) return;
    const saved = access; setConfirming(true);
    confirmation.current = confirmAction(modal, { title: 'Create working and Artifact copies?', okText: 'Create copies',
      content: <Flex vertical gap={token.paddingSM}><Typography.Text style={{ overflowWrap: 'anywhere' }}>{saved.displayName}</Typography.Text><Typography.Text>Create an editable working file and an immutable Artifact copy in this Session. The original stays unchanged.</Typography.Text></Flex>,
      onCancel: () => { setConfirming(false); }, afterClose: () => { if (mounted.current) setConfirming(false); confirmation.current = null; },
      onOk: async () => {
        if (sending.current) return; sending.current = true; setBusy(true); setResult(undefined); setError(undefined);
        request.current?.abort(); const controller = new AbortController(); request.current = controller;
        try {
          const value = await copyAttachment(tenantId, sessionId, attachmentId, saved.securityRevision, controller.signal);
          if (!controller.signal.aborted) { setResult(value); setBlocked(true); setEpoch(n => n + 1); }
        } catch (failure) {
          if (!controller.signal.aborted) {
            setBlocked(true);
            const partial = failure instanceof IdentityError && failure.reasonCode === 'attachment_materialization_incomplete';
            const uncertain = partial || failure instanceof IdentityCompletionError || !(failure instanceof IdentityError)
              || failure.status >= 500 || failure.reasonCode === 'attachment_materialization_unknown';
            if (!(failure instanceof IdentityError && failure.status === 409 && !uncertain)) setAccess(undefined);
            setMustReview(uncertain);
            setError(partial ? 'The working copy completed, but Artifact publication did not. Reload current files before deciding on another copy.'
              : uncertain ? 'The copy outcome could not be confirmed. Reload current working files and Artifacts before deciding on another copy.'
                : 'The copy was refused. Reload current file access before trying again.');
          }
        } finally { sending.current = false; if (mounted.current && !controller.signal.aborted) setBusy(false); }
      },
    });
  }
  return <>
    <Tooltip title="Copy to working files"><Button ref={opener} type="text" icon={<CopyOutlined />} aria-label={`Copy ${displayName} to working files`} onClick={start} /></Tooltip>
    <TenantSessionFilesDrawer tenant={tenantId} sessionId={sessionId} organization="Current organization" open={open} narrow={!screens.md} openerRef={opener} onClose={close} externalBusy={busy || confirming} refreshKey={epoch}>
      <Flex vertical gap={token.paddingSM}>
        <Typography.Text strong>Copy original to working files</Typography.Text>
        {loading ? <Spin aria-label="Loading copy access" /> : access && <Typography.Text style={{ overflowWrap: 'anywhere' }}>{access.displayName}</Typography.Text>}
        {access?.readOnly && <Alert type="info" showIcon title="This Session is read-only" />}
        {access && !access.canMaterialize && !access.readOnly && <Alert type="info" showIcon title="You do not have access to create these copies" />}
        {result && <Alert type="success" showIcon title="Copies created" description={<Typography.Text style={{ overflowWrap: 'anywhere' }}>{result.workspaceLogicalPath}. The Artifact is available below if you have read access.</Typography.Text>} />}
        {error && <Alert type="warning" showIcon title={error} />}
        <Flex gap={token.paddingXS} wrap>
          <Button type="primary" aria-label="Create copies" onClick={confirmCopy} loading={busy} disabled={!access?.canMaterialize || blocked || mustReview || loading || confirming}>Create copies</Button>
          <Button disabled={busy || confirming || loading} onClick={() => void reload(mustReview)}>Reload files</Button>
        </Flex>
        <Typography.Text type="secondary">Working files and Artifacts have separate read access. Creating copies does not grant access to read them.</Typography.Text>
      </Flex>
    </TenantSessionFilesDrawer>
  </>;
}
