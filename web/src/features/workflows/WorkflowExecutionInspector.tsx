import { useEffect, useRef, useState, type RefObject } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Flex, Form, Grid, Input, Select, Tabs, Tag, Timeline, Typography } from 'antd';
import { confirmAction } from '../../app/confirmAction';
import { followAppLink } from '../../app/appLink';
import { IdentityCompletionError, IdentityError, identityRequest, type TenantContext } from '../../services/identity';
import { executionApi, readWorkflowGraph, terminalWorkflow, workflowApi, workflowPath, workflowStatus, type WorkflowExecution, type WorkflowGoal, type WorkflowStep, type WorkflowVersion } from '../../services/tenantWorkflows';
import { freshTenantRevision } from '../../services/tenantEvaluations';
import { tenantAccessPath, tenantAgentPath } from '../access/accessRoute';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { EvaluationAttemptInspector } from '../access/EvaluationAttemptInspector';
import { useAccessDraftGuard } from '../access/useAccessDraftGuard';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { useTenantCommand } from '../access/useTenantCommand';
import { SessionSandboxFrame } from '../chat/SessionSandboxFrame';
import { SessionGoalsDrawer } from '../chat/SessionGoalsDrawer';
import { TaskCenterDrawer } from '../chat/TaskCenterDrawer';
import { WorkflowDiagram } from './WorkflowDiagram';
import { WorkflowOutline } from './WorkflowOutline';

