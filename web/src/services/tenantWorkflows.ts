import { identityRequest, tenantApi, type Page } from './identity';
import { freshTenantRevision } from './tenantEvaluations';

export const nodeKinds = ['Start', 'Agent task', 'Verify', 'Human sign-off', 'Branch', 'Success', 'Failure'] as const;
export const requirementLabels = ['Optional', 'Required on every success route', 'Required on a selected branch'] as const;
export type WorkflowInput = { key: string; label: string; kind: number; required: boolean; maxLength: number; choices?: string[] | null };
export type WorkflowGoal = { sessionId: string; goalId: string; planRevision: number };
export type WorkflowNode = {
  id: string; name: string; kind: number; requirement: number; requiredOnEdgeId?: string | null;
  task?: { targetInstanceId: string; objective: string; spaceId: string; environmentId?: string | null } | null;
  verification?: { kind: number; taskNodeId?: string | null; suiteId?: string | null; suiteVersion?: number | null; suiteHash?: string | null;
    sessionId?: string | null; goalId?: string | null; checkpointId?: string | null; planRevision?: number | null } | null;
  decision?: { reviewerPrincipalId: string; prompt: string; timeoutHours: number } | null;
  branch?: { sourceNodeId: string; fact: number } | null;
};
export type WorkflowEdge = { id: string; from: string; to: string; outcome: string };
export type WorkflowGraph = { schemaVersion: 1; nodes: WorkflowNode[]; edges: WorkflowEdge[]; inputs: WorkflowInput[]; allowedOrigins: number[];
  coordinatorInstanceIds: string[]; maxTotalTaskSteps: number; maxTotalTaskSeconds: number; successGoal?: WorkflowGoal | null };
export type WorkflowDefinition = { tenantId: string; workflowId: string; name: string; description: string; draftJson: string; revision: number;
  publishedVersion: number; retired: boolean; createdAt: string; updatedAt: string };
export type WorkflowVersion = { tenantId: string; workflowId: string; version: number; graph: { canonicalJson: string; hash: string }; publishedAt: string };
export type WorkflowControls = { definition: WorkflowDefinition | null; securityRevision: number; canCreate: boolean; canUpdate: boolean; canPublish: boolean; canStart: boolean };
export type WorkflowReceipt = { definition: WorkflowDefinition; version?: WorkflowVersion | null; securityRevision: number; requestKey: string };
export type WorkflowDiagnostic = { code: string; message: string; nodeId?: string | null; edgeId?: string | null };
export type WorkflowStep = { nodeId: string; name: string; kind: string; requirement: string; status: string; outcome: string | null; proven: boolean;
  applicable: boolean; revision: number; taskId: string | null; workerRunId: string | null; evaluationAttemptId: string | null; prompt: string | null;
  reviewerPrincipalId: string | null; canDecide: boolean; decisionExpiresAt: string | null; startedAt: string | null; settledAt: string | null; goalLink: WorkflowGoal | null };
export type WorkflowExecution = { executionId: string; workflowId: string; version: number; graphHash: string; status: string; sourceKind: string;
  reasonCode: string | null; revision: number; securityRevision: number; createdAt: string; deadlineAt: string; terminalAt: string | null;
  cancellationRequested: boolean; canCancel: boolean; requiredCount: number; provenCount: number; steps: WorkflowStep[]; sourceLinks: { sessionId: string | null; runId: string | null; automationId: string | null; instanceId: string | null }; successGoal: WorkflowGoal | null; workflowName: string };
export type WorkflowStartReceipt = Pick<WorkflowExecution, 'executionId' | 'workflowId' | 'version' | 'graphHash' | 'status' | 'revision'>;
export const workflowApi = (tenant: string, suffix = '') => tenantApi(tenant, '/workflows' + suffix);
export const executionApi = (tenant: string, suffix = '') => tenantApi(tenant, '/workflow-executions' + suffix);
export const workflowPath = (tenant: string, workflow?: string, execution?: string) => { const params = new URLSearchParams(); if (workflow) params.set('workflow', workflow); if (execution) params.set('execution', execution); return `/t/${encodeURIComponent(tenant)}/admin/workflows${params.size ? `?${params}` : ''}`; };
export const terminalWorkflow = (status: string) => ['Completed', 'Failed', 'Cancelled', 'Expired'].includes(status);
export const workflowStatus = (status: string) => ({ PendingAdmission: 'Request accepted', Running: 'Running', WaitingForAgent: 'Waiting for agent',
  WaitingForVerification: 'Waiting for verification', WaitingForHuman: 'Waiting for human sign-off', WaitingForOperationApproval: 'Waiting for operation approval',
  NeedsAttention: 'Needs attention', Blocked: 'Blocked', Completed: 'Completed', Failed: 'Failed', Cancelled: 'Cancelled', Expired: 'Expired' }[status] ?? status);
