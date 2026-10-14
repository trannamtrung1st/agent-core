import { SandboxEvidence } from "../sandbox/SandboxEvidence";
import { useEffect, useRef, useState, type RefObject } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Spin, Tag, Typography, theme } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { getSandboxExecution, getSessionSandbox, resetSessionSandbox, stopSandboxExecution, type SandboxEnvironment, type SandboxExecution } from '../../services/sandboxes';
import { DiagnosticDetails } from './DiagnosticDetails';

export function SessionSandboxDrawer({ sessionId, open, narrow, readOnly, openerRef, onClose }: { sessionId: string; open: boolean; narrow: boolean; readOnly?: boolean; openerRef?: RefObject<HTMLButtonElement | null>; onClose: () => void }) {
  const { token } = theme.useToken(); const { modal, message } = App.useApp();
  const [environment, setEnvironment] = useState<SandboxEnvironment | null>(null); const [execution, setExecution] = useState<SandboxExecution | null>(null);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const [reading, setReading] = useState(false);
  const epoch = useRef(0); const selected = useRef(sessionId); selected.current = sessionId;
  const wasOpen = useRef(false);
  useEffect(() => {
    const closing = wasOpen.current && !open;
    wasOpen.current = open;
    if (!closing || !openerRef) return;
    // A rapid open/close can cancel AntD's motion callback. Restore after
    // focus-lock cleanup even when that callback never fires; a reopen cancels it.
    const frame = requestAnimationFrame(() => { if (!wasOpen.current) openerRef.current?.focus({ preventScroll: true }); });
    return () => cancelAnimationFrame(frame);
  }, [open, openerRef]);
  const refresh = useRef<() => Promise<void>>(async () => {});
  const observed = useRef({ environment, execution, error, reading }); observed.current = { environment, execution, error, reading };
  useEffect(() => {
    const generation = ++epoch.current; setEnvironment(null); setExecution(null); setError(null); setLoading(true); setBusy(false); setReading(false);
    if (!open) return;
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
    let inFlight: Promise<void> | undefined;
    async function read() {
      setReading(true);
      try {
        const next = await getSessionSandbox(sessionId, controller.signal);
        const id = next?.activeExecutionId ?? next?.lastExecutionId;
        const result = id ? await getSandboxExecution(sessionId, id, controller.signal) : null;
        if (controller.signal.aborted || epoch.current !== generation) return;
        setEnvironment(next); setExecution(result); setError(null);
      } catch { if (!controller.signal.aborted && epoch.current === generation) setError('Sandbox status is unavailable. Checking again automatically; previous evidence is retained.'); }
      finally { if (!controller.signal.aborted && epoch.current === generation) { setLoading(false); setReading(false); } }
    }
    // Polling, Retry and post-mutation refresh share one read so an older failure
    // cannot overwrite a newer success or re-enable recovery from stale evidence.
    function load() {
      if (controller.signal.aborted || epoch.current !== generation) return Promise.resolve();
      return inFlight ??= read().finally(() => { inFlight = undefined; });
    }
    async function poll() { await load(); if (!controller.signal.aborted) timer = setTimeout(() => void poll(), 2000); }
    refresh.current = load; void poll();
    return () => { controller.abort(); if (timer) clearTimeout(timer); ++epoch.current; };
  }, [sessionId, open]);
  async function mutate(action: () => Promise<unknown>) {
    const id = sessionId; const generation = epoch.current; setBusy(true); setError(null);
    try { await action(); if (selected.current === id && epoch.current === generation) await refresh.current(); }
    catch { if (selected.current === id && epoch.current === generation) setError('Sandbox recovery was not confirmed. Refresh status before retrying.'); }
    finally { if (selected.current === id && epoch.current === generation) setBusy(false); }
  }
  function confirmMutation(title: string, content: string, action: () => Promise<unknown>, executionId?: string) {
    const generation = epoch.current; const sandboxGeneration = environment?.generation;
    confirmAction(modal, { title, content, danger: !executionId, okText: executionId ? 'Stop command' : 'Reset sandbox', onOk: async () => {
      if (epoch.current !== generation || selected.current !== sessionId || observed.current.error || observed.current.reading || observed.current.environment?.generation !== sandboxGeneration
          || executionId && observed.current.execution?.executionId !== executionId) { message.warning('Selection changed. Refresh before recovery.'); return; }
      await mutate(action);
    } });
  }
  const uncertain = environment?.state === 'NeedsResync' || environment?.state === 'NeedsReconciliation' || execution?.status === 'Indeterminate';
  const ended = environment?.state === 'Terminated' || environment?.state === 'Ready' && Date.parse(environment.expiresAt) <= Date.now();
  const initializationBlocked=environment?.initialization?.state==='Running'||environment?.initialization?.state==='Failed';
  const cleanupLabels:Record<string,string>={reset:'Explicit reset',sessionDeleted:'Session deleted',sessionArchived:'Session archived',sessionEnded:'Session ended',sessionDeactivated:'Session deactivated',sessionLifecycle:'Session lifecycle changed',authorityRevoked:'Instance or Profile authority revoked',maximumLifetime:'Maximum lifetime reached',idleExpired:'Idle timeout reached',initializationUncertain:'Initialization outcome uncertain',initializationFailed:'Initialization failed',setupFailed:'Environment setup failed'};
  const syncLabel: Record<string, string> = { committed: 'Files committed', notCommitted: 'Files not committed', committing: 'File commit pending recovery', unknown: 'File commit outcome unknown', pending: 'Files pending' };
  const recoveryDescription = execution?.workspaceSyncStatus === 'committed'
    ? 'Canonical files were committed, but command completion was not confirmed. Inspect retained results before retrying. Reset preserves these files and Artifacts.'
    : execution?.workspaceSyncStatus === 'unknown' || execution?.workspaceSyncStatus === 'committing'
      ? 'Files may already have been committed. Inspect canonical /working before retrying. Commands are never repeated automatically. Reset preserves canonical files and Artifacts.'
      : 'The command outcome or scratch synchronization is uncertain. Commands are not repeated automatically. Reset discards sandbox-only state and retains canonical Workspace files and Artifacts.';
  return <Drawer title="Session sandbox" open={open} onClose={onClose} size={narrow ? '100%' : 640} destroyOnHidden focusable={{ focusTriggerAfterClose: !openerRef }}
    afterOpenChange={visible => { if (!visible && openerRef?.current?.isConnected) openerRef.current.focus({ preventScroll: true }); }}>
    <Flex vertical gap={token.padding} onKeyDownCapture={event => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose(); }
    }}>
      {error && <Alert type="error" showIcon title={error} action={<Button loading={reading} onClick={() => void refresh.current()}>Retry</Button>} />}
      {loading ? <Spin aria-label="Loading Session sandbox" /> : !environment ? !error && <Empty description="No sandbox environment yet. An authorized command provisions the selected verified profile for this Session." /> : <>
        <Flex align="center" wrap gap={token.paddingXS}><Tag color={uncertain ? 'warning' : environment.state === 'Ready' && !ended ? 'success' : 'default'}>{ended && environment.state === 'Ready' ? 'Expired' : environment.state}</Tag><Typography.Text>{environment.profileId} · revision {environment.profileRevision}</Typography.Text></Flex>
        {uncertain && <Alert type="warning" showIcon title="Inspect before retrying" description={recoveryDescription} />}
        {environment.message && !ended && <Alert type="error" showIcon title="Environment setup failed" description={environment.message} />}
        {ended && <Alert type="info" showIcon title="Environment ended" description={initializationBlocked?"Initialization failed or was not confirmed. Inspect the retained output and canonical files, then explicitly reset before retrying. Scripts are never repeated automatically.":"The next authorized command creates a fresh environment from canonical /working. Saved Workspace files and Artifacts are retained."} />}
        <Descriptions bordered size="small" column={1}>
          {environment.profileName && <Descriptions.Item label="Pinned profile">{environment.profileName} · revision {environment.profileRevision}</Descriptions.Item>}
          {environment.imageReference && <Descriptions.Item label="Pinned image"><Typography.Text code style={{overflowWrap:'anywhere'}}>{environment.imageReference}</Typography.Text></Descriptions.Item>}
          {environment.createdAt && <Descriptions.Item label="Created">{new Date(environment.createdAt).toLocaleString()}</Descriptions.Item>}
          {environment.lastActivityAt && <Descriptions.Item label="Last activity">{new Date(environment.lastActivityAt).toLocaleString()}</Descriptions.Item>}
          {environment.maximumLifetimeAt && <Descriptions.Item label="Maximum lifetime">{new Date(environment.maximumLifetimeAt).toLocaleString()}</Descriptions.Item>}
          <Descriptions.Item label="Cleanup">{environment.cleanupPending ? `Pending · ${environment.cleanupPasses??0} reconciliation passes` : 'No pending cleanup'}{environment.cleanupReason?` · ${cleanupLabels[environment.cleanupReason]??environment.cleanupReason}`:''}</Descriptions.Item>
          {environment.lastCleanupAttemptAt && <Descriptions.Item label="Last cleanup attempt">{new Date(environment.lastCleanupAttemptAt).toLocaleString()}</Descriptions.Item>}
          <Descriptions.Item label={environment.activeExecutionId?"Execution reserved until":"Idle expiry"}>{new Date(environment.expiresAt).toLocaleString()}</Descriptions.Item>
          <Descriptions.Item label="Files">Canonical /working remains authoritative. Use workspace.copy to retain results in /home and publish files as Artifacts.</Descriptions.Item>
        </Descriptions>
        {environment.initialization && <section aria-label="Sandbox initialization"><Flex vertical gap={token.paddingSM}>
          <Flex wrap gap={token.paddingXS} align="center"><Typography.Text strong>Initialization</Typography.Text><Tag color={environment.initialization.state==='Succeeded'?'success':environment.initialization.state==='Failed'?'error':'processing'}>{environment.initialization.state}</Tag></Flex>
          <Typography.Text type="secondary">Profile revision {environment.initialization.profileRevision} · {environment.initialization.instanceRevision===null?'Inherited setup':`Instance revision ${environment.initialization.instanceRevision}`} · {environment.initialization.exitCode===null?'Exit unknown':`Exit ${environment.initialization.exitCode}`} · {(environment.initialization.durationMs/1000).toFixed(1)}s</Typography.Text>
          {environment.initialization.startedAt && <Typography.Text type="secondary">{new Date(environment.initialization.startedAt).toLocaleString()}{environment.initialization.finishedAt?` – ${new Date(environment.initialization.finishedAt).toLocaleString()}`:''}</Typography.Text>}
          <SandboxEvidence stdout={environment.initialization.stdout} stderr={environment.initialization.stderr} truncated={environment.initialization.truncated} />
          <DiagnosticDetails fields={{diagnosticId:environment.initialization.diagnosticId}} />
        </Flex></section>}
        {execution && <section aria-label="Sandbox command result">
          <Flex vertical gap={token.paddingSM}>
            <Flex wrap align="center" gap={token.paddingXS}><Typography.Text strong>Latest command</Typography.Text><Tag>{execution.status}</Tag><Typography.Text type="secondary">{execution.exitCode === null ? 'Exit unknown' : `Exit ${execution.exitCode}`} · {(execution.durationMs / 1000).toFixed(1)}s · {syncLabel[execution.workspaceSyncStatus] ?? "File synchronization unavailable"}</Typography.Text></Flex>
            <Typography.Paragraph code copyable={{ text: execution.commandText }} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{execution.commandText}</Typography.Paragraph>
            <Collapse defaultActiveKey={['stdout']} items={[
              { key: 'stdout', label: execution.background ? 'Command output (combined)' : 'Standard output', children: <section aria-label={execution.background ? "Command output (combined)" : "Standard output"}><Typography.Paragraph copyable={{ text: execution.stdout }} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{execution.stdout || 'No command output.'}</Typography.Paragraph></section> },
              { key: 'stderr', label: 'Standard error', children: <section aria-label="Standard error"><Typography.Paragraph copyable={{ text: execution.stderr }} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{execution.stderr || 'No standard error.'}</Typography.Paragraph></section> },
            ]} />
            {execution.truncated && <Alert type="info" showIcon title="Output was truncated at the bounded log limit." />}
            <DiagnosticDetails fields={{ diagnosticId: execution.diagnosticId ?? environment.diagnosticId }} />
            {execution.background && execution.status === 'Running' && <Button disabled={busy || reading || !!error || readOnly} loading={busy} onClick={() => confirmMutation('Stop this background command?', 'Confirm termination, then synchronize validated scratch files.', () => stopSandboxExecution(sessionId, execution.executionId), execution.executionId)}>Stop command</Button>}
            {!execution.background && ['Running','Dispatching'].includes(execution.status) && <Typography.Text type="secondary">Use Chat’s Stop control to cancel this active Run.</Typography.Text>}
          </Flex>
        </section>}
        <Flex wrap gap={token.paddingXS}><Button loading={reading} onClick={() => void refresh.current()}>Refresh status</Button><Button danger disabled={busy || reading || !!error || readOnly || environment.state === 'Terminated' && !initializationBlocked} loading={busy} onClick={() => confirmMutation('Reset this Session sandbox?', 'Stop processes and discard sandbox-only packages and unsynchronized files. Canonical /working, /home and Artifacts are preserved. The next command creates a fresh generation.', () => resetSessionSandbox(sessionId, environment.generation))}>Reset sandbox</Button></Flex>
      </>}
    </Flex>
  </Drawer>;
}
