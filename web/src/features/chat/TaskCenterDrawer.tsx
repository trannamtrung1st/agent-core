import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import { Alert, App, Button, Checkbox, Empty, Flex, Form, Input, InputNumber, Select, Skeleton, Tabs, Tag, Typography, theme } from 'antd';
import { ArrowLeftOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons';
import { IdentityError, identityRequest, tenantApi } from '../../services/identity';
import { delegateTask, privateTaskDenial, readTask, readTaskMessages, readTaskNotices, taskApi, taskStatus,
  type TaskCreateReceipt, type TaskApprovalReceipt, type DelegatedTask, type TaskMessage, type TaskNotice, type TaskPage, type TaskTarget } from '../../services/tenantTasks';
import { workflowPath } from '../../services/tenantWorkflows';
import { confirmAction } from '../../app/confirmAction';
import { followAppLink } from '../../app/appLink';
import { tenantApprovalPath, tenantAgentPath } from '../access/accessRoute';
import { useAccessDraftGuard } from '../access/useAccessDraftGuard';
import { DrawerListFooter } from './DrawerListFooter';
import { formatChatTime } from './chatTime';
import { SessionSandboxFrame } from './SessionSandboxFrame';

type Draft = { title?: string; objective: string; target: string; spaceId: string; environmentId?: string; dependsOnTaskIds?: string[]; hours: number; reportOnCompletion?: boolean };
const reportWithheldText = (reason: string) => ({
  task_report_no_worker_run: 'No worker started; the outcome remains in this inbox.',
  task_report_parent_unavailable: 'The source Session cannot receive a report. Inspect the outcome here.',
  task_report_source_unavailable: 'The original outcome is unavailable under current access.',
  task_report_review_required: 'A source report requires current authorization. Inspect the task outcome here.',
  task_report_model_unavailable: "The source Agent's model is unavailable. Inspect the saved task outcome.",
  task_report_budget_unavailable: 'The reserved report budget is unavailable. Inspect the task outcome here.',
  task_report_deadline_exceeded: 'The reporting deadline passed. Inspect the saved task outcome here.',
} as Record<string, string>)[reason] ?? 'A source report could not be admitted under current access or policy.';
type TaskCenterProps = {
  tenant: string; session?: string; instance?: string; open: boolean; narrow: boolean; readonly?: boolean;
  openerRef?: RefObject<HTMLButtonElement | null>; initialTaskId?: string; onClose: () => void;
};
// Retire the complete private subtree synchronously when its identity changes.
export function TaskCenterDrawer(props: TaskCenterProps) {
  return <TaskCenterScope key={JSON.stringify([props.tenant, props.session, props.instance, props.open, props.initialTaskId])} {...props} />;
}
function TaskCenterScope({ tenant, session, instance, open, narrow, readonly = false, openerRef, initialTaskId, onClose }: TaskCenterProps) {
  const { token } = theme.useToken(); const { modal } = App.useApp(); const [form] = Form.useForm<Draft>();
  const [scope, setScope] = useState(session ? 'created' : 'assigned');
  const [page, setPage] = useState<TaskPage<DelegatedTask> | null>(null); const [selected, setSelected] = useState<DelegatedTask | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(initialTaskId ?? null); const selection = useRef(selectedId); selection.current = selectedId;
  const [messages, setMessages] = useState<TaskPage<TaskMessage> | null>(null); const [notices, setNotices] = useState<TaskNotice[]>([]);
  const [targets, setTargets] = useState<TaskTarget[]>([]); const [targetsPage, setTargetsPage] = useState(0); const [moreTargets, setMoreTargets] = useState(false);
  const [approvalReceipt, setApprovalReceipt] = useState<TaskApprovalReceipt | null>(null);
  const [retryOf, setRetryOf] = useState<string | null>(null);
  const [creating, setCreating] = useState(false); const [dirty, setDirty] = useState(false); const [reply, setReply] = useState('');
  const [loading, setLoading] = useState(false); const [moreLoading, setMoreLoading] = useState(false); const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null); const [uncertain, setUncertain] = useState(false);
  const epoch = useRef(0); const readSequence = useRef(0); const pending = useRef(false); const abort = useRef(new AbortController());
  const createCommand = useRef<Record<string, unknown> | null>(null); const replyCommand = useRef<{ text: string; requestKey: string } | null>(null);
  const uncertainAction = useRef<{ kind: 'create' | 'message' | 'cancel' | 'acknowledge'; key: string; task?: string; notification?: string } | null>(null);
  const loadedPages = useRef(1); const listing = useRef(false);
  const loadedMessages = useRef<{ task: string; page: TaskPage<TaskMessage> } | null>(null);
  const back = useRef<HTMLButtonElement>(null); const rowButtons = useRef(new Map<string, HTMLButtonElement | HTMLAnchorElement>());
  const guard = useAccessDraftGuard(dirty || !!reply, busy, onClose);
  const targetId = Form.useWatch('target', form); const target = targets.find(t => t.instanceId === targetId);
  const spaceId = Form.useWatch('spaceId', form); const selectedSpace = target?.spaces.find(space => space.spaceId === spaceId);
  const listPath = session ? `/sessions/${encodeURIComponent(session)}/tasks` : `/agents/${encodeURIComponent(instance ?? '')}/tasks`;
  const retire = useCallback(() => {
    loadedPages.current = 1; listing.current = false; epoch.current++; readSequence.current++; abort.current.abort(); abort.current = new AbortController(); pending.current = false;
    setApprovalReceipt(null); setPage(null); setSelected(null); setSelectedId(null); setMessages(null); setNotices([]); setTargets([]); setCreating(false); setDirty(false);
    setRetryOf(null); setReply(''); setBusy(false); setLoading(false); setMoreLoading(false); setUncertain(false); createCommand.current = null; replyCommand.current = null; uncertainAction.current = null; loadedMessages.current = null; form.resetFields();
  }, [form]);
  const fail = useCallback((reason: unknown, reading = false) => {
    if (privateTaskDenial(reason) || reading && reason instanceof IdentityError && reason.status === 409) retire();
    setError(reason instanceof Error ? reason.message : 'Tasks are unavailable. Read saved facts to recover.');
  }, [retire]);
  const refresh = useCallback(async (explicit = false) => {
    if (pending.current || listing.current) return;
    listing.current = true;
    const generation = epoch.current; const sequence = ++readSequence.current; const id = selection.current;
    if (explicit) setLoading(true);
    try {
      if (id) {
        const previous = loadedMessages.current?.task === id ? loadedMessages.current.page : null;
        const [detail, correspondence, notifications] = await Promise.all([readTask(tenant, id, abort.current.signal), readTaskMessages(tenant, id, abort.current.signal, previous?.items.at(-1)?.messageId), readTaskNotices(tenant, id, abort.current.signal)]);
        if (generation !== epoch.current || sequence !== readSequence.current || id !== selection.current) return;
        const merged = { ...correspondence, items: [...new Map([...(previous?.items ?? []), ...correspondence.items].map(message => [message.messageId, message])).values()].slice(-500) };
        loadedMessages.current = { task: id, page: merged }; setSelected(detail); setMessages(merged); setNotices(notifications.items);
      } else {
        if (!session && !instance) { setPage(null); return; }
        // Revalidate every visible page. Keeping cached rows would retain private
        // content after a scoped permission change; refreshing only page one loses rows.
        let cursor: string | null = null; let next: TaskPage<DelegatedTask> | null = null;
        const items: DelegatedTask[] = [];
        for (let index = 0; index < loadedPages.current; index++) {
          next = await identityRequest<TaskPage<DelegatedTask>>(tenantApi(tenant, `${listPath}?scope=${scope}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`), { signal: abort.current.signal });
          if (generation !== epoch.current || sequence !== readSequence.current) return;
          items.push(...next.items); cursor = next.nextCursor;
          if (!cursor) { loadedPages.current = index + 1; break; }
        }
        if (creating && session) {
          const choices: TaskTarget[] = [];
          for (let index = 0; index <= targetsPage; index++) {
            const targetPage = await identityRequest<{ items: TaskTarget[]; hasMore: boolean }>(tenantApi(tenant, `/sessions/${encodeURIComponent(session)}/task-targets?page=${index}`), { signal: abort.current.signal });
            if (generation !== epoch.current || sequence !== readSequence.current) return;
            choices.push(...targetPage.items); setMoreTargets(targetPage.hasMore);
            if (!targetPage.hasMore) { setTargetsPage(index); break; }
          }
          if (retryOf) await readTask(tenant, retryOf, abort.current.signal);
          if (generation !== epoch.current || sequence !== readSequence.current) return;
          setTargets(choices);
          const selectedTarget = form.getFieldValue('target') as string | undefined;
          const candidate = choices.find(choice => choice.instanceId === selectedTarget);
          if (selectedTarget && !candidate) { form.setFieldsValue({ target: undefined, spaceId: undefined, environmentId: undefined }); createCommand.current = null; }
          else if (candidate && !candidate.spaces.some(space => space.spaceId === form.getFieldValue('spaceId'))) {
            form.setFieldsValue({ spaceId: candidate.defaultSpaceId, environmentId: undefined }); createCommand.current = null;
          }
          else if (candidate && form.getFieldValue('environmentId') && !candidate.spaces.find(space => space.spaceId === form.getFieldValue('spaceId'))?.environments?.some(environment => environment.environmentId === form.getFieldValue('environmentId'))) {
            form.setFieldValue('environmentId', undefined); createCommand.current = null;
          }
        }
        if (next) setPage({ ...next, items: [...new Map(items.map(task => [task.taskId, task])).values()] });
      }
      if (explicit) setError(null);
    } catch (reason) { if (generation === epoch.current && sequence === readSequence.current && !abort.current.signal.aborted) fail(reason, true); }
    finally { if (generation === epoch.current && sequence === readSequence.current) { listing.current = false; setLoading(false); } }
  }, [tenant, listPath, scope, fail, creating, session, instance, targetsPage, retryOf, form]);
  useEffect(() => {
    retire(); setError(null); setScope(session ? 'created' : 'assigned'); setSelectedId(initialTaskId ?? null); selection.current = initialTaskId ?? null;
    return () => { epoch.current++; abort.current.abort(); };
  }, [tenant, session, instance, open, initialTaskId, retire]);
  useEffect(() => { if (open) void refresh(true); }, [open, refresh, selectedId]);
  useEffect(() => { if (!open) return; const timer = window.setInterval(() => void refresh(), 3000); return () => clearInterval(timer); }, [open, refresh]);
  useEffect(() => {
    if (!open) return;
    const reload = (event: Event) => { if ((event as CustomEvent<{ tenant: string }>).detail?.tenant === tenant) void refresh(); };
    window.addEventListener('agentcore:coordination-changed', reload);
    return () => window.removeEventListener('agentcore:coordination-changed', reload);
  }, [open, tenant, refresh]);
  async function loadMore() {
    if (!page?.nextCursor || moreLoading || pending.current || listing.current) return;
    listing.current = true; const generation = epoch.current; const sequence = readSequence.current; setMoreLoading(true);
    try {
      const next = await identityRequest<TaskPage<DelegatedTask>>(tenantApi(tenant, `${listPath}?scope=${scope}&cursor=${encodeURIComponent(page.nextCursor)}`), { signal: abort.current.signal });
      if (generation === epoch.current && sequence === readSequence.current) { loadedPages.current++; setPage(previous => ({ ...next, items: [...new Map([...(previous?.items ?? []), ...next.items].map(t => [t.taskId, t])).values()] })); }
    } catch (reason) { if (generation === epoch.current) fail(reason); }
    finally { if (generation === epoch.current) { if (sequence === readSequence.current) listing.current = false; setMoreLoading(false); } }
  }
  async function loadTargets(pageIndex = 0) {
    if (!session) return; const generation = epoch.current;
    try {
      const next = await identityRequest<{ items: TaskTarget[]; hasMore: boolean }>(tenantApi(tenant, `/sessions/${encodeURIComponent(session)}/task-targets?page=${pageIndex}`), { signal: abort.current.signal });
      if (generation !== epoch.current) return;
      setTargets(previous => pageIndex ? [...previous, ...next.items] : next.items); setTargetsPage(pageIndex); setMoreTargets(next.hasMore); return next.items;
    } catch (reason) { if (generation === epoch.current) fail(reason); }
  }
  async function perform(work: () => Promise<void>) {
    if (pending.current || uncertain) return; const generation = epoch.current; pending.current = true; setBusy(true); setError(null);
    try { await work(); if (generation === epoch.current) { pending.current = false; await refresh(true); } }
    catch (reason) { if (generation === epoch.current) { setUncertain(!(reason instanceof IdentityError && reason.status < 500)); fail(reason); } }
    finally { if (generation === epoch.current) { pending.current = false; setBusy(false); } }
  }
  function choose(id: string) { setApprovalReceipt(null); listing.current = false; setReply(''); replyCommand.current = null; setCreating(false); setDirty(false); setRetryOf(null); createCommand.current = null; form.resetFields(); readSequence.current++; loadedMessages.current = null; setSelected(null); setMessages(null); setNotices([]); setSelectedId(id); selection.current = id; setError(null); requestAnimationFrame(() => back.current?.focus()); }
  function acceptCreateReceipt(saved: TaskCreateReceipt) {
    setCreating(false); setRetryOf(null); setDirty(false); form.resetFields(); createCommand.current = null;
    if ('approvalRequestId' in saved) { setApprovalReceipt(saved); selection.current = null; setSelectedId(null); }
    else choose(saved.task.taskId);
  }
  async function submit(draft: Draft) {
    if (!session || !page || !target) return; const generation = epoch.current;
    const body = createCommand.current ?? { requestKey: crypto.randomUUID(), expectedSecurityRevision: page.securityRevision, objective: draft.objective,
      title: draft.title?.trim() || undefined, retryOfTaskId: retryOf ?? undefined, target: { instanceId: draft.target }, spaceId: draft.spaceId, environmentId: draft.environmentId || undefined, dependsOnTaskIds: draft.dependsOnTaskIds ?? [],
      reportOnCompletion: !!draft.reportOnCompletion, deadlineAt: new Date(Date.now() + draft.hours * 3600000).toISOString() };
    createCommand.current = body;
    uncertainAction.current = { kind: 'create', key: String(body.requestKey) };
    await perform(async () => {
      const saved = await delegateTask(tenant, session, body, abort.current.signal); if (generation !== epoch.current) return;
      acceptCreateReceipt(saved);
    });
  }
  async function replaceTask() {
    if (!session || !selected?.canRetry || selected.sourceSessionId !== session || pending.current || uncertain) return;
    const previous = selected; const generation = epoch.current; pending.current = true; readSequence.current++; setBusy(true); setError(null);
    try {
      const [list, eligible] = await Promise.all([
        identityRequest<TaskPage<DelegatedTask>>(tenantApi(tenant, `${listPath}?scope=created`), { signal: abort.current.signal }), loadTargets(),
      ]);
      if (generation !== epoch.current || selection.current !== previous.taskId || !eligible) return;
      const worker = eligible.find(candidate => candidate.instanceId === previous.selectedTargetInstanceId);
      setPage(list); setScope('created'); loadedPages.current = 1; loadedMessages.current = null;
      selection.current = null; setSelectedId(null); setSelected(null); setMessages(null); setNotices([]);
      setCreating(true); setDirty(true); setRetryOf(previous.taskId); createCommand.current = null;
      form.setFieldsValue({ title: previous.title, objective: previous.objective, target: worker?.instanceId,
        spaceId: worker?.spaces.some(space => space.spaceId === previous.spaceId) ? previous.spaceId! : worker?.defaultSpaceId,
        dependsOnTaskIds: [], hours: 24, reportOnCompletion: false });
      requestAnimationFrame(() => form.getFieldInstance('target')?.focus());
    } catch (reason) { if (generation === epoch.current) fail(reason); }
    finally { if (generation === epoch.current) { pending.current = false; setBusy(false); } }
  }
  function discardAssignment() {
    const previous = retryOf; setRetryOf(null); setCreating(false); setDirty(false); createCommand.current = null; form.resetFields();
    if (previous) choose(previous);
  }
  async function send() {
    if (!selected || !reply.trim()) return; const id = selected.taskId; const generation = epoch.current;
    const command = replyCommand.current ?? { text: reply, requestKey: crypto.randomUUID() }; replyCommand.current = command;
    uncertainAction.current = { kind: 'message', key: command.requestKey, task: id };
    await perform(async () => {
      await identityRequest(taskApi(tenant, id, '/messages'), { method: 'POST', signal: abort.current.signal, body: { ...command, expectedSecurityRevision: selected.securityRevision } });
      if (generation === epoch.current && selection.current === id) { setReply(''); replyCommand.current = null; }
    });
  }
  function cancel() {
    if (!selected) return; const task = selected;
    confirmAction(modal, { title: 'Request task cancellation?', content: 'The worker may already have performed an effect. Read the final saved outcome before retrying work.', okText: 'Request cancellation',
      onOk: () => perform(async () => { const key = crypto.randomUUID(); uncertainAction.current = { kind: 'cancel', key, task: task.taskId }; await identityRequest(taskApi(tenant, task.taskId, '/cancel'), { method: 'POST', signal: abort.current.signal,
        body: { expectedRevision: task.revision, expectedSecurityRevision: task.securityRevision, requestKey: key } }); }) });
  }
  async function moreMessages() {
    if (!selected || !messages?.nextCursor) return; const generation = epoch.current; const id = selected.taskId;
    try { const next = await readTaskMessages(tenant, id, abort.current.signal, messages.nextCursor);
      if (generation === epoch.current && selection.current === id) { const merged = { ...next, items: [...new Map([...(loadedMessages.current?.page.items ?? []), ...next.items].map(m => [m.messageId, m])).values()].slice(-500) }; loadedMessages.current = { task: id, page: merged }; setMessages(merged); } }
    catch (reason) { if (generation === epoch.current) fail(reason); }
  }
  async function recover() {
    const action = uncertainAction.current; if (!action || pending.current) return; const generation = epoch.current; setBusy(true);
    try {
      const path = action.kind === 'create' ? tenantApi(tenant, `/sessions/${encodeURIComponent(session!)}/tasks/recovery?requestKey=${encodeURIComponent(action.key)}`)
        : action.kind === 'message' ? taskApi(tenant, action.task!, `/messages/recovery?requestKey=${encodeURIComponent(action.key)}`)
          : taskApi(tenant, action.task!, `/requests/${encodeURIComponent(action.key)}?kind=${action.kind}${action.notification ? `&notificationId=${encodeURIComponent(action.notification)}` : ''}`);
      const saved = await identityRequest<TaskCreateReceipt | { task?: DelegatedTask } | null>(path, { signal: abort.current.signal });
      if (generation !== epoch.current) return;
      if (!saved) { setError('No saved receipt is visible yet. Read the same request again; another mutation stays disabled.'); return; }
      setUncertain(false); setError(null); uncertainAction.current = null;
      if (action.kind === 'create') {
        if ('approvalRequestId' in saved) acceptCreateReceipt(saved);
        else if (saved.task) acceptCreateReceipt({ task: saved.task, replayed: true, securityRevision: page?.securityRevision ?? 0 });
      }
      if (action.kind === 'message') { replyCommand.current = null; setReply(''); }
      await refresh(true);
    } catch (reason) { if (generation === epoch.current) fail(reason); }
    finally { if (generation === epoch.current) setBusy(false); }
  }
  const approvalMessage = approvalReceipt?.admissionState === 'pendingReview' ? 'The request is saved. A Task and worker will be admitted only after approval and current checks.'
    : approvalReceipt?.admissionState === 'awaitingAdmission' ? 'The decision is saved. Reload tasks to read the admission result.' : 'This request created no Task. Review the saved decision before creating another request.';
  const approvalAction = approvalReceipt?.canReadApproval ? <Button href={tenantApprovalPath(tenant, approvalReceipt.approvalRequestId)} onClick={followAppLink}>View approval</Button> : undefined;
  const recoverAction = <Button loading={busy} onClick={() => void recover()}>Read saved request</Button>;
  const task = selected;
  return <>{guard.holder}<SessionSandboxFrame title="Tasks" open={open} narrow={narrow} openerRef={openerRef} onClose={guard.close} blocked={busy}>
    <Flex wrap gap={token.paddingXS} justify="space-between">
      {selectedId && (session || instance) ? <Button ref={back} type="text" disabled={busy || uncertain} icon={<ArrowLeftOutlined aria-hidden="true" />} onClick={() => guard.requestLeave(() => { const previous = selectedId; listing.current = false; readSequence.current++; selection.current = null; loadedMessages.current = null; setSelectedId(null); setSelected(null); setMessages(null); setNotices([]); setReply(''); replyCommand.current = null; requestAnimationFrame(() => rowButtons.current.get(previous)?.focus()); })}>All tasks</Button>
        : <Typography.Text type="secondary">Assignments have separate worker Sessions and Run outcomes.</Typography.Text>}
      <Button icon={<ReloadOutlined aria-hidden="true" />} disabled={busy} onClick={() => void refresh(true)}>Reload tasks</Button>
    </Flex>
    {error ? <Alert type="error" showIcon title={error} /> : null}
    {approvalReceipt ? <Alert showIcon type={approvalReceipt.admissionState === 'awaitingAdmission' ? 'info' : 'warning'}
      title={approvalReceipt.admissionState === 'pendingReview' ? 'Task needs approval' : approvalReceipt.admissionState === 'awaitingAdmission' ? 'Approval recorded; admission pending' : 'Task was not admitted'}
      description={<Flex vertical align="start" gap={token.paddingSM}><span>{approvalMessage}</span>{narrow ? approvalAction : null}</Flex>}
      action={narrow ? undefined : approvalAction} /> : null}
    {uncertain ? <Alert type="warning" showIcon title="Read saved facts before another action" description={<Flex vertical align="start" gap={token.paddingSM}><span>The last change is unconfirmed. Request readback does not repeat it.</span>{narrow ? recoverAction : null}</Flex>} action={narrow ? undefined : recoverAction} /> : null}
    {loading && !page && !task ? <Skeleton active paragraph={{ rows: 4 }} /> : null}
    {selectedId ? task ? <Flex vertical gap={token.padding}>
      <Flex vertical gap={token.paddingXS}>
        <Typography.Title level={5} style={{ margin: 0, overflowWrap: 'anywhere' }}>{task.title}</Typography.Title>
        <Flex wrap align="center" gap={token.paddingXS}><Tag>{taskStatus[task.state]}</Tag><Typography.Text style={{ minWidth: 0, overflowWrap: 'anywhere' }}>Assigned to {task.selectedTargetName ?? 'an authorized Agent'}</Typography.Text></Flex>
        <Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>{task.sourceActorKind === 'Human' ? 'Requested by a human supervisor'
          : task.sourceActorKind === 'Agent' ? `Delegated by ${task.sourceAgentName ?? 'an authorized Agent'}` : task.sourceActorKind === 'WorkflowContinuation' ? 'Continued by the saved formal Workflow' : 'Source attribution unavailable'}</Typography.Text>
        <Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>Worker Space: {task.spaceName ?? 'Unavailable under current access'} · Environment: {task.environmentConfigured === false ? 'None selected' : task.environmentName ?? 'Unavailable under current access'}</Typography.Text>
        <Typography.Text type="secondary">Deadline {formatChatTime(task.deadlineAt)} · Updated {formatChatTime(task.updatedAt)}</Typography.Text>
        <Typography.Paragraph style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{task.objective}</Typography.Paragraph>
        {task.reason ? <Typography.Text type={task.state === 'NeedsAttention' ? 'warning' : 'secondary'}>{task.reason.replaceAll('_', ' ')}</Typography.Text> : null}
      </Flex>
      {task.state === 'NeedsAttention' ? <Alert type="warning" showIcon title="The outcome needs attention" description="An effect or result is unconfirmed. Inspect saved evidence before starting replacement work." /> : null}
      {task.result ? <Flex vertical gap={token.paddingXS}><Typography.Title level={5} style={{ margin: 0 }}>Original worker result</Typography.Title><Typography.Paragraph style={{ margin: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{task.result}</Typography.Paragraph><Typography.Text type="secondary">Task success is separate from Goal and Workflow verification.</Typography.Text></Flex>
        : task.state === 'Succeeded' ? <Typography.Text type="secondary">The worker succeeded. Reading the original result requires independent access to its Session and Run.</Typography.Text> : null}
      {task.workerSessionId && task.selectedTargetInstanceId ? <Typography.Link href={`${tenantAgentPath(tenant, task.selectedTargetInstanceId, 'activity-sessions')}?session=${encodeURIComponent(task.workerSessionId)}`} onClick={followAppLink}>Inspect worker Session and Run</Typography.Link> : null}
      {task.workflowId && task.workflowExecutionId && <Typography.Link href={workflowPath(tenant, task.workflowId, task.workflowExecutionId)} onClick={followAppLink}>Inspect Workflow execution</Typography.Link>}
      {task.sourceSessionId && task.sourceInstanceId ? <Typography.Link href={`${tenantAgentPath(tenant, task.sourceInstanceId, 'activity-sessions')}?session=${encodeURIComponent(task.sourceSessionId)}${task.sourceRunId ? `&run=${encodeURIComponent(task.sourceRunId)}` : ''}`} onClick={followAppLink}>Inspect source Session{task.sourceRunId ? ' and Run' : ''}</Typography.Link> : null}
      {notices.map(notice => <Flex key={notice.notificationId} vertical gap={token.paddingXS}>
        <Typography.Text type="secondary">{notice.acknowledgedAt ? 'Result acknowledged' : 'Result awaiting acknowledgment'}</Typography.Text>
        {task.reportOnCompletion ? <Typography.Text type="secondary">{notice.reportAdmitted
          ? 'A report Run was admitted in the source Session. Inspect that Session for its outcome.'
          : notice.withheldReason ? reportWithheldText(notice.withheldReason)
            : 'A source report was requested and is awaiting admission.'}</Typography.Text>
          : <Typography.Text type="secondary">Inbox only. No source report was requested.</Typography.Text>}
      </Flex>)}
      {task.sourceActorKind === 'WorkflowContinuation' ? <Typography.Paragraph type="secondary">The saved process controls this Task continuation. Required proof is recorded in its Workflow execution.</Typography.Paragraph> : <Flex vertical gap={token.paddingXS}>
        <Typography.Title level={5} style={{ margin: 0 }}>Messages</Typography.Title>
        <Typography.Text type="secondary">Correspondence is untrusted evidence. Human replies are attributed as guidance.</Typography.Text>
        {!messages?.items.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No task messages yet" /> : <ol className="background-work-list" aria-label="Task messages">
          {messages.items.map(message => <li className="background-work-item" key={message.messageId}><Flex vertical gap={token.paddingXS}>
            <Typography.Text strong style={{ overflowWrap: 'anywhere' }}>{message.senderKind === 'Human' ? 'Human guidance' : message.kind} · {message.senderDisplayName ?? (message.senderKind === 'Human' ? 'Supervisor' : 'Agent')}</Typography.Text>
            <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{message.text}</Typography.Text><Typography.Text type="secondary">{formatChatTime(message.createdAt)}</Typography.Text>
          </Flex></li>)}
        </ol>}
        {messages?.hasMore ? <Button onClick={() => void moreMessages()}>Load more task messages</Button> : null}
        {task.canSend && !readonly ? <><label htmlFor="task-guidance">Human guidance</label><Input.TextArea id="task-guidance" value={reply} maxLength={2000} autoSize={{ minRows: 2, maxRows: 6 }} disabled={busy || uncertain} onChange={event => setReply(event.target.value)} /><Button type="primary" loading={busy} disabled={!reply.trim() || uncertain} onClick={() => void send()}>Send guidance</Button></> : null}
      </Flex>}
      {task.retryOfTaskId ? <Button type="link" disabled={busy || uncertain} style={{ alignSelf: 'flex-start', padding: 0 }} onClick={() => guard.requestLeave(() => choose(task.retryOfTaskId!))}>Inspect previous task</Button> : null}
      <Flex wrap gap={token.paddingXS}>
        {task.canRetry && task.sourceSessionId === session && !readonly ? <Button disabled={busy || uncertain} onClick={() => void replaceTask()}>Create replacement task</Button> : null}
        {task.canCancel && !readonly ? <Button danger disabled={busy || uncertain || task.cancellationRequested} onClick={cancel}>{task.cancellationRequested ? 'Cancellation requested' : 'Cancel task'}</Button> : null}
        {task.canAcknowledge && !readonly ? notices.filter(notice => !notice.acknowledgedAt).map(notice => <Button key={notice.notificationId} disabled={busy || uncertain} onClick={() => void perform(async () => { const key = crypto.randomUUID(); uncertainAction.current = { kind: 'acknowledge', key, task: task.taskId, notification: notice.notificationId }; await identityRequest(taskApi(tenant, task.taskId, `/notifications/${notice.notificationId}/acknowledge`), { method: 'POST', signal: abort.current.signal,
          body: { requestKey: key, expectedRevision: notice.revision, expectedSecurityRevision: task.securityRevision } }); })}>Acknowledge result</Button>) : null}
      </Flex>
    </Flex> : !loading && !error ? <Empty description="This task is unavailable in the current scope" /> : null : <>
      {session ? <Tabs activeKey={scope} onChange={key => guard.requestLeave(() => {
        epoch.current++; readSequence.current++; listing.current = false; loadedPages.current = 1; abort.current.abort(); abort.current = new AbortController();
        setMoreLoading(false); setScope(key); setPage(null); setCreating(false); setDirty(false); setRetryOf(null); createCommand.current = null; form.resetFields();
      })} items={[{ key: 'created', label: 'Created', disabled: busy || uncertain }, { key: 'assigned', label: 'Assigned', disabled: busy || uncertain }]} /> : null}
      {page?.canDelegate && !readonly && !creating ? <Button icon={<PlusOutlined aria-hidden="true" />} disabled={uncertain} onClick={() => { setRetryOf(null); setCreating(true); void loadTargets(); }}>Delegate task</Button> : null}
      {creating ? <Form form={form} layout="vertical" disabled={busy || uncertain} initialValues={{ hours: 24 }} onValuesChange={() => { setDirty(true); createCommand.current = null; }} onFinish={draft => void submit(draft)}>
        {retryOf ? <Alert type="info" showIcon title="New replacement assignment" description="Confirm the current Agent, Space and objective. This creates a new Task and preserves the previous saved outcome." style={{ marginBottom: token.margin }} /> : null}
        <Form.Item label="Assigned Agent" name="target" rules={[{ required: true }]}><Select placeholder="Choose an eligible Agent" options={targets.map(t => ({ label: t.name, value: t.instanceId }))} onChange={id => form.setFieldsValue({ spaceId: targets.find(t => t.instanceId === id)?.defaultSpaceId, environmentId: undefined })} /></Form.Item>
        {!targets.length ? <Typography.Paragraph type="secondary">No eligible Agents are available. Enable task acceptance and grant the worker its own execution permissions in Agent settings.</Typography.Paragraph> : null}
        {moreTargets ? <Button onClick={() => void loadTargets(targetsPage + 1)}>Load more eligible Agents</Button> : null}
        <Form.Item label="Worker Space" name="spaceId" rules={[{ required: true }]} extra="The worker uses its own membership. Your files and credentials are not shared."><Select disabled={!target} options={target?.spaces.map(s => ({ value: s.spaceId, label: s.name }))} onChange={() => form.setFieldValue('environmentId', undefined)} /></Form.Item>
        <Form.Item label="Environment profile" name="environmentId" extra="Only profiles readable by you and the worker are listed. Values remain private."><Select allowClear placeholder="No Environment profile" disabled={!selectedSpace?.environments?.length} options={selectedSpace?.environments?.map(environment => ({ value: environment.environmentId, label: environment.name }))} /></Form.Item>
        {target ? <Flex vertical gap={token.paddingXS} style={{ marginBottom: token.margin }}>
          <Typography.Text>Worker model: {target.model?.label ?? 'Model details require model access'}</Typography.Text>
          {target.budget ? <Typography.Text type="secondary">Worker ceiling: {target.budget.maxSteps} steps and {target.budget.durationSeconds} seconds of active time. The remaining root budget may lower these limits.</Typography.Text> : null}
          <Typography.Text type="secondary">Current settings are rechecked when the assignment is saved.</Typography.Text>
        </Flex> : null}
        <Form.Item label="Title" name="title"><Input maxLength={80} /></Form.Item>
        <Form.Item label="Objective" name="objective" rules={[{ required: true, whitespace: true }]}><Input.TextArea maxLength={4000} autoSize={{ minRows: 3, maxRows: 8 }} showCount /></Form.Item>
        <Form.Item label="Wait for earlier tasks" name="dependsOnTaskIds"><Select mode="multiple" maxCount={8} options={page?.items.map(t => ({ label: t.title, value: t.taskId }))} /></Form.Item>
        <Form.Item label="Deadline in hours" name="hours" rules={[{ required: true }]}><InputNumber min={1} max={24} /></Form.Item>
        <Form.Item name="reportOnCompletion" valuePropName="checked"><Checkbox>Send one completion report to this conversation</Checkbox></Form.Item>
        <Flex wrap gap={token.paddingXS}><Button htmlType="submit" type="primary" loading={busy} disabled={!targets.length || uncertain}>Assign task</Button><Button disabled={busy || uncertain} onClick={discardAssignment}>Discard assignment</Button></Flex>
      </Form> : null}
      {!loading && page && !page.items.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={scope === 'created' ? 'No tasks created in this conversation' : 'No assigned tasks in this scope'} /> : null}
      <ul className="background-work-list" aria-label="Delegated tasks">{page?.items.map(item => <li key={item.taskId} className="background-work-item"><Flex vertical gap={token.paddingXS}>
        <Flex wrap align="center" gap={token.paddingXS}><Typography.Text strong style={{ overflowWrap: 'anywhere' }}>{item.title}</Typography.Text><Tag>{taskStatus[item.state]}</Tag></Flex>
        <Typography.Text type="secondary">Assigned to {item.selectedTargetName ?? 'an authorized Agent'} · {formatChatTime(item.updatedAt)}</Typography.Text>
        <Button ref={node => { if (node) rowButtons.current.set(item.taskId, node); else rowButtons.current.delete(item.taskId); }} style={{ alignSelf: 'flex-start' }} disabled={busy || uncertain} onClick={() => guard.requestLeave(() => choose(item.taskId))}>Inspect task</Button>
      </Flex></li>)}</ul>
      <DrawerListFooter loadingMore={moreLoading} hasMore={!!page?.hasMore} error={error} count={page?.items.length ?? 0} onLoadMore={() => void loadMore()} onRetry={() => void refresh(true)} />
    </>}
  </SessionSandboxFrame></>;
}
