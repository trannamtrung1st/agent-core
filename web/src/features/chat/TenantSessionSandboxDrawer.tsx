import { useEffect, useRef, useState, type RefObject } from 'react';
import { Alert, App, Button, Descriptions, Empty, Flex, Input, Select, Tag, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { IdentityCompletionError, IdentityError, identityRequest } from '../../services/identity';
import { tenantSessionSandboxApi, tenantSandboxWitness, type TenantSandboxControls, type TenantSandboxExecution, type TenantSandboxObservation } from '../../services/tenantSessionSandbox';
import { sandboxStatusLabel } from '../../services/sandboxes';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { useTenantCommand } from '../access/useTenantCommand';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { SandboxEvidence } from '../sandbox/SandboxEvidence';
import { DiagnosticDetails } from './DiagnosticDetails';
import { SessionSandboxFrame } from './SessionSandboxFrame';

export function TenantSessionSandboxDrawer({ tenant, organization, sessionId, open, narrow, openerRef, onClose }: { tenant: string; organization: string; sessionId: string; open: boolean; narrow: boolean; openerRef?: RefObject<HTMLButtonElement | null>; onClose: () => void }) {
  const [busy, setBusy] = useState(false);
  return <SessionSandboxFrame open={open} narrow={narrow} openerRef={openerRef} onClose={onClose} blocked={busy}>
    {open && <SessionSandboxBody key={`${tenant}:${sessionId}`} tenant={tenant} organization={organization} session={sessionId} onBusy={setBusy} />}
  </SessionSandboxFrame>;
}

function SessionSandboxBody({ tenant, organization, session, onBusy }: { tenant: string; organization: string; session: string; onBusy: (value: boolean) => void }) {
  const [attempt, setAttempt] = useState(0); const [busy, setBusy] = useState(false); const [notice, setNotice] = useState<string>(); const [readLoss, setReadLoss] = useState<unknown>();
  const reloadAction = useRef<HTMLButtonElement>(null); const restoreFocus = useRef(false);
  const state = useIdentityQuery<TenantSandboxControls>(tenantSessionSandboxApi(tenant, session, '/controls'), String(attempt));
  useEffect(() => { onBusy(busy); return () => onBusy(false); }, [busy, onBusy]);
  useEffect(() => { if (!state.loading && restoreFocus.current) { restoreFocus.current = false; requestAnimationFrame(() => reloadAction.current?.focus()); } }, [state.loading]);
  const reload = () => { restoreFocus.current = true; setReadLoss(undefined); setAttempt(value => value + 1); };
  return <>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Stored Sandbox evidence for {organization}. Reload to check current saved state. This does not confirm live provider readiness or grant command authority.</Typography.Paragraph>
    <Button aria-label="Reload Sandbox" ref={reloadAction} style={{ alignSelf: 'flex-start' }} loading={state.loading} disabled={busy} onClick={reload}>Reload Sandbox</Button>
    {notice && <Alert type="info" showIcon title={notice} />}
    {readLoss !== undefined ? <AccessError error={readLoss} /> : state.error ? <AccessError error={state.error} /> : state.loading ? <AccessLoading /> : state.data && <SessionSandboxControls key={attempt} tenant={tenant} organization={organization} session={session} view={state.data} onBusy={setBusy} readLost={setReadLoss} completed={acknowledged => { setNotice(acknowledged ? 'Request acknowledged. Use stored evidence to confirm cleanup or selection; acceptance is separate from completion.' : 'The request response could not be read. No request was repeated; read stored evidence before another control.'); reload(); }} />}
  </>;
}

function SessionSandboxControls({ tenant, organization, session, view, onBusy, readLost, completed }: { tenant: string; organization: string; session: string; view: TenantSandboxControls; onBusy: (value: boolean) => void; readLost: (error: unknown) => void; completed: (acknowledged: boolean) => void }) {
  const { observation: observation } = view; const environment = observation.environment; const layout = useAdminDetailLayout();
  const [selected, setSelected] = useState<string>(); const [submitted, setSubmitted] = useState(false); const [confirming, setConfirming] = useState(false);
  const command = useTenantCommand(); const { modal } = App.useApp(); const confirmation = useRef<ReturnType<typeof modal.confirm> | null>(null); const active = useRef(true);
  const pending = command.busy || confirming;
  useEffect(() => { onBusy(pending); }, [pending, onBusy]);
  useEffect(() => { active.current = true; return () => { active.current = false; confirmation.current?.destroy(); onBusy(false); }; }, [onBusy]);
  const profile = view.profiles.find(p => p.id === selected);
  const confirm = (kind: 'reset' | 'stop' | 'profile') => {
    if (pending || submitted || confirmation.current || !environment || kind === 'reset' && !view.canReset || kind === 'stop' && !view.canStop || kind === 'profile' && !profile?.canSelect) return;
    const execution = observation.execution; const target = profile;
    if (kind === 'stop' && !execution) return;
    setConfirming(true);
    const context = `${organization}. Session ${session}; generation ${environment.generation}; lease revision ${environment.revision}; Session revision ${observation.sessionRevision}. `;
    confirmation.current = confirmAction(modal, { title: kind === 'reset' ? 'Reset this Session sandbox?' : kind === 'stop' ? 'Stop this exact command?' : 'Switch this Session profile?',
      okText: kind === 'reset' ? 'Request reset' : kind === 'stop' ? 'Request Stop' : 'Request profile switch', danger: kind === 'reset',
      content: context + (kind === 'reset' ? 'Request retirement and clear the Session profile choice. Canonical /working, /home and Artifacts are preserved. Writes may continue until retirement is confirmed. Future initialization or commands require fresh exact Review.'
        : kind === 'stop' ? `Execution ${execution!.executionId}, revision ${execution!.revision}. Request interruption of this exact command; partial file writes remain. Acceptance does not confirm that processes stopped.`
          : `Choose ${target!.name}, profile revision ${target!.revision}, Instance revision ${view.instanceRevision}. Retire the old generation first. Preserve canonical files; no replacement or initialization starts here. Future commands require fresh exact Review.`),
      afterClose: () => { confirmation.current = null; if (active.current) setConfirming(false); }, onOk: () => {
        setSubmitted(true); return command.run(async signal => {
          const witnesses = tenantSandboxWitness(view); let acknowledged = true;
          const body = kind === 'stop' ? { expectedRevision: witnesses.expectedRevision, expectedSessionRevision: witnesses.expectedSessionRevision,
            expectedGeneration: witnesses.expectedGeneration, expectedLeaseRevision: witnesses.expectedLeaseRevision, expectedExecutionRevision: execution!.revision }
            : kind === 'profile' ? { ...witnesses, profileId: target!.id, expectedProfileRevision: target!.revision, expectedInstanceRevision: view.instanceRevision } : witnesses;
          try { await identityRequest(tenantSessionSandboxApi(tenant, session, kind === 'stop' ? `/executions/${encodeURIComponent(execution!.executionId)}/stop` : `/${kind}`), { method: 'POST', signal, body }); }
          catch (error) { if (error instanceof IdentityCompletionError) acknowledged = false; else { if (error instanceof IdentityError && [401, 403, 404].includes(error.status)) readLost(error); throw error; } }
          return acknowledged;
        }, completed);
      }
    });
  };
  return <Flex vertical gap="var(--ac-space-section)">
    {command.error !== undefined && <AccessError error={command.error} />}
    {submitted && <Typography.Text role="status" type="secondary">No request is repeated automatically. Reload Sandbox before another control.</Typography.Text>}
    {!environment ? <Empty description="No stored Sandbox environment. An authorized command can create a generation; this inspector does not provision one." /> : <>
      <Flex align="center" wrap gap="var(--ac-space-compact)"><Tag>Stored: {environment.storedState}</Tag><Typography.Text>{environment.profileName} · revision {environment.profileRevision}</Typography.Text></Flex>
      {environment.cleanupPending && <Alert type="warning" showIcon title="Cleanup pending" description="Complete or partial writes may continue until termination is confirmed. Inspect stored evidence; do not replay the command." />}
      {environment.message && <Alert type="info" showIcon title="Stored guidance" description={environment.message} />}
      {environment.selectionRestricted ? <Alert type="info" showIcon title="Selected profile details are restricted" /> : environment.selection && <Alert type="info" showIcon title="Profile selection recorded" description={`Profile ${view.profiles.find(p => p.id === environment.selection!.profileId)?.name ?? environment.selection.profileId}, revision ${environment.selection.profileRevision} · Instance revision ${environment.selection.instanceRevision} · ${environment.selection.storedPhase === 'retirement' ? 'Retirement pending' : 'Awaiting fresh Review'}. No initialization is started by selection.`} />}
      <Descriptions {...layout} bordered size="small" column={1} items={[
        { label: 'Pinned image', children: <Typography.Text code style={{ overflowWrap: 'anywhere' }}>{environment.imageReference}</Typography.Text> },
        { label: 'Generation', children: environment.generation }, { label: 'Saved revisions', children: `Lease ${environment.revision} · Session ${observation.sessionRevision} · Instance ${view.instanceRevision}` },
        { label: 'Created', children: new Date(environment.createdAt).toLocaleString() }, { label: 'Stored expiry', children: new Date(environment.expiresAt).toLocaleString() },
        { label: 'Canonical files', children: '/working, authorized /home and Artifacts are preserved by reset and profile switching. Failed or cancelled commands can leave partial writes.' },
        { label: 'Storage enforcement', children: environment.storageEnforcement === 'sampled' ? 'Sampled budgets; files written before a limit remain.' : 'Inspect current configuration.' }
      ]} />
      {environment.initialization && <section aria-label="Stored initialization"><Flex vertical gap="var(--ac-space-compact)"><Typography.Title level={5}>Stored initialization</Typography.Title><Tag style={{ alignSelf: 'flex-start' }}>{environment.initialization.state}</Tag><Typography.Text type="secondary">Profile revision {environment.initialization.profileRevision} · Instance revision {environment.initialization.instanceRevision ?? 'unavailable'} · {environment.initialization.exitCode === null ? 'Exit unknown' : `Exit ${environment.initialization.exitCode}`} · {(environment.initialization.durationMs / 1000).toFixed(1)}s</Typography.Text>{environment.initialization.startedAt && <Typography.Text type="secondary">{new Date(environment.initialization.startedAt).toLocaleString()}{environment.initialization.finishedAt ? ` – ${new Date(environment.initialization.finishedAt).toLocaleString()}` : ''}{environment.initialization.phase ? ` · ${environment.initialization.phase} phase` : ''}</Typography.Text>}<SandboxEvidence {...environment.initialization} /></Flex></section>}
      {observation.execution && <SandboxCommandObservation execution={observation.execution} session={session} label="Latest stored command" />}
    </>}
    <section aria-label="Session Sandbox controls"><Flex vertical gap="var(--ac-space-default)"><Typography.Title level={5}>Session controls</Typography.Title>
      <Typography.Text type="secondary">Only readable profiles allowed by current Instance policy appear. Reusable verification is separate from this Session’s readiness and command authority.</Typography.Text>
      <Select aria-label="Profile for this Session" placeholder="Choose an available profile" value={selected} disabled={pending || submitted || !environment} options={view.profiles.map(p => ({ value: p.id, label: `${p.name} · revision ${p.revision} · ${sandboxStatusLabel(p)}`, disabled: !p.canSelect }))} onChange={setSelected} />
      <Flex wrap gap="var(--ac-space-compact)"><Button disabled={pending || submitted || !profile?.canSelect} onClick={() => confirm('profile')}>Switch profile</Button><Button disabled={pending || submitted || !view.canStop} loading={command.busy} onClick={() => confirm('stop')}>Stop command</Button><Button danger disabled={pending || submitted || !view.canReset} loading={command.busy} onClick={() => confirm('reset')}>Reset sandbox</Button></Flex>
      {!view.canReset && !view.canStop && <Typography.Text type="secondary">Controls require an applicable stored target, Session management and exact agent Sandbox management. Read access alone grants no control.</Typography.Text>}
    </Flex></section>
    <ExactSandboxReceipt tenant={tenant} session={session} disabled={pending} />
  </Flex>;
}

function SandboxCommandObservation({ execution: e, session, label }: { execution: TenantSandboxExecution; session: string; label: string }) {
  const layout = useAdminDetailLayout();
  return <section aria-label={label}><Flex vertical gap="var(--ac-space-compact)"><Typography.Title level={5}>{label}</Typography.Title><Flex wrap gap="var(--ac-space-compact)"><Tag>{e.state}</Tag>{e.stopRequested && <Tag>Stop requested</Tag>}</Flex>
    <Descriptions {...layout} size="small" column={1} items={[{ label: 'Receipt', children: e.executionId }, { label: 'Generation', children: e.generation }, { label: 'Result', children: `${e.exitCode === null ? 'Exit unknown' : `Exit ${e.exitCode}`} · ${e.durationMs === null ? 'Duration unavailable' : `${(e.durationMs / 1000).toFixed(1)}s`}` },
      { label: 'File effects', children: e.filesystemEffectsMayHaveOccurred ? 'Complete or partial writes may have occurred.' : 'No dispatch recorded.' }, { label: 'Termination', children: e.terminationConfirmed ? 'Termination confirmed' : 'Termination unconfirmed' }]} />
    <Typography.Paragraph code copyable={{ text: e.commandText }} style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{e.commandText}</Typography.Paragraph>
    <DiagnosticDetails label="Command diagnostics" fields={{ diagnosticId: e.diagnosticId, sessionId: session }} />
    <SandboxEvidence stdout={e.stdout} stderr={e.stderr} truncated={e.truncated} label="Command" combined={e.background} />
  </Flex></section>;
}
function ExactSandboxReceipt({ tenant, session, disabled }: { tenant: string; session: string; disabled: boolean }) {
  const [draft, setDraft] = useState(''); const [id, setId] = useState<string>(); const [version, setVersion] = useState(0);
  const valid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(draft.trim());
  const state = useIdentityQuery<TenantSandboxObservation>(id ? tenantSessionSandboxApi(tenant, session, `/executions/${encodeURIComponent(id)}`) : null, String(version));
  return <section aria-label="Inspect a saved execution"><Flex vertical gap="var(--ac-space-default)"><Typography.Title level={5}>Inspect a saved execution</Typography.Title><Typography.Text type="secondary">Enter an exact Core receipt ID. This read does not change the active generation or authorize Stop.</Typography.Text><Input aria-label="Execution receipt ID" maxLength={36} value={draft} disabled={disabled} onChange={event => setDraft(event.target.value)} /><Button style={{ alignSelf: 'flex-start' }} disabled={disabled || !valid || !!id && state.loading} onClick={() => { setId(draft.trim()); setVersion(value => value + 1); }}>Inspect exact receipt</Button>
    {id && (state.error ? <AccessError error={state.error} /> : state.loading ? <AccessLoading /> : state.data?.execution && <SandboxCommandObservation execution={state.data.execution} session={session} label="Exact stored receipt" />)}
  </Flex></section>;
}
