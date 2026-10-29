import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App, ConfigProvider } from 'antd';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { IdentityError, identityRequest } from '../../services/identity';
import { delegateTask, readTask, readTaskMessages, readTaskNotices, type DelegatedTask, type TaskPage } from '../../services/tenantTasks';
import { TaskCenterDrawer } from './TaskCenterDrawer';
vi.mock('../../services/identity', async original => ({ ...await original<object>(), identityRequest: vi.fn() }));
vi.mock('../../services/tenantTasks', async original => ({ ...await original<object>(), delegateTask: vi.fn(), readTask: vi.fn(), readTaskMessages: vi.fn(), readTaskNotices: vi.fn() }));
const task = (taskId: string): DelegatedTask => ({ taskId, title: taskId, objective: `Private ${taskId}`, revision: 1, state: 'Waiting',
  sourceSessionId: 'source', selectedTargetInstanceId: 'worker', selectedTargetName: 'Worker', spaceId: null, workerSessionId: null,
  initialRunId: null, terminalRunId: null, result: null, reason: null, cancellationRequested: false,
  createdAt: '2026-10-27T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z', deadlineAt: '2026-10-28T00:00:00Z',
  reportOnCompletion: false, canInspect: true, canCancel: true, canSend: true, securityRevision: 1, retryOfTaskId: null, canRetry: false, canAcknowledge: true });
