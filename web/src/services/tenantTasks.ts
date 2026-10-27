import { IdentityError, identityRequest, tenantApi } from './identity';

export type TaskState = 'PendingDependencies' | 'Queued' | 'Running' | 'Waiting' | 'Blocked' | 'Succeeded' | 'Failed' | 'Cancelled' | 'NeedsAttention';
export type DelegatedTask = { taskId: string; revision: number; state: TaskState; title: string; objective: string;
  sourceSessionId: string | null; selectedTargetInstanceId: string | null; selectedTargetName: string | null; spaceId: string | null;
  spaceName?: string | null; environmentName?: string | null; environmentConfigured?: boolean | null;
  workerSessionId: string | null; initialRunId: string | null; terminalRunId: string | null; result: string | null; reason: string | null;
  cancellationRequested: boolean; createdAt: string; updatedAt: string; deadlineAt: string; reportOnCompletion: boolean;
  canInspect: boolean; canCancel: boolean; canSend: boolean; securityRevision: number; retryOfTaskId: string | null; canRetry: boolean;
  sourceActorKind?: 'Human' | 'Agent'; sourceInstanceId?: string | null; sourceAgentName?: string | null; sourceRunId?: string | null; canAcknowledge?: boolean };
export type TaskMutationReceipt = { task: Pick<DelegatedTask, 'taskId' | 'revision'>; replayed: boolean; securityRevision: number };
export type TaskApprovalReceipt = { approvalRequestId: string; approvalStatus: string; canReadApproval: boolean; admissionState: 'pendingReview' | 'awaitingAdmission' | 'notAdmitted'; reasonCode: string | null; securityRevision: number; replayed: boolean };
export type TaskCreateReceipt = TaskMutationReceipt | TaskApprovalReceipt;
export type TaskMessage = { messageId: string; threadId: string; sequence: number; senderKind: 'Human' | 'Agent'; senderInstanceId: string | null;
  senderDisplayName?: string | null; kind: string; text: string; createdAt: string; deliveryState?: string; deliveryReason?: string | null; wakeSessionId?: string | null; wakeRunId?: string | null };
export type TaskNotice = { notificationId: string; taskId: string; revision: number; createdAt: string; acknowledgedAt: string | null; withheldReason: string | null; reportAdmitted: boolean };
export type TaskPage<T> = { items: T[]; nextCursor: string | null; hasMore: boolean; securityRevision: number; canDelegate?: boolean };
export type TaskTarget = { instanceId: string; name: string; defaultSpaceId: string; spaces: { spaceId: string; name: string; environments?: { environmentId: string; name: string }[] }[]; budget?: { maxSteps: number; durationSeconds: number } | null; model?: { mode: string; label: string } | null };
export type AgentThread = { threadId: string; firstInstanceId: string; secondInstanceId: string; firstName: string; secondName: string; createdAt: string };
export type AgentCooperation = { acceptTasks: boolean; acceptDirectMessages: boolean; allowDirectMessageWake: boolean; maxConcurrentTasks: number };
export const taskStatus: Record<TaskState, string> = { PendingDependencies: 'Waiting for dependencies', Queued: 'Queued', Running: 'Working', Waiting: 'Waiting', Blocked: 'Blocked', Succeeded: 'Succeeded', Failed: 'Failed', Cancelled: 'Cancelled', NeedsAttention: 'Needs attention' };
export const tasksApi = (tenant: string, path: string) => tenantApi(tenant, path);
export const taskApi = (tenant: string, task: string, suffix = '') => tenantApi(tenant, `/tasks/${encodeURIComponent(task)}${suffix}`);
export const privateTaskDenial = (reason: unknown) => reason instanceof IdentityError && [401, 403, 404, 410].includes(reason.status);
export const readTask = (tenant: string, task: string, signal?: AbortSignal) => identityRequest<DelegatedTask>(taskApi(tenant, task), { signal });
export const readTaskMessages = (tenant: string, task: string, signal?: AbortSignal, cursor?: string) => identityRequest<TaskPage<TaskMessage>>(taskApi(tenant, task, `/messages${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`), { signal });
export const readTaskNotices = (tenant: string, task: string, signal?: AbortSignal) => identityRequest<TaskPage<TaskNotice>>(taskApi(tenant, task, '/notifications'), { signal });
export async function delegateTask(tenant: string, session: string, body: Record<string, unknown>, signal: AbortSignal) {
  const root = tenantApi(tenant, `/sessions/${encodeURIComponent(session)}/tasks`);
  try { return await identityRequest<TaskCreateReceipt>(root, { method: 'POST', body, signal }); }
  catch (reason) {
    if (signal.aborted || reason instanceof IdentityError && reason.status < 500) throw reason;
    const saved = await identityRequest<TaskCreateReceipt | null>(`${root}/recovery?requestKey=${encodeURIComponent(String(body.requestKey))}`, { signal });
    if (saved) return saved;
    throw new Error('The assignment is unconfirmed. Read saved Tasks before making another assignment.');
  }
}
