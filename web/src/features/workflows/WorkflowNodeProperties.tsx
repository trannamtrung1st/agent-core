import { useState } from 'react';
import { Alert, Button, Flex, Form, Input, InputNumber, Select, Typography } from 'antd';
import { type Member, type Page, type TenantContext, tenantApi } from '../../services/identity';
import { goalsApi, type GoalsView, type SessionGoal } from '../../services/tenantSessionGoals';
import type { ContextOptions } from '../../services/tenantSessionContext';
import { useEvaluationCatalog } from '../access/useEvaluationCatalog';
import type { EvaluationSuite } from '../../services/tenantEvaluations';
import { AccessError, AccessLoading } from '../access/AccessFeedback';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { connectWorkflow, nodeKinds, outcomesFor, requirementLabels, type WorkflowGraph, type WorkflowGoal, type WorkflowNode } from '../../services/tenantWorkflows';

export type WorkflowAgent = { principalId: string; instance: { instanceId: string; lifecycle: string; persona: { name: string; role: string }; cooperation?: { acceptTasks: boolean } | null } };
const selectStyle = { width: '100%' };
export const emptyGuid = '00000000-0000-0000-0000-000000000000';
export function WorkflowNodeProperties({ context, graph, node, agents, disabled, onChange, onRemove }: {
  context: TenantContext; graph: WorkflowGraph; node: WorkflowNode; agents: WorkflowAgent[]; disabled: boolean;
  onChange: (graph: WorkflowGraph) => void; onRemove: () => void;
}) {
  const tenant = context.tenant.tenantId;
  const [memberPage, setMemberPage] = useState(0);
  const members = useIdentityQuery<Page<Member>>(node.kind === 3 && context.permissions.includes('members.read') ? tenantApi(tenant, `/members?page=${memberPage}&pageSize=100`) : null);
  const patch = (value: Partial<WorkflowNode>) => onChange({ ...graph, nodes: graph.nodes.map(n => n.id === node.id ? { ...n, ...value } : n) });
  const task = node.task; const decision = node.decision; const branch = node.branch; const verification = node.verification;
  return <section className="workflow-properties" aria-label="Selected step properties">
    <Flex justify="space-between" align="center" gap="var(--ac-space-compact)"><Typography.Title level={5} style={{ margin: 0 }}>{nodeKinds[node.kind]}</Typography.Title><Typography.Text type="secondary">{node.id}</Typography.Text></Flex>
    <Form layout="vertical" disabled={disabled} component="div">
      <Form.Item label="Step name" htmlFor="workflow-step-name"><Input id="workflow-step-name" maxLength={80} value={node.name} onChange={e => patch({ name: e.target.value })} /></Form.Item>
      {node.kind > 0 && node.kind < 5 && <Form.Item label="Evidence requirement" htmlFor="workflow-requirement"><Select id="workflow-requirement" style={selectStyle} value={node.requirement} options={requirementLabels.map((label, value) => ({ label, value }))} onChange={requirement => patch({ requirement, requiredOnEdgeId: null })} /></Form.Item>}
      {node.requirement === 2 && <Form.Item label="Required when this branch is selected" htmlFor="workflow-required-branch"><Select id="workflow-required-branch" style={selectStyle} value={node.requiredOnEdgeId ?? undefined} placeholder="Select a branch edge" options={graph.edges.filter(e => graph.nodes.find(n => n.id === e.from)?.kind === 4).map(e => ({ value: e.id, label: `${graph.nodes.find(n => n.id === e.from)?.name}: ${e.outcome}` }))} onChange={requiredOnEdgeId => patch({ requiredOnEdgeId })} /></Form.Item>}
      {task && <>
        <Form.Item label="Target agent" htmlFor="workflow-task-agent"><Select id="workflow-task-agent" labelRender={option => agents.find(a => a.instance.instanceId === option.value)?.instance.persona.name ?? 'Saved target (not in loaded choices)'} style={selectStyle} showSearch optionFilterProp="label" value={task.targetInstanceId === emptyGuid ? undefined : task.targetInstanceId} placeholder="Choose an agent accepting Tasks" options={agents.filter(a => a.instance.cooperation?.acceptTasks).map(a => ({ value: a.instance.instanceId, label: a.instance.persona.name }))} onChange={targetInstanceId => patch({ task: { ...task, targetInstanceId, spaceId: emptyGuid, environmentId: null } })} /></Form.Item>
        <Form.Item label="Task objective" htmlFor="workflow-task-objective" extra="Inputs are appended as untrusted data. They cannot choose agents, permissions or the next step."><Input.TextArea id="workflow-task-objective" rows={4} maxLength={4000} showCount value={task.objective} onChange={e => patch({ task: { ...task, objective: e.target.value } })} /></Form.Item>
        {task.targetInstanceId !== emptyGuid && <TaskContext key={task.targetInstanceId} tenant={tenant} task={task} onChange={next => patch({ task: next })} />}
      </>}
      {decision && <>
        {members.error && <AccessError error={members.error} retry={members.reload} />}
        <Form.Item label="Assigned human reviewer" htmlFor="workflow-reviewer"><Select id="workflow-reviewer" labelRender={option => members.data?.items.find(m => m.principalId === option.value)?.displayName ?? (option.value === context.principalId ? 'You' : 'Assigned reviewer')} style={selectStyle} value={decision.reviewerPrincipalId} showSearch optionFilterProp="label" options={members.data?.items.filter(m => m.status === 0).map(m => ({ value: m.principalId, label: m.principalId === context.principalId ? `${m.displayName} (you)` : m.displayName })) ?? [{ value: context.principalId, label: 'You' }]} onChange={reviewerPrincipalId => patch({ decision: { ...decision, reviewerPrincipalId } })} /></Form.Item>
        <Flex wrap gap="var(--ac-space-compact)">{memberPage > 0 && <Button onClick={() => setMemberPage(p => p - 1)}>Previous reviewers</Button>}{members.data && (memberPage + 1) * 100 < members.data.total && <Button onClick={() => setMemberPage(p => p + 1)}>More reviewers</Button>}</Flex>
        <Form.Item label="Sign-off question" htmlFor="workflow-signoff-question"><Input.TextArea id="workflow-signoff-question" rows={3} maxLength={1000} value={decision.prompt} onChange={e => patch({ decision: { ...decision, prompt: e.target.value } })} /></Form.Item>
        <Form.Item label="Decision expires after (hours)" htmlFor="workflow-signoff-hours"><InputNumber id="workflow-signoff-hours" min={1} max={720} value={decision.timeoutHours} onChange={value => patch({ decision: { ...decision, timeoutHours: value ?? 72 } })} /></Form.Item>
        <Alert showIcon type="info" title="Procedural sign-off" description="This records a process decision. Protected agent actions still require their own IAM approval." />
      </>}
      {verification && <>
        <Form.Item label="Verification source" htmlFor="workflow-verification-source"><Select id="workflow-verification-source" style={selectStyle} value={verification.kind} options={[{ value: 0, label: 'Exact Evaluation of an earlier Task' }, { value: 1, label: 'Verified Session Goal checkpoint' }]} onChange={kind => patch({ verification: { kind } })} /></Form.Item>
        {verification.kind === 0 ? <EvaluationBinding tenant={tenant} graph={graph} node={node} onChange={value => patch({ verification: value })} />
          : <GoalBinding tenant={tenant} value={verification.sessionId && verification.goalId && verification.planRevision ? { sessionId: verification.sessionId, goalId: verification.goalId, planRevision: verification.planRevision } : undefined} checkpoint={verification.checkpointId ?? ''}
              onChange={(value, checkpointId) => patch({ verification: { kind: 1, ...value, checkpointId } })} />}
      </>}
      {branch && <>
        <Form.Item label="Recorded fact" htmlFor="workflow-branch-fact"><Select id="workflow-branch-fact" style={selectStyle} value={branch.fact} options={[{ value: 0, label: 'Verification passed' }, { value: 1, label: 'Human sign-off approved' }, { value: 2, label: 'Task succeeded' }]} onChange={fact => patch({ branch: { fact, sourceNodeId: '' } })} /></Form.Item>
        <Form.Item label="Earlier evidence step" htmlFor="workflow-branch-source"><Select id="workflow-branch-source" style={selectStyle} value={branch.sourceNodeId || undefined} placeholder="Select saved evidence" options={graph.nodes.filter(n => n.kind === (branch.fact === 0 ? 2 : branch.fact === 1 ? 3 : 1)).map(n => ({ value: n.id, label: n.name }))} onChange={sourceNodeId => patch({ branch: { ...branch, sourceNodeId } })} /></Form.Item>
      </>}
      {node.kind === 5 && <Typography.Paragraph type="secondary">Success requires every applicable required step and independent Task verification. A bound Goal must also be completed by its own verification owner.</Typography.Paragraph>}
      {node.kind === 6 && <Typography.Paragraph type="secondary">This route ends in failure and preserves its recorded evidence.</Typography.Paragraph>}
      {outcomesFor(node.kind).map(outcome => <Form.Item key={outcome} label={`After ${outcome}`} htmlFor={`workflow-edge-${outcome}`}><Select id={`workflow-edge-${outcome}`} style={selectStyle} allowClear placeholder="Choose the next step" value={graph.edges.find(e => e.from === node.id && e.outcome === outcome)?.to} options={graph.nodes.filter(n => n.id !== node.id && n.kind !== 0).map(n => ({ value: n.id, label: n.name }))} onChange={to => onChange(connectWorkflow(graph, node.id, outcome, to))} /></Form.Item>)}
      {node.kind !== 0 && <Button danger onClick={onRemove}>Remove step</Button>}
    </Form>
  </section>;
}
function TaskContext({ tenant, task, onChange }: { tenant: string; task: NonNullable<WorkflowNode['task']>; onChange: (value: NonNullable<WorkflowNode['task']>) => void }) {
  const [page, setPage] = useState(0);
  const state = useIdentityQuery<ContextOptions>(tenantApi(tenant, `/agents/${task.targetInstanceId}/context-options?page=${page}${task.spaceId !== emptyGuid ? `&spaceId=${task.spaceId}` : ''}`));
  return <>{state.loading && <AccessLoading />}{state.error && <AccessError error={state.error} retry={state.reload} />}
    <Form.Item label="Worker Space" htmlFor="workflow-worker-space"><Select id="workflow-worker-space" labelRender={option => state.data?.spaces.find(s => s.spaceId === option.value)?.name ?? 'Saved Space (not on this page)'} style={selectStyle} value={task.spaceId === emptyGuid ? undefined : task.spaceId} placeholder="Choose an authorized membership" options={state.data?.spaces.map(s => ({ value: s.spaceId, label: s.name }))} onChange={spaceId => onChange({ ...task, spaceId, environmentId: null })} /></Form.Item>
    <Flex gap="var(--ac-space-compact)">{page > 0 && <Button size="small" onClick={() => setPage(p => p - 1)}>Previous Spaces</Button>}{state.data?.hasMore && <Button size="small" onClick={() => setPage(p => p + 1)}>More Spaces</Button>}</Flex>
    {state.data?.environmentsEnabled && <Form.Item label="Worker Environment (optional)" htmlFor="workflow-worker-environment"><Select id="workflow-worker-environment" labelRender={option => state.data?.environments.find(e => e.environmentId === option.value)?.name ?? 'Saved Environment (unavailable)'} style={selectStyle} allowClear value={task.environmentId ?? undefined} disabled={!state.data.environmentSelectionAllowed} options={state.data.environments.map(e => ({ value: e.environmentId, label: e.name }))} onChange={environmentId => onChange({ ...task, environmentId: environmentId ?? null })} /></Form.Item>}
  </>;
}
function EvaluationBinding({ tenant, graph, node, onChange }: { tenant: string; graph: WorkflowGraph; node: WorkflowNode; onChange: (value: NonNullable<WorkflowNode['verification']>) => void }) {
  const suites = useEvaluationCatalog<EvaluationSuite>(tenant, 'evaluation-suites'); const verification = node.verification!;
  return <>{suites.error && <AccessError error={suites.error} retry={suites.reload} />}
    <Form.Item label="Task to verify" htmlFor="workflow-verified-task"><Select id="workflow-verified-task" style={selectStyle} placeholder="Choose an earlier Task" value={verification.taskNodeId ?? undefined} options={graph.nodes.filter(n => n.kind === 1).map(n => ({ value: n.id, label: n.name }))} onChange={taskNodeId => onChange({ ...verification, taskNodeId })} /></Form.Item>
    <Form.Item label="Immutable Evaluation Suite" htmlFor="workflow-suite"><Select id="workflow-suite" labelRender={option => `${suites.data?.items.find(s => s.suiteId === option.value)?.name ?? 'Saved Suite'} · pinned v${verification.suiteVersion}`} style={selectStyle} placeholder="Choose a current Suite version" loading={suites.loading} value={verification.suiteId ?? undefined} options={suites.data?.items.filter(s => !s.archived).map(s => ({ value: s.suiteId, label: `${s.name} · v${s.version}` }))} onChange={id => { const suite = suites.data?.items.find(s => s.suiteId === id); if (suite) onChange({ ...verification, suiteId: id, suiteVersion: suite.version, suiteHash: suite.contentHash }); }} /></Form.Item>
    {verification.suiteVersion && <Typography.Paragraph type="secondary">Pinned Suite version {verification.suiteVersion}. Inconclusive results cannot advance this gate.</Typography.Paragraph>}
  </>;
}
export function GoalBinding({ tenant, value, checkpoint, onChange }: { tenant: string; value?: WorkflowGoal; checkpoint?: string; onChange: (value: WorkflowGoal | undefined, checkpoint?: string) => void }) {
  const [page, setPage] = useState(0); const [goalPage, setGoalPage] = useState(0);
  const sessions = useIdentityQuery<Page<{ sessionId: string; title: string; agentName: string }>>(tenantApi(tenant, `/sessions?page=${page}&pageSize=50`));
  const goals = useIdentityQuery<GoalsView>(value?.sessionId ? goalsApi(tenant, value.sessionId, `?page=${goalPage}&pageSize=50`) : null);
  const exact = useIdentityQuery<SessionGoal>(value?.sessionId && value.goalId !== emptyGuid ? goalsApi(tenant, value.sessionId, `/${value.goalId}`) : null);
  const selected = exact.data ?? goals.data?.goals.items.find(g => g.goalId === value?.goalId);
  return <>
    {sessions.error && <AccessError error={sessions.error} retry={sessions.reload} />}{goals.error && <AccessError error={goals.error} retry={goals.reload} />}
    <Form.Item label="Goal Session" htmlFor="workflow-goal-session"><Select id="workflow-goal-session" labelRender={option => sessions.data?.items.find(s => s.sessionId === option.value)?.title ?? 'Saved Session (not on this page)'} style={selectStyle} allowClear placeholder="Choose a readable Session" value={value?.sessionId} options={sessions.data?.items.map(s => ({ value: s.sessionId, label: `${s.title} · ${s.agentName}` }))} onChange={sessionId => onChange(sessionId ? { sessionId, goalId: emptyGuid, planRevision: 1 } : undefined)} /></Form.Item>
    <Flex gap="var(--ac-space-compact)">{page > 0 && <Button size="small" onClick={() => setPage(p => p - 1)}>Previous Sessions</Button>}{sessions.data && (page + 1) * 50 < sessions.data.total && <Button size="small" onClick={() => setPage(p => p + 1)}>More Sessions</Button>}</Flex>
    {value?.sessionId && <Form.Item label="Goal and plan" htmlFor="workflow-goal-plan"><Select id="workflow-goal-plan" style={selectStyle} placeholder="Choose the exact current plan" value={selected?.goalId} options={goals.data?.goals.items.map(g => ({ value: g.goalId, label: `${g.plan.title} · plan ${g.planRevision}` }))} onChange={goalId => { const goal = goals.data?.goals.items.find(g => g.goalId === goalId); if (goal) onChange({ sessionId: value.sessionId, goalId, planRevision: goal.planRevision }); }} /></Form.Item>}
    {exact.error && <AccessError error={exact.error} retry={exact.reload} />}
    <Flex wrap gap="var(--ac-space-compact)">{goalPage > 0 && <Button onClick={() => setGoalPage(p => p - 1)}>Previous Goals</Button>}{goals.data && (goalPage + 1) * 50 < goals.data.goals.total && <Button onClick={() => setGoalPage(p => p + 1)}>More Goals</Button>}</Flex>
    {value && value.goalId !== emptyGuid && <Typography.Paragraph type="secondary">Bound plan revision {value.planRevision}.</Typography.Paragraph>}
    {selected && value && selected.planRevision !== value.planRevision && <Alert type="warning" showIcon title="The Goal plan changed" description="The saved process still binds its original plan revision. Review and deliberately choose a current binding in a new draft." />}
    {checkpoint !== undefined && selected && <Form.Item label="Verified checkpoint" htmlFor="workflow-goal-checkpoint"><Select id="workflow-goal-checkpoint" labelRender={option => selected?.plan.checkpoints.find(c => c.checkpointId === option.value)?.title ?? 'Saved checkpoint (not in the current plan)'} style={selectStyle} value={checkpoint || undefined} placeholder="Choose a checkpoint" options={selected.plan.checkpoints.map(c => ({ value: c.checkpointId, label: c.title }))} onChange={id => onChange(value, id)} /></Form.Item>}
  </>;
}