const page = (items: DelegatedTask[], nextCursor: string | null = null): TaskPage<DelegatedTask> => ({ items, nextCursor, hasMore: !!nextCursor, securityRevision: 1 });
const show = (tenant = 'first', initialTaskId?: string) => <ConfigProvider theme={{ token: { motion: false } }}><App><TaskCenterDrawer tenant={tenant} session="source" open narrow={false} initialTaskId={initialTaskId} onClose={vi.fn()} /></App></ConfigProvider>;
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(readTask).mockImplementation(async (_tenant, id) => task(id));
  vi.mocked(readTaskMessages).mockResolvedValue({ items: [], nextCursor: null, hasMore: false, securityRevision: 1 });
  vi.mocked(readTaskNotices).mockResolvedValue({ items: [], nextCursor: null, hasMore: false, securityRevision: 1 });
});
describe('Task Center current private scope', () => {
  it('keeps a reviewed human assignment separate from an admitted Task and retires its receipt on scope change', async () => {
    vi.mocked(readTask).mockResolvedValue({ ...task('private-task'), state: 'Cancelled', canRetry: true, canCancel: false, canSend: false });
    vi.mocked(identityRequest).mockImplementation(async url => String(url).includes('task-targets')
      ? { items: [{ instanceId: 'worker', name: 'Worker', defaultSpaceId: 'private-space', spaces: [{ spaceId: 'private-space', name: 'Worker private Space' }] }], hasMore: false }
      : { ...page([task('private-task')]), canDelegate: true });
    vi.mocked(delegateTask).mockResolvedValue({ approvalRequestId: 'approval-a', approvalStatus: 'Pending', canReadApproval: true,
      admissionState: 'pendingReview', reasonCode: null, securityRevision: 2, replayed: false });
    const rendered = render(show('first', 'private-task'));
    fireEvent.click(await screen.findByRole('button', { name: 'Create replacement task' }));
    const assign = await screen.findByRole('button', { name: 'Assign task' });
    await waitFor(() => expect(assign).toBeEnabled());
    await waitFor(() => expect(screen.getByLabelText('Objective')).toHaveValue('Private private-task'));
    fireEvent.click(assign);
    expect(await screen.findByText('Task needs approval', {}, { timeout: 5_000 })).toBeVisible();
    expect(screen.getByRole('link', { name: 'View approval' })).toHaveAttribute('href', expect.stringContaining('approval-a'));
    expect(vi.mocked(readTask).mock.calls.some(([, id]) => id === 'approval-a')).toBe(false);
    expect(vi.mocked(delegateTask)).toHaveBeenCalledOnce();
    await act(async () => { rendered.rerender(show('second')); });
    expect(screen.queryByText('Task needs approval')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'View approval' })).not.toBeInTheDocument();
  });
  it('distinguishes inbox acknowledgment from an admitted or withheld source report', async () => {
    vi.mocked(readTask).mockResolvedValue({ ...task('blocked-task'), state: 'Blocked', reportOnCompletion: true, canSend: false, canCancel: false });
    const notice = { notificationId: 'notice', taskId: 'blocked-task', revision: 1, createdAt: '2026-10-27T00:00:00Z',
      acknowledgedAt: null, withheldReason: 'task_report_no_worker_run', reportAdmitted: false };
    vi.mocked(readTaskNotices).mockResolvedValue({ items: [notice], nextCursor: null, hasMore: false, securityRevision: 1 });
    render(show('first', 'blocked-task'));
    expect(await screen.findByText('No worker started; the outcome remains in this inbox.')).toBeVisible();
    expect(screen.getByText('Result awaiting acknowledgment')).toBeVisible();
    expect(screen.getByRole('button', { name: 'Acknowledge result' })).toBeVisible();
    vi.mocked(readTaskNotices).mockResolvedValue({ items: [{ ...notice, withheldReason: null, reportAdmitted: true, acknowledgedAt: notice.createdAt }], nextCursor: null, hasMore: false, securityRevision: 1 });
    fireEvent.click(screen.getByRole('button', { name: /Reload tasks/ }));
    expect(await screen.findByText('A report Run was admitted in the source Session. Inspect that Session for its outcome.')).toBeVisible();
    expect(screen.getByText('Result acknowledged')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Acknowledge result' })).not.toBeInTheDocument();
  });
  it('revalidates every loaded page and removes rows that become unreadable', async () => {
    let removed = false;
    vi.mocked(identityRequest).mockImplementation(async url => page(String(url).includes('cursor=') ? (removed ? [] : [task('second-row')]) : [task('first-row')], String(url).includes('cursor=') ? null : 'next'));
    render(show());
    expect(await screen.findByText('first-row')).toBeVisible();
    fireEvent.click(await screen.findByRole('button', { name: 'Load more' }));
    expect(await screen.findByText('second-row')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: /Reload tasks/ }));
    await waitFor(() => expect(vi.mocked(identityRequest).mock.calls).toHaveLength(4));
    expect(screen.getByText('second-row')).toBeVisible();
    removed = true; fireEvent.click(screen.getByRole('button', { name: /Reload tasks/ }));
    await waitFor(() => expect(screen.queryByText('second-row')).not.toBeInTheDocument());
    expect(screen.getByText('first-row')).toBeVisible();
  });
  it('clears private content on tenant change and ignores a late old response', async () => {
    let finish!: (value: TaskPage<DelegatedTask>) => void;
    vi.mocked(identityRequest).mockResolvedValueOnce(page([task('old-private')]))
      .mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }))
      .mockResolvedValueOnce(page([task('new-private')]));
    const view = render(show()); expect(await screen.findByText('old-private')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: /Reload tasks/ }));
    view.rerender(show('second'));
    expect(screen.queryByText('old-private')).not.toBeInTheDocument();
    expect(await screen.findByText('new-private')).toBeVisible();
    await act(async () => finish(page([task('late-private')])));
    expect(screen.queryByText('late-private')).not.toBeInTheDocument();
    expect(screen.getByText('new-private')).toBeVisible();
  });
  it('retires text, messages and mutation controls on a denied refresh', async () => {
    vi.mocked(readTaskMessages).mockResolvedValue({ items: [{ messageId: 'message', threadId: 'thread', sequence: 1, senderKind: 'Human', senderInstanceId: null,
      kind: 'HumanGuidance', text: 'Private guidance', createdAt: '2026-10-27T00:00:00Z' }], nextCursor: null, hasMore: false, securityRevision: 1 });
    vi.mocked(readTask).mockResolvedValueOnce(task('private-task')).mockRejectedValueOnce(new IdentityError(403, 'resource_unavailable'));
    render(show('first', 'private-task'));
    expect(await screen.findByText('Private private-task')).toBeVisible(); expect(screen.getByText('Private guidance')).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: /Reload tasks/ }));
    await waitFor(() => expect(screen.queryByText('Private private-task')).not.toBeInTheDocument());
    expect(screen.queryByText('Private guidance')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Send guidance' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Cancel task' })).not.toBeInTheDocument();
  });
  it('recovers an uncertain message with the same request key without reposting', async () => {
    vi.mocked(identityRequest).mockRejectedValueOnce(new TypeError('connection lost')).mockResolvedValueOnce({ messageId: 'saved' });
    render(show('first', 'private-task'));
    fireEvent.change(await screen.findByLabelText('Human guidance'), { target: { value: 'Clarification' } });
    fireEvent.click(screen.getByRole('button', { name: 'Send guidance' }));
    expect(await screen.findByRole('button', { name: 'Read saved request' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Send guidance' })).toBeDisabled();
    const command = vi.mocked(identityRequest).mock.calls[0][1]?.body as { requestKey: string };
    fireEvent.click(screen.getByRole('button', { name: 'Read saved request' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Read saved request' })).not.toBeInTheDocument());
    expect(vi.mocked(identityRequest).mock.calls[1][0]).toContain(`/messages/recovery?requestKey=${command.requestKey}`);
    expect(vi.mocked(identityRequest).mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
    expect(screen.getByLabelText('Human guidance')).toHaveValue('');
  });
  it('deliberately prepares a replacement with a new request key and the previous terminal Task link', async () => {
    vi.mocked(readTask).mockImplementation(async (_tenant, id) => id === 'private-task'
      ? { ...task(id), state: 'Cancelled', canRetry: true, canCancel: false, canSend: false }
      : { ...task(id), retryOfTaskId: 'private-task' });
    vi.mocked(identityRequest).mockImplementation(async url => String(url).includes('task-targets')
      ? { items: [{ instanceId: 'worker', name: 'Worker', defaultSpaceId: 'private-space', spaces: [{ spaceId: 'private-space', name: 'Worker private Space' }] }], hasMore: false }
      : { ...page([task('private-task')]), canDelegate: true });
    vi.mocked(delegateTask).mockResolvedValue({ task: { taskId: 'replacement', revision: 1 }, replayed: false, securityRevision: 2 });
    render(show('first', 'private-task'));
    fireEvent.click(await screen.findByRole('button', { name: 'Create replacement task' }));
    expect(await screen.findByText('New replacement assignment')).toBeVisible();
    expect(screen.getByLabelText('Objective')).toHaveValue('Private private-task');
    expect(vi.mocked(delegateTask)).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Assign task' }));
    expect(await screen.findByText('Private replacement')).toBeVisible();
    expect(vi.mocked(delegateTask)).toHaveBeenCalledOnce();
    const [tenant, session, body] = vi.mocked(delegateTask).mock.calls[0];
    expect([tenant, session]).toEqual(['first', 'source']);
    expect(body).toMatchObject({ retryOfTaskId: 'private-task', target: { instanceId: 'worker' }, spaceId: 'private-space', reportOnCompletion: false, dependsOnTaskIds: [] });
    expect(body.requestKey).toEqual(expect.any(String));
    fireEvent.click(screen.getByRole('button', { name: 'Inspect previous task' }));
    expect(await screen.findByText('Private private-task')).toBeVisible();
    expect(vi.mocked(delegateTask)).toHaveBeenCalledOnce();
  });

  it('keeps a guidance draft bound to its recipient while confirming a task switch', async () => {
    vi.mocked(identityRequest).mockResolvedValue(page([task('first-task'), task('second-task')]));
    render(show('first', 'first-task'));
    fireEvent.change(await screen.findByLabelText('Human guidance'), { target: { value: 'Only for the first worker' } });
    fireEvent.click(screen.getByRole('button', { name: 'All tasks' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Discard changes' })).toBeVisible(), { timeout: 5000 });
    expect(screen.getByLabelText('Human guidance')).toHaveValue('Only for the first worker');
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    // The guarded switch commits after the Modal close animation, not on its OK click.
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Discard changes' })).not.toBeInTheDocument(), { timeout: 5000 });
    await waitFor(() => expect(screen.getAllByRole('button', { name: 'Inspect task' })).toHaveLength(2), { timeout: 5000 });
    fireEvent.click(screen.getAllByRole('button', { name: 'Inspect task' })[1]);
    expect(await screen.findByText('Private second-task')).toBeVisible();
    expect(screen.getByLabelText('Human guidance')).toHaveValue('');
    expect(vi.mocked(identityRequest).mock.calls.some(([, options]) => options?.method === 'POST')).toBe(false);
  });

});