function useWorkflowMonitor(tenant: string, execution: string) {
  const [state, setState] = useState<{ data?: WorkflowExecution; error?: unknown; loading: boolean }>({ loading: true }); const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController(); let timer: number | undefined;
    async function read() {
      try {
        const data = await identityRequest<WorkflowExecution>(executionApi(tenant, `/${execution}`), { signal: controller.signal });
        if (data.executionId !== execution) throw new IdentityCompletionError();
        if (!controller.signal.aborted) { setState({ data, loading: false }); timer = window.setTimeout(() => void read(), terminalWorkflow(data.status) ? 5000 : 2000); }
      } catch (error) { if (!controller.signal.aborted) setState({ error, loading: false }); }
    }
    void read(); return () => { controller.abort(); window.clearTimeout(timer); };
  }, [tenant, execution, attempt]);
  return { ...state, reload: () => setAttempt(v => v + 1) };
}
type Pending = { kind: 'cancel' } | { kind: 'decision'; nodeId: string; approved: boolean };
export function WorkflowExecutionInspector({ context, id, openerRef, onClose }: { context: TenantContext; id: string; openerRef?: RefObject<HTMLButtonElement | null>; onClose: () => void }) {
  const tenant = context.tenant.tenantId; const screens = Grid.useBreakpoint(); const { modal, message } = App.useApp(); const state = useWorkflowMonitor(tenant, id);
  const value = state.data; const publication = useIdentityQuery<WorkflowVersion>(value ? workflowApi(tenant, `/${value.workflowId}/versions/${value.version}`) : null);
  const operation = useTenantCommand(); const [dirty, setDirty] = useState(false); const [pending, setPending] = useState<Pending>(); const [selected, setSelected] = useState<string>();
  const [task, setTask] = useState<string>(); const [evaluation, setEvaluation] = useState<string>(); const [goal, setGoal] = useState<WorkflowGoal>(); const sourceOpener = useRef<HTMLButtonElement | null>(null);
  const guard = useAccessDraftGuard(dirty || !!pending, operation.busy, onClose);
  const graph = publication.data ? readWorkflowGraph(publication.data.graph.canonicalJson) : undefined;
  const sourceLabel = value?.sourceKind === 'HumanManual' ? 'Human request' : value?.sourceKind === 'ClaimedAgent' ? 'Claimed Agent Run' : 'Automation occurrence';
  async function recover() {
    await operation.run(async signal => { const observed = await identityRequest<WorkflowExecution>(executionApi(tenant, `/${id}`), { signal }); if (observed.executionId !== id) throw new IdentityCompletionError(); return observed; }, observed => {
      const recorded = pending?.kind === 'cancel' ? observed.cancellationRequested
        : pending?.kind === 'decision' ? observed.steps.some(step => step.nodeId === pending.nodeId && step.outcome === (pending.approved ? 'approve' : 'reject')) : true;
      if (recorded || terminalWorkflow(observed.status)) { setPending(undefined); setDirty(false); void message.info('Current saved status loaded.'); }
      state.reload();
    });
  }
  function cancel() {
    if (!value?.canCancel || pending) return;
    confirmAction(modal, { title: 'Cancel this Workflow execution?', content: 'New steps will stop. Existing Tasks reconcile their current work; confirmed effects remain recorded and uncertain effects require inspection.', okText: 'Request cancellation', danger: true,
      onOk: () => operation.run(async signal => {
        const expectedSecurityRevision = await freshTenantRevision(tenant, signal); setPending({ kind: 'cancel' });
        try { const receipt = await identityRequest<{ executionId: string }>(executionApi(tenant, `/${id}/cancel`), { method: 'POST', signal, body: { requestKey: crypto.randomUUID(), expectedRevision: value.revision, expectedSecurityRevision } }); if (receipt.executionId !== id) throw new IdentityCompletionError(); }
        catch (failure) { if (failure instanceof IdentityError && failure.status < 500) setPending(undefined); throw failure; }
      }, () => { setPending(undefined); state.reload(); void message.success('Cancellation requested.'); }) });
  }
  function decide(step: WorkflowStep, approved: boolean, rationale?: string) {
    if (!value || pending) return;
    void operation.run(async signal => {
      const expectedSecurityRevision = await freshTenantRevision(tenant, signal); setPending({ kind: 'decision', nodeId: step.nodeId, approved });
      try {
        const receipt = await identityRequest<{ executionId: string; nodeId: string; approved: boolean }>(executionApi(tenant, `/${id}/nodes/${encodeURIComponent(step.nodeId)}/decision`), { method: 'POST', signal,
          body: { approved, rationale, requestKey: crypto.randomUUID(), expectedRevision: value.revision, expectedStepRevision: step.revision, expectedSecurityRevision } });
        if (receipt.executionId !== id || receipt.nodeId !== step.nodeId || receipt.approved !== approved) throw new IdentityCompletionError();
      } catch (failure) { if (failure instanceof IdentityError && failure.status < 500) setPending(undefined); throw failure; }
    }, () => { setPending(undefined); setDirty(false); state.reload(); void message.success('Procedural decision recorded.'); });
  }
  return <>{guard.holder}<SessionSandboxFrame open title="Workflow execution" narrow={!screens.md} openerRef={openerRef} onClose={guard.close} blocked={operation.busy || !!task || !!evaluation || !!goal}>
    <Flex wrap gap="var(--ac-space-compact)" justify="space-between"><Button disabled={operation.busy} onClick={state.reload}>Refresh status</Button>{value?.canCancel && <Button danger disabled={operation.busy || !!pending} onClick={cancel}>Cancel execution</Button>}</Flex>
    {operation.error !== undefined && <AccessError error={operation.error} />}
    {pending && <Alert type="warning" showIcon title="The last action is unconfirmed" description="Check its saved state before any further action. No mutation is repeated." action={<Button aria-label="Check saved status" loading={operation.busy} disabled={operation.busy} onClick={() => void recover()}>Check saved status</Button>} />}
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : value && <>
      <Typography.Title level={4} style={{ margin: 0 }}>{value.workflowName}</Typography.Title>
      <Flex wrap gap="var(--ac-space-compact)"><Tag color={value.status === 'Completed' ? 'success' : value.status === 'Failed' ? 'error' : undefined}>{workflowStatus(value.status)}</Tag><Tag>Version {value.version}</Tag><Tag>{sourceLabel}</Tag></Flex>
      <Typography.Text strong>{value.provenCount}/{value.requiredCount} applicable required steps proven</Typography.Text>
      {value.status === 'NeedsAttention' && <Alert showIcon type="warning" title={value.reasonCode === 'workflow_unknown_effect' ? 'Unknown effect — inspect before retry' : 'Evidence or recovery needs attention'} description="An unconfirmed result is not a pass. Inspect the original Task, Run and Evaluation before starting replacement work." />}
      {value.reasonCode && <Typography.Paragraph type="secondary">{value.reasonCode.replaceAll('_', ' ')}</Typography.Paragraph>}
      {value.cancellationRequested && !terminalWorkflow(value.status) && <Alert showIcon type="info" title="Cancellation requested" description="The current Task or effect is still being reconciled." />}
      {value.reasonCode === 'evaluation_review_pending' && <Alert showIcon type="info" title="Independent Evaluation needs a human assessment" description="Inspect its Evaluation evidence. This assessment is separate from process sign-off and operation permission." />}
      {value.status === 'WaitingForOperationApproval' && <Alert showIcon type="info" title="A protected operation needs its own approval" description="Workflow sign-off grants no permission for this action." action={context.permissions.includes('approvals.read') ? <Button href={tenantAccessPath(tenant, 'approvals')} onClick={followAppLink}>Open operation approvals</Button> : undefined} />}
      <Descriptions column={1} size="small" items={[{ key: 'created', label: 'Accepted', children: new Date(value.createdAt).toLocaleString() }, { key: 'deadline', label: 'Expires', children: new Date(value.deadlineAt).toLocaleString() }, ...(value.terminalAt ? [{ key: 'terminal', label: 'Finished', children: new Date(value.terminalAt).toLocaleString() }] : [])]} />
      {value.steps.filter(step => step.canDecide).map(step => <DecisionForm key={step.nodeId} step={step} disabled={operation.busy || !!pending} onDirty={() => setDirty(true)} onDecision={(approved, rationale) => decide(step, approved, rationale)} />)}
      <Flex wrap gap="var(--ac-space-compact)">{value.sourceLinks.instanceId && value.sourceLinks.sessionId && <Typography.Link href={`${tenantAgentPath(tenant, value.sourceLinks.instanceId, 'activity-sessions')}?session=${value.sourceLinks.sessionId}${value.sourceLinks.runId ? `&run=${value.sourceLinks.runId}` : ''}`} onClick={followAppLink}>Original source Session{value.sourceLinks.runId ? ' and Run' : ''}</Typography.Link>}{value.sourceLinks.instanceId && value.sourceLinks.automationId && <Typography.Link href={`${tenantAgentPath(tenant, value.sourceLinks.instanceId, 'automations')}?automation=${value.sourceLinks.automationId}`} onClick={followAppLink}>Source Automation</Typography.Link>}{value.successGoal && <Button onClick={event => { sourceOpener.current = event.currentTarget as HTMLButtonElement; setGoal(value.successGoal!); }}>Inspect final Goal verification</Button>}</Flex>
      {publication.loading ? <AccessLoading /> : publication.error ? <AccessError error={publication.error} retry={publication.reload} /> : graph && <Tabs defaultActiveKey="outline" items={[{ key: 'outline', label: 'Steps and evidence', children: <WorkflowOutline graph={graph} selected={selected} onSelect={setSelected} steps={value.steps} /> }, { key: 'diagram', label: 'Diagram', children: <WorkflowDiagram graph={graph} selected={selected} onSelect={setSelected} steps={value.steps} /> }]} />}
      <Typography.Title level={5} style={{ margin: 0 }}>Recorded timeline</Typography.Title>
      <Timeline items={value.steps.filter(step => step.startedAt).sort((a, b) => a.startedAt!.localeCompare(b.startedAt!)).map(step => ({ key: step.nodeId, title: step.name, content: <Flex vertical gap="var(--ac-space-compact)">
        <Typography.Text>{step.status}{step.outcome ? ` · ${step.outcome}` : ''}{step.proven ? ' · Proof accepted' : ''}</Typography.Text><Typography.Text type="secondary">{new Date(step.settledAt ?? step.startedAt!).toLocaleString()}</Typography.Text>
        <Flex wrap gap="var(--ac-space-compact)">{step.taskId && <Button onClick={event => { sourceOpener.current = event.currentTarget as HTMLButtonElement; setTask(step.taskId!); }}>Inspect Task and worker Run</Button>}{step.evaluationAttemptId && <Button onClick={event => { sourceOpener.current = event.currentTarget as HTMLButtonElement; setEvaluation(step.evaluationAttemptId!); }}>Inspect Evaluation</Button>}{step.goalLink && <Button onClick={event => { sourceOpener.current = event.currentTarget as HTMLButtonElement; setGoal(step.goalLink!); }}>Inspect Goal evidence</Button>}</Flex>
      </Flex> }))} />
      <Collapse items={[{ key: 'details', label: 'Execution references', children: <Descriptions column={1} size="small" items={[{ key: 'id', label: 'Execution ID', children: <Typography.Text copyable>{id}</Typography.Text> }, { key: 'hash', label: 'Pinned graph hash', children: <Typography.Text copyable style={{ overflowWrap: 'anywhere' }}>{value.graphHash}</Typography.Text> }, { key: 'catalog', label: 'Definition', children: <Typography.Link href={workflowPath(tenant, value.workflowId)} onClick={followAppLink}>Inspect Workflow definition</Typography.Link> }]} /> }]} />
    </>}
    {task && value && <TaskCenterDrawer tenant={tenant} initialTaskId={task} open narrow={!screens.md} readonly openerRef={sourceOpener} onClose={() => setTask(undefined)} />}
    {evaluation && value && <EvaluationAttemptInspector tenant={tenant} id={evaluation} canReview={context.permissions.includes('evaluations.review')} onClose={() => setEvaluation(undefined)} onSaved={state.reload} />}
    {goal && value && <SessionGoalsDrawer tenant={tenant} session={goal.sessionId} instance="" source={null} initialGoalId={goal.goalId} open narrow={!screens.md} readonly refreshKey={String(value.revision)} openerRef={sourceOpener} onClose={() => setGoal(undefined)} onWorked={() => {}} />}
  </SessionSandboxFrame></>;
}
function DecisionForm({ step, disabled, onDirty, onDecision }: { step: WorkflowStep; disabled: boolean; onDirty: () => void; onDecision: (approved: boolean, rationale?: string) => void }) {
  return <section aria-label="Procedural human sign-off"><Alert showIcon type="info" title={step.name} description={step.prompt} />
    <Typography.Paragraph type="secondary">This records a process decision and grants no operation permission. Expires {step.decisionExpiresAt && new Date(step.decisionExpiresAt).toLocaleString()}.</Typography.Paragraph>
    <Form layout="vertical" disabled={disabled} onValuesChange={onDirty} onFinish={value => onDecision(value.approved === 'approve', value.rationale)}>
      <Form.Item name="approved" label="Process decision" rules={[{ required: true, message: 'Choose approve or reject.' }]}><Select options={[{ value: 'approve', label: 'Approve this process step' }, { value: 'reject', label: 'Reject this process step' }]} /></Form.Item>
      <Form.Item name="rationale" label="Decision rationale (optional)"><Input.TextArea rows={3} maxLength={2000} /></Form.Item><Button htmlType="submit" type="primary">Record sign-off</Button>
    </Form>
  </section>;
}
