import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Spin, Tag, Typography, theme } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { getSandboxExecution, getSessionSandbox, resetSessionSandbox, stopSandboxExecution, type SandboxEnvironment, type SandboxExecution } from '../../services/sandboxes';
import { DiagnosticDetails } from './DiagnosticDetails';

export function SessionSandboxDrawer({ sessionId, open, narrow, readOnly, onClose }: { sessionId: string; open: boolean; narrow: boolean; readOnly?: boolean; onClose: () => void }) {
  const { token } = theme.useToken(); const { modal, message } = App.useApp();
  const [environment, setEnvironment] = useState<SandboxEnvironment | null>(null); const [execution, setExecution] = useState<SandboxExecution | null>(null);
  const [loading, setLoading] = useState(true); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const epoch = useRef(0); const selected = useRef(sessionId); selected.current = sessionId;
  const opener = useRef<HTMLElement | null>(null);
  useEffect(() => { if (open && document.activeElement instanceof HTMLElement) opener.current = document.activeElement; }, [open]);
  const refresh = useRef<() => Promise<void>>(async () => {});
  const observed = useRef({ environment, execution, error }); observed.current = { environment, execution, error };
  useEffect(() => {
    const generation = ++epoch.current; setEnvironment(null); setExecution(null); setError(null); setLoading(true); setBusy(false);
    if (!open) return;
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
    async function load() {
      try {
        const next = await getSessionSandbox(sessionId, controller.signal);
        const id = next?.activeExecutionId ?? next?.lastExecutionId;
        const result = id ? await getSandboxExecution(sessionId, id, controller.signal) : null;
        if (controller.signal.aborted || epoch.current !== generation) return;
        setEnvironment(next); setExecution(result); setError(null);
      } catch { if (!controller.signal.aborted && epoch.current === generation) setError('Sandbox status is unavailable. Retry before stopping or resetting; previous evidence is retained.'); }
      finally { if (!controller.signal.aborted && epoch.current === generation) setLoading(false); }
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
      if (epoch.current !== generation || selected.current !== sessionId || observed.current.error || observed.current.environment?.generation !== sandboxGeneration
          || executionId && observed.current.execution?.executionId !== executionId) { message.warning('Selection changed. Refresh before recovery.'); return; }
      await mutate(action);
    } });
  }
  const uncertain = environment?.state === 'NeedsResync' || environment?.state === 'NeedsReconciliation' || execution?.status === 'Indeterminate';
  return <Drawer title="Session sandbox" open={open} onClose={onClose} size={narrow ? '100%' : 640} destroyOnHidden
    afterOpenChange={visible => { if (!visible && opener.current?.isConnected) opener.current.focus(); }}>
    <Flex vertical gap={token.padding} onKeyDownCapture={event => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose(); }
    }}>
      {error && <Alert type="error" showIcon title={error} action={<Button onClick={() => void refresh.current()}>Retry</Button>} />}
      {loading ? <Spin aria-label="Loading Session sandbox" /> : !environment ? <Empty description="No sandbox environment yet. An authorized command provisions the selected Workbench for this Session." /> : <>
        <Flex align="center" wrap gap={token.paddingXS}><Tag color={uncertain ? 'warning' : environment.state === 'Ready' ? 'success' : 'default'}>{environment.state}</Tag><Typography.Text>{environment.profileId} · revision {environment.profileRevision}</Typography.Text></Flex>
        {uncertain && <Alert type="warning" showIcon title="Inspect before retrying" description="The command outcome or scratch synchronization is uncertain. Commands are not repeated automatically. Reset discards sandbox-only state and retains canonical Workspace files and Artifacts." />}
        <Descriptions bordered size="small" column={1}>
          <Descriptions.Item label="Expires">{new Date(environment.expiresAt).toLocaleString()}</Descriptions.Item>
          <Descriptions.Item label="Files">Canonical /working remains authoritative. Use workspace.copy to retain results in /home and publish files as Artifacts.</Descriptions.Item>
        </Descriptions>
        {execution && <section aria-label="Sandbox command result">
          <Flex vertical gap={token.paddingSM}>
            <Flex wrap align="center" gap={token.paddingXS}><Typography.Text strong>Latest command</Typography.Text><Tag>{execution.status}</Tag><Typography.Text type="secondary">{execution.exitCode === null ? 'Exit unknown' : `Exit ${execution.exitCode}`} · {(execution.durationMs / 1000).toFixed(1)}s · {execution.workspaceSyncStatus}</Typography.Text></Flex>
            <Typography.Paragraph code copyable={{ text: execution.commandText }} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{execution.commandText}</Typography.Paragraph>
            <Collapse defaultActiveKey={['stdout']} items={[
              { key: 'stdout', label: execution.background ? 'Command output (combined)' : 'Standard output', children: <section aria-label={execution.background ? "Command output (combined)" : "Standard output"}><Typography.Paragraph copyable={{ text: execution.stdout }} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{execution.stdout || 'No command output.'}</Typography.Paragraph></section> },
              { key: 'stderr', label: 'Standard error', children: <section aria-label="Standard error"><Typography.Paragraph copyable={{ text: execution.stderr }} style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{execution.stderr || 'No standard error.'}</Typography.Paragraph></section> },
            ]} />
            {execution.truncated && <Alert type="info" showIcon title="Output was truncated at the bounded log limit." />}
            <DiagnosticDetails fields={{ diagnosticId: execution.diagnosticId ?? environment.diagnosticId }} />
            {execution.background && execution.status === 'Running' && <Button disabled={busy || !!error || readOnly} loading={busy} onClick={() => confirmMutation('Stop this background command?', 'Confirm termination, then synchronize validated scratch files.', () => stopSandboxExecution(sessionId, execution.executionId), execution.executionId)}>Stop command</Button>}
            {!execution.background && ['Running','Dispatching'].includes(execution.status) && <Typography.Text type="secondary">Use Chat’s Stop control to cancel this active Run.</Typography.Text>}
          </Flex>
        </section>}
        <Flex wrap gap={token.paddingXS}><Button onClick={() => void refresh.current()}>Refresh status</Button><Button danger disabled={busy || !!error || readOnly || environment.state === 'Terminated'} loading={busy} onClick={() => confirmMutation('Reset this Session sandbox?', 'Stop processes and discard sandbox-only packages and unsynchronized files. Canonical /working, /home and Artifacts are preserved. The next command creates a fresh generation.', () => resetSessionSandbox(sessionId, environment.generation))}>Reset sandbox</Button></Flex>
      </>}
    </Flex>
  </Drawer>;
}
