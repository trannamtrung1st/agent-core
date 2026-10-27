import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { Alert, App, Button, Collapse, Empty, Flex, Form, Input, Select, Spin, Tag, Typography, theme } from 'antd';
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { identityRequest, IdentityError } from '../../services/identity';
import { checkpointStatus, goalsApi, goalStatus, readGoals, verificationLabel, writeGoal, type GoalCommand, type GoalPlan, type GoalVerification, type GoalsView } from '../../services/tenantSessionGoals';
import type { ActivitySource, AgentRun } from '../../services/activitySource';
import { confirmAction } from '../../app/confirmAction';
import { EvaluationAttemptInspector } from '../access/EvaluationAttemptInspector';
import { useAccessDraftGuard } from '../access/useAccessDraftGuard';
import { ArtifactView } from "./ArtifactView";
import { GoalPlanEditor } from './GoalPlanEditor';
import { requiredPrerequisiteIds } from './goalCheckpoints';
import { SessionSandboxFrame } from './SessionSandboxFrame';
import { AgentRunDetailDrawer } from './AgentRunDetails';

const privateDenial = (reason: unknown) => reason instanceof IdentityError && [401,403,404,410].includes(reason.status);
const verificationText = (v: GoalVerification) => v.kind === 0 ? `Exact Evaluation pass · Suite ${v.suiteId} · version ${v.suiteVersion} · hash ${v.suiteHash}` : verificationLabel[v.kind];
const blankAction = { note: '', runId: '', sourceId: '', evidenceKind: 0 };
export function SessionGoalsDrawer({ tenant, session, instance, source, open, narrow, readonly, refreshKey, openerRef, onClose, onWorked }: {
  tenant: string; session: string; instance: string; source: ActivitySource | null; open: boolean; narrow: boolean; readonly: boolean; refreshKey: string;
  openerRef: RefObject<HTMLButtonElement | null>; onClose: () => void; onWorked: () => void;
}) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [view, setView] = useState<GoalsView | null>(null); const [selectedId, setSelectedId] = useState<string | null>(() => new URLSearchParams(window.location.search).get("goal"));
  const [loading, setLoading] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const [editor, setEditor] = useState<'create' | 'revise' | null>(null); const [dirty, setDirty] = useState(false); const [reloadRequired, setReloadRequired] = useState(false);
  const [checkpointAction, setCheckpointAction] = useState<string | null>(null); const [action, setAction] = useState(blankAction);
  const [evaluationId, setEvaluationId] = useState<string | null>(null); const [run, setRun] = useState<AgentRun | null>(null);
  const [activity, setActivity] = useState<{ revision: number; action: string; at: string; actorId: string; note: string | null }[]>([]); const [activityPage, setActivityPage] = useState(0); const [activityTotal, setActivityTotal] = useState(0);
  const epoch = useRef(0); const readSequence = useRef(0); const controller = useRef(new AbortController()); const activeMutation = useRef(false);
  const guard = useAccessDraftGuard(dirty, busy, onClose);
  const goal = view?.goals.items.find(g => g.goalId === selectedId) ?? view?.goals.items.find(g => g.goalId === view.focusedGoalId) ?? view?.goals.items[0];
  const currentGoalId = useRef(goal?.goalId); currentGoalId.current = goal?.goalId;
  const retirePrivate = useCallback(() => {
    epoch.current++; readSequence.current++; controller.current.abort(); controller.current = new AbortController(); activeMutation.current = false;
    setBusy(false); setLoading(false); setView(null); setSelectedId(null); setActivity([]); setActivityPage(0); setActivityTotal(0);
    setRun(null); setEvaluationId(null); setEditor(null); setDirty(false); setCheckpointAction(null); setAction(blankAction); setReloadRequired(false);
  }, []);
  const canCreate = !!view?.canManage && !readonly && !reloadRequired;
  const wrongSpace = !!goal && goal.spaceId !== view?.currentSpaceId;
  const editable = canCreate && !wrongSpace;
  const refresh = useCallback(async (explicit = false) => {
    const generation = epoch.current; const sequence = ++readSequence.current;
    if (explicit) setLoading(true);
    try {
      const next = await readGoals(tenant, session, controller.current.signal);
      if (generation !== epoch.current || sequence !== readSequence.current) return;
      setView(next); if (explicit) { setError(null); setReloadRequired(false); }
    } catch (reason) {
      if (generation !== epoch.current || sequence !== readSequence.current || controller.current.signal.aborted) return;
      if (privateDenial(reason)) { retirePrivate(); }
      setError(reason instanceof Error ? reason.message : 'Goals could not be loaded.');
    } finally { if (generation === epoch.current && sequence === readSequence.current) setLoading(false); }
  }, [tenant, session, retirePrivate]);
  useEffect(() => {
    epoch.current++; controller.current.abort(); controller.current = new AbortController();
    activeMutation.current = false; setBusy(false); setView(null); setEditor(null); setDirty(false); setActivity([]); setError(null); setRun(null); setEvaluationId(null); setReloadRequired(false);
    if (open) void refresh(true);
    return () => { epoch.current++; controller.current.abort(); };
  }, [open, tenant, session, refresh]);
  useEffect(() => { if (open && !activeMutation.current) void refresh(); }, [open, refreshKey, refresh]);
  useEffect(() => { if (!open) return; const timer = setInterval(() => { if (!activeMutation.current) void refresh(); }, 3000); return () => clearInterval(timer); }, [open, refresh]);
  useEffect(() => { setCheckpointAction(null); setAction(blankAction); setActivity([]); setActivityPage(0); }, [goal?.goalId]);
  async function perform(work: (signal: AbortSignal) => Promise<unknown>, after?: () => void) {
    if (activeMutation.current) return; const generation = epoch.current; activeMutation.current = true; setBusy(true); setError(null);
    try { await work(controller.current.signal); if (generation === epoch.current) { after?.(); await refresh(true); } }
    catch (reason) { if (generation === epoch.current) { setError(reason instanceof Error ? reason.message : 'The change could not be confirmed. Reload the current Goal.'); setReloadRequired(true); if (privateDenial(reason)) { retirePrivate(); } } }
    finally { if (generation === epoch.current) { activeMutation.current = false; setBusy(false); } }
  }
  function command(kind: number, extra: Partial<GoalCommand> = {}, target = goal, after?: () => void) {
    if (!view) return;
    const body: GoalCommand = { kind, requestKey: crypto.randomUUID(), expectedSecurityRevision: view.securityRevision, expectedGoalRevision: kind === 0 ? 0 : target?.revision ?? 0, expectedPlanRevision: kind === 0 ? 0 : target?.planRevision ?? 0, ...extra };
    const id = kind === 0 ? crypto.randomUUID() : target!.goalId;
    void perform(signal => writeGoal(tenant, session, id, body, signal), () => { setSelectedId(id); after?.(); });
  }
  function savePlan(plan: GoalPlan) { command(editor === 'create' ? 0 : 1, { plan }, editor === 'create' ? undefined : goal, () => { setDirty(false); setEditor(null); }); }
  function decidePlan(accept: boolean) {
    if (!goal) return;
    confirmAction(modal, { title: accept ? goal.status === 0 ? 'Start this goal?' : 'Accept this revised plan?' : 'Reject the proposed revision?',
      content: accept ? 'Confirm the displayed outcome, criteria, verification methods and automatic continuation allowance. Verified history stays factual; the conversation remains active.' : 'The accepted plan remains in place.', okText: accept ? 'Accept plan' : 'Reject revision',
      onOk: () => command(2, { action: accept ? 'accept' : 'reject' }) });
  }
  async function inspectRun(id: string) {
    if (!source) return; const generation = epoch.current; const goalId = currentGoalId.current;
    try { const value = await source.getInstanceRun(instance, id); if (generation === epoch.current && currentGoalId.current === goalId) setRun(value); }
    catch (reason) { if (generation === epoch.current) { if (privateDenial(reason)) retirePrivate(); setError(reason instanceof Error ? reason.message : 'Source Run is unavailable.'); } }
  }
  async function loadActivity(page: number) {
    if (!goal) return; const generation = epoch.current; const id = goal.goalId;
    try { const result = await identityRequest<{ items: typeof activity; total: number }>(goalsApi(tenant, session, `/${id}/activity?page=${page}&pageSize=20`), { signal: controller.current.signal });
      if (generation === epoch.current && id === currentGoalId.current) { setActivity(result.items); setActivityPage(page); setActivityTotal(result.total); } }
    catch (reason) { if (generation === epoch.current) { if (privateDenial(reason)) retirePrivate(); setError(reason instanceof Error ? reason.message : 'Goal activity is unavailable.'); } }
  }
  const next = goal?.plan.checkpoints.find(p => ![4,5].includes(goal.checkpoints.find(c => c.checkpointId === p.checkpointId)!.status) && p.dependencies.every(id => goal.checkpoints.find(c => c.checkpointId === id)?.status === 4));
  const required = goal?.plan.checkpoints.filter(p => p.required) ?? [];
  const prerequisites = goal ? requiredPrerequisiteIds(goal) : new Set<string>();
  const verified = required.filter(p => goal?.checkpoints.find(c => c.checkpointId === p.checkpointId)?.status === 4).length;
  return <>{guard.holder}<SessionSandboxFrame title="Session goals" open={open} narrow={narrow} openerRef={openerRef} onClose={guard.close} blocked={busy}>
    <Flex wrap gap={token.paddingXS} justify="space-between"><Button icon={<ReloadOutlined aria-hidden />} disabled={busy || loading} onClick={() => void refresh(true)}>Refresh goals</Button>
      {canCreate && !editor && <Button icon={<PlusOutlined aria-hidden />} onClick={() => { setEditor('create'); setDirty(false); }}>Create goal</Button>}</Flex>
    {error && <Alert type="error" showIcon title={error} description={reloadRequired ? 'Your draft is retained. Refresh authoritative state, compare the current plan and submit deliberately.' : undefined} />}
    {loading && !view ? <Spin aria-label="Loading goals" /> : view && !goal && !editor ? <Empty description="No goals in this conversation"><Typography.Paragraph type="secondary">Create an explicit outcome with criteria and checkpoints when you want to track verified work.</Typography.Paragraph></Empty> : null}
    {view && goal && !editor && <>
      <Form.Item layout="vertical" label="Goals in this conversation" style={{ marginBottom: 0 }}><Select aria-label="Goals in this conversation" value={goal.goalId} disabled={busy} style={{ width: '100%' }} onChange={id => { setSelectedId(id); setActivity([]); }} options={view.goals.items.map(g => ({ value: g.goalId, label: `${g.plan.title} · ${goalStatus[g.status]}${g.goalId === view.focusedGoalId ? ' · Focused' : ''}` }))} /></Form.Item>
      <Flex vertical gap={token.paddingXS}>
        <Flex wrap align="center" gap={token.paddingXS}><Typography.Title level={3} style={{ margin: 0, overflowWrap: 'anywhere' }}>{goal.plan.title}</Typography.Title><Tag color={goal.status === 3 ? 'success' : undefined}>{goalStatus[goal.status]}</Tag></Flex>
        <Typography.Text aria-live="polite">{verified} of {required.length} required checkpoints verified</Typography.Text>
        <Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', margin: 0 }}>{goal.plan.objective}</Typography.Paragraph>
        <Typography.Text strong>Success criteria</Typography.Text><Typography.Paragraph style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', margin: 0 }}>{goal.plan.successCriteria}</Typography.Paragraph>
        {goal.plan.deadlineAt && <Typography.Text type="secondary">Deadline: {new Date(goal.plan.deadlineAt).toLocaleString()}</Typography.Text>}
        <Typography.Text type="secondary">{goal.plan.autoContinuation.enabled ? `${Math.max(0, goal.plan.autoContinuation.maxRuns - goal.autoRunsUsed)} automatic Runs remain · until ${new Date(goal.plan.autoContinuation.until!).toLocaleString()}` : 'Automatic continuation is off. Use Work on checkpoint deliberately.'}</Typography.Text>
        {goal.status === 3 && <Alert type="success" showIcon title="Goal completed" description="All required criteria were verified. This does not end the conversation." />}
        {readonly && <Alert type="info" showIcon title="Conversation history is read-only" description="Ending a conversation does not verify its open Goals." />}
      </Flex>
      {wrongSpace && <Alert type="warning" showIcon title="Return to this goal’s Space to continue" description="The goal stays in its creating Space. Close this panel and use the conversation’s Space selector, or create a separate goal in the current Space." />}
      {goal.pendingPlan && <Alert type="warning" showIcon title="A revised plan needs human review" description={<Flex vertical gap={token.paddingXS}>
        <Typography.Text>Accepted: {goal.plan.title} — {goal.plan.successCriteria}</Typography.Text><Typography.Text>Proposed: {goal.pendingPlan.title} — {goal.pendingPlan.successCriteria}</Typography.Text>
        <Collapse items={[{ key: 'diff', label: 'Compare full criteria and continuation policy', children: <Flex vertical gap={token.paddingSM}>{[goal.plan, goal.pendingPlan].map((plan, i) => <section key={i}><Typography.Text strong>{i ? 'Proposed plan' : 'Accepted plan'}</Typography.Text><Typography.Paragraph>{plan.objective}</Typography.Paragraph>{plan.checkpoints.map(p => <Typography.Paragraph style={{ overflowWrap: 'anywhere' }} key={p.checkpointId}>{p.title}: {p.expectedOutcome} · {p.required ? 'Required' : 'Optional'} · {p.verification.map(verificationText).join(' + ')}</Typography.Paragraph>)}<Typography.Text>Final acceptance: {plan.requireFinalAcceptance ? 'Required' : 'Off'} · Goal deadline: {plan.deadlineAt ?? 'None'} · Automatic Runs: {plan.autoContinuation.enabled ? plan.autoContinuation.maxRuns : 'Off'} · Continuation deadline: {plan.autoContinuation.until ?? 'None'}</Typography.Text></section>)}</Flex> }]} />
        {view.canAccept && !readonly && !wrongSpace && <Flex wrap gap={token.paddingXS}><Button disabled={busy || reloadRequired} onClick={() => decidePlan(true)}>Accept revised plan</Button><Button disabled={busy || reloadRequired} onClick={() => decidePlan(false)}>Reject revision</Button></Flex>}
      </Flex>} />}
      {verified === required.length && goal.status === 1 && !goal.plan.requireFinalAcceptance && <Alert type="warning" showIcon title="Current evidence must be requalified" description="Some required proof is no longer available with current access. Restore source access and verify its exact saved evidence again." />}
      <Flex wrap gap={token.paddingXS}>
        {goal.status === 0 && view.canAccept && !readonly && !wrongSpace && <Button type="primary" disabled={busy || reloadRequired} onClick={() => decidePlan(true)}>Start goal</Button>}
        {editable && goal.goalId !== view.focusedGoalId && <Button disabled={busy} onClick={() => void perform(signal => identityRequest(goalsApi(tenant, session, `/${goal.goalId}/focus`), { method: 'POST', signal, body: { requestId: crypto.randomUUID(), expectedSecurityRevision: view.securityRevision, expectedSessionRevision: view.sessionRevision, expectedGoalRevision: goal.revision } }))}>Focus goal</Button>}
        {editable && goal.status === 1 && !goal.pendingPlan && next && <Button type="primary" disabled={busy || goal.goalId !== view.focusedGoalId} onClick={() => void perform(signal => identityRequest(goalsApi(tenant, session, `/${goal.goalId}/continue`), { method: 'POST', signal, body: { requestId: crypto.randomUUID(), expectedSecurityRevision: view.securityRevision, expectedSessionRevision: view.sessionRevision, expectedGoalRevision: goal.revision, expectedPlanRevision: goal.planRevision } }), onWorked)}>Work on checkpoint</Button>}
        {editable && [1,2].includes(goal.status) && <Button disabled={busy} onClick={() => command(6, { action: goal.status === 1 ? 'pause' : 'resume' })}>{goal.status === 1 ? 'Pause goal' : 'Resume goal'}</Button>}
        {editable && goal.status < 3 && <Button disabled={busy} onClick={() => { setEditor('revise'); setDirty(false); }}>Revise plan</Button>}
        {editable && goal.status < 3 && <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: 'Cancel this goal?', content: 'Future Goal work will stop. Any current Run retains its admitted intent; its Stop control remains available in the conversation. Existing effects and evidence remain factual.', okText: 'Cancel goal', danger: true, onOk: () => command(6, { action: 'cancel' }) })}>Cancel goal</Button>}
      </Flex>
      {goal.goalId !== view.focusedGoalId && goal.status === 1 && <Typography.Text type="secondary">Focus this goal before starting work. Focus applies to future Runs.</Typography.Text>}
      <Collapse items={goal.plan.checkpoints.map((p, index) => {
        const state = goal.checkpoints.find(c => c.checkpointId === p.checkpointId)!; const eligible = p.dependencies.every(id => goal.checkpoints.find(c => c.checkpointId === id)?.status === 4);
        return { key: p.checkpointId, label: `${index + 1}. ${p.title} · ${checkpointStatus[state.status]}`, children: <Flex vertical gap={token.paddingSM}>
          <Typography.Paragraph style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{p.expectedOutcome}</Typography.Paragraph>
          <Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>{p.required ? 'Required' : 'Optional'} · Verify with {p.verification.map(verificationText).join(' + ')}</Typography.Text>
          {!eligible && <Typography.Text>Waiting for earlier dependencies to be verified.</Typography.Text>}
          {state.note && <Typography.Paragraph style={{ margin: 0 }}>{state.note}</Typography.Paragraph>}
          {state.sourceRunId && <Button disabled={busy} onClick={() => void inspectRun(state.sourceRunId!)}>Inspect source Run</Button>}
          {state.evidence?.map(e => <Flex wrap gap={token.paddingXS} key={`${e.kind}:${e.sourceId}`}>
            <Typography.Text>{verificationLabel[e.kind]}{e.principalId ? ` · by ${e.principalId}` : ''}</Typography.Text>
            {e.kind === 0 && <Button onClick={() => setEvaluationId(e.sourceId)}>Inspect Evaluation</Button>}
            {e.runId && <Button onClick={() => void inspectRun(e.runId!)}>Inspect evidence Run</Button>}
            {e.kind === 1 && <ArtifactView tenantId={tenant} sessionId={session} artifactId={e.sourceId} />}
            {state.status === 4 && e.kind !== 2 && editable && goal.status === 1 && !goal.pendingPlan && <Button disabled={busy} onClick={() => command(4, { checkpointId: p.checkpointId, evidence: e })}>Record evidence audit</Button>}
          </Flex>)}
          {state.status === 4 && <Typography.Text type="secondary">Verified evidence is preserved. This action audits the saved reference without revalidating its source.</Typography.Text>}
          {editable && goal.status === 1 && !goal.pendingPlan && state.status !== 4 && eligible && <Button onClick={() => { setCheckpointAction(p.checkpointId); setAction({ ...blankAction, runId: state.sourceRunId ?? '', evidenceKind: p.verification.find(v => v.kind !== 2)?.kind ?? 0 }); }}>Report or verify checkpoint</Button>}
          {checkpointAction === p.checkpointId && editable && goal.status === 1 && !goal.pendingPlan && state.status !== 4 && eligible && <Flex vertical gap={token.paddingSM}>
            {state.status !== 4 && <><Form.Item label="Progress or acceptance reason" style={{ margin: 0 }}><Input.TextArea aria-label="Progress or acceptance reason" rows={2} maxLength={2000} value={action.note} onChange={e => setAction(a => ({ ...a, note: e.target.value }))} /></Form.Item>
            <Flex wrap gap={token.paddingXS}><Button disabled={busy || !action.note.trim()} onClick={() => command(3, { checkpointId: p.checkpointId, note: action.note, action: 'report' })}>Report progress</Button><Button disabled={busy || !action.note.trim()} onClick={() => command(3, { checkpointId: p.checkpointId, note: action.note, action: 'blocked' })}>Report blocker</Button>
              {!p.required && <Button disabled={busy || !action.note.trim() || prerequisites.has(p.checkpointId)} onClick={() => command(3, { checkpointId: p.checkpointId, note: action.note, action: 'skip' })}>Skip optional checkpoint</Button>}
              {view.canAccept && p.verification.some(v => v.kind === 2) && <><Button disabled={busy || !action.note.trim()} onClick={() => confirmAction(modal, { title: 'Accept this checkpoint?', content: `${p.expectedOutcome} — Your signed-in acceptance will be recorded against plan revision ${goal.planRevision}.`, okText: 'Accept criterion', onOk: () => command(5, { checkpointId: p.checkpointId, note: action.note, action: 'accept' }) })}>Accept criterion</Button><Button disabled={busy || !action.note.trim()} onClick={() => command(5, { checkpointId: p.checkpointId, note: action.note, action: 'reject' })}>Reject criterion</Button></>}
            </Flex></>}
            {!p.required && prerequisites.has(p.checkpointId) && <Typography.Text type="secondary">Cannot skip: an unfinished required outcome depends on this checkpoint. Verify it or propose a revised plan.</Typography.Text>}
            {p.verification.some(v => v.kind !== 2) && <>
              <Form.Item label="Evidence method" style={{ margin: 0 }}><Select aria-label="Evidence method" value={action.evidenceKind} options={p.verification.filter(v => v.kind !== 2).map(v => ({ value: v.kind, label: verificationLabel[v.kind] }))} onChange={kind => setAction(a => ({ ...a, evidenceKind: kind }))} /></Form.Item>
              <Form.Item label="Exact source Run ID" style={{ margin: 0 }}><Input aria-label="Exact source Run ID" value={action.runId} onChange={e => setAction(a => ({ ...a, runId: e.target.value }))} /></Form.Item>
              <Form.Item label={action.evidenceKind === 0 ? 'Saved Evaluation attempt ID' : 'Published Artifact ID'} style={{ margin: 0 }}><Input aria-label={action.evidenceKind === 0 ? 'Saved Evaluation attempt ID' : 'Published Artifact ID'} value={action.sourceId} onChange={e => setAction(a => ({ ...a, sourceId: e.target.value }))} /></Form.Item>
              <Button disabled={busy || !action.runId || !action.sourceId} onClick={() => command(4, { checkpointId: p.checkpointId, evidence: { kind: action.evidenceKind, sourceId: action.sourceId, runId: action.runId } })}>Verify saved evidence</Button>
            </>}
          </Flex>}
        </Flex> };
      })} />
      {verified === required.length && goal.status === 1 && goal.plan.requireFinalAcceptance && view.canAccept && editable && <Flex vertical gap={token.paddingXS}><Typography.Text>All required checkpoints passed. Final human acceptance is required.</Typography.Text><Input.TextArea aria-label="Final acceptance reason" value={action.note} maxLength={2000} onChange={e => setAction(a => ({ ...a, note: e.target.value }))} /><Button type="primary" disabled={busy || !action.note.trim() || reloadRequired} onClick={() => confirmAction(modal, { title: 'Accept the completed goal?', content: goal.plan.successCriteria, okText: 'Accept completed goal', onOk: () => command(5, { action: 'accept', note: action.note }) })}>Accept completed goal</Button></Flex>}
      <Collapse items={[{ key: 'activity', label: 'Goal activity and attribution', children: <Flex vertical gap={token.paddingXS}><Button onClick={() => void loadActivity(0)}>Read goal activity</Button>{activity.map(item => <Typography.Paragraph key={item.revision} style={{ margin: 0, overflowWrap: 'anywhere' }}>{item.action} · revision {item.revision} · {new Date(item.at).toLocaleString()} · {item.actorId}{item.note ? ` — ${item.note}` : ''}</Typography.Paragraph>)}<Flex gap={token.paddingXS}><Button disabled={activityPage === 0} onClick={() => void loadActivity(activityPage - 1)}>Newer activity</Button><Button disabled={(activityPage + 1) * 20 >= activityTotal} onClick={() => void loadActivity(activityPage + 1)}>Older activity</Button></Flex></Flex> }]} />
    </>}
    {editor && view && <GoalPlanEditor key={`${editor}:${goal?.goalId ?? 'new'}`} tenant={tenant} initial={editor === 'revise' ? goal?.plan : undefined} busy={busy} reloadRequired={reloadRequired} onSave={savePlan} onDirty={() => setDirty(true)} onCancel={() => { if (!dirty) setEditor(null); else confirmAction(modal, { title: 'Discard this draft?', content: 'The accepted Goal stays unchanged.', okText: 'Discard draft', onOk: () => { setDirty(false); setEditor(null); } }); }} />}
  </SessionSandboxFrame>
    {open && evaluationId && <EvaluationAttemptInspector tenant={tenant} id={evaluationId} canReview={false} onClose={() => setEvaluationId(null)} onSaved={() => void refresh()} />}
    {open && run && <AgentRunDetailDrawer run={run} open wide={!narrow} onClose={() => setRun(null)} onChange={setRun} />}
  </>;
}