export function readWorkflowGraph(json: string): WorkflowGraph {
  const graph = JSON.parse(json) as WorkflowGraph;
  if (graph.schemaVersion !== 1 || !Array.isArray(graph.nodes) || !Array.isArray(graph.edges) || graph.nodes.length > 40 || graph.edges.length > 80)
    throw new Error('Unsupported saved Workflow graph.');
  return graph;
}
export const outcomesFor = (kind: number) => kind === 0 ? ['next'] : kind === 1 ? ['succeeded', 'failed'] : kind === 2 ? ['passed', 'failed']
  : kind === 3 ? ['approve', 'reject'] : kind === 4 ? ['true', 'false'] : [];
export function initialWorkflow(reviewer: string): WorkflowGraph {
  return { schemaVersion: 1, nodes: [{ id: 'start', name: 'Start', kind: 0, requirement: 0 },
    { id: 'review', name: 'Process sign-off', kind: 3, requirement: 1, decision: { reviewerPrincipalId: reviewer, prompt: 'Accept the recorded process outcome?', timeoutHours: 72 } },
    { id: 'done', name: 'Complete', kind: 5, requirement: 0 }, { id: 'failed', name: 'Rejected', kind: 6, requirement: 0 }],
    edges: [{ id: 'start-review', from: 'start', to: 'review', outcome: 'next' }, { id: 'review-approve', from: 'review', to: 'done', outcome: 'approve' },
      { id: 'review-reject', from: 'review', to: 'failed', outcome: 'reject' }], inputs: [], allowedOrigins: [0], coordinatorInstanceIds: [], maxTotalTaskSteps: 24, maxTotalTaskSeconds: 180 };
}
/** Deterministic outline order, including detached draft steps, without evaluating any user expression. */
export function workflowOrder(graph: WorkflowGraph): WorkflowNode[] {
  const pending = new Map(graph.nodes.map(node => [node.id, node])); const result: WorkflowNode[] = [];
  while (pending.size) {
    const next = [...pending.values()].find(node => !graph.edges.some(edge => edge.to === node.id && pending.has(edge.from)));
    if (!next) { result.push(...pending.values()); break; }
    result.push(next); pending.delete(next.id);
  }
  return result;
}
export function connectWorkflow(graph: WorkflowGraph, from: string, outcome: string, to?: string): WorkflowGraph {
  const existing = graph.edges.find(edge => edge.from === from && edge.outcome === outcome);
  const edges = graph.edges.filter(edge => edge.from !== from || edge.outcome !== outcome);
  if (to) edges.push({ id: existing?.id ?? `${from}-${outcome}-${crypto.randomUUID().slice(0, 8)}`, from, outcome, to });
  return { ...graph, edges };
}
export async function startWorkflow(tenant: string, workflow: string, version: number, inputs: Record<string, unknown>, requestKey: string,
  signal: AbortSignal, sourceSessionId?: string, coordinatorInstanceId?: string, securityRevision?: number) {
  return identityRequest<WorkflowStartReceipt>(workflowApi(tenant, `/${workflow}/executions`), { method: 'POST', signal, body: {
    version, inputs, requestKey, sourceSessionId, coordinatorInstanceId, expectedSecurityRevision: securityRevision ?? await freshTenantRevision(tenant, signal) } });
}
export const workflowCatalog = (tenant: string, page = 0, signal?: AbortSignal) => identityRequest<Page<WorkflowDefinition>>(workflowApi(tenant, `/?page=${page}&pageSize=20`), { signal });
