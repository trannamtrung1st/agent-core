import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Collapse, Empty, Flex, Grid, Skeleton, Typography, theme } from 'antd';
import { tenantApi } from '../../services/identity';
import { privateTaskDenial, type AgentThread, type TaskMessage, type TaskPage } from '../../services/tenantTasks';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { TaskCenterDrawer } from '../chat/TaskCenterDrawer';
import { formatChatTime } from '../chat/chatTime';

export function InstanceCommunicationSection({ tenant, instance }: { tenant: string; instance: string }) {
  const { token } = theme.useToken(); const [open, setOpen] = useState(false); const [tasksOpen, setTasksOpen] = useState(false);
  const screens = Grid.useBreakpoint();
  const [threadCursor, setThreadCursor] = useState<string | null>(null); const [threadHistory, setThreadHistory] = useState<(string | null)[]>([]);
  const [messageCursor, setMessageCursor] = useState<string | null>(null); const [messageHistory, setMessageHistory] = useState<(string | null)[]>([]);
  const [threadId, setThreadId] = useState<string | null>(null); const opener = useRef<HTMLButtonElement>(null);
  const threads = useIdentityQuery<TaskPage<AgentThread>>(open ? tenantApi(tenant, `/agent-threads?instanceId=${encodeURIComponent(instance)}${threadCursor ? `&cursor=${encodeURIComponent(threadCursor)}` : ''}`) : null);
  const messages = useIdentityQuery<TaskPage<TaskMessage>>(open && threadId ? tenantApi(tenant, `/agent-threads/${encodeURIComponent(threadId)}/messages${messageCursor ? `?cursor=${encodeURIComponent(messageCursor)}` : ''}`) : null);
  const denied = privateTaskDenial(threads.error) || privateTaskDenial(messages.error);
  useEffect(() => { setThreadId(null); setTasksOpen(false); }, [tenant, instance]);
  useEffect(() => { if (denied) { setThreadId(null); setTasksOpen(false); } }, [denied]);
  useEffect(() => {
    if (!open) return;
    const reload = (event: Event) => { if ((event as CustomEvent<{ tenant: string }>).detail?.tenant === tenant) { threads.reload(); if (threadId) messages.reload(); } };
    window.addEventListener('agentcore:coordination-changed', reload);
    return () => window.removeEventListener('agentcore:coordination-changed', reload);
  }, [open, tenant, threadId, threads.reload, messages.reload]);
  const selectThread = (id: string | null) => { setThreadId(id); setMessageCursor(null); setMessageHistory([]); };
  return <><Collapse onChange={keys => setOpen(keys.length > 0)} items={[{ key: 'communication', label: 'Agent Inbox & Communication', children:
    <Flex vertical gap={token.padding}>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Assigned work and private two-Agent threads. Humans inspect direct correspondence; Agent messages are sent by actual claimed Runs.</Typography.Paragraph>
      <Flex wrap gap={token.paddingXS}><Button ref={opener} onClick={() => setTasksOpen(true)}>Open assigned tasks</Button><Button onClick={() => { threads.reload(); if (threadId) messages.reload(); }}>Reload communication</Button></Flex>
      {denied ? <Alert type="error" showIcon title="Communication is unavailable in the current scope" /> : <>
        {threads.loading ? <Skeleton active paragraph={{ rows: 2 }} /> : threads.error ? <Alert type="error" showIcon title={threads.error.message} /> : !threads.data?.items.length ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No direct Agent threads" /> :
          <Flex vertical gap={token.paddingXS}>{threads.data.items.map(thread => <Button key={thread.threadId} type={threadId === thread.threadId ? 'primary' : 'default'} style={{ height: 'auto', minHeight: screens.md ? token.controlHeight : token.controlHeightLG, whiteSpace: 'normal', textAlign: 'start' }} onClick={() => selectThread(thread.threadId)}>{thread.firstName} and {thread.secondName} · {formatChatTime(thread.createdAt)}</Button>)}</Flex>}
        {threads.data && (threadHistory.length > 0 || threads.data.hasMore) ? <Flex wrap gap={token.paddingXS}>
          <Button disabled={!threadHistory.length || threads.loading} onClick={() => { setThreadCursor(threadHistory.at(-1) ?? null); setThreadHistory(history => history.slice(0, -1)); selectThread(null); }}>Previous threads</Button>
          <Button disabled={!threads.data.hasMore || threads.loading} onClick={() => { setThreadHistory(history => [...history, threadCursor]); setThreadCursor(threads.data!.nextCursor); selectThread(null); }}>Next threads</Button>
        </Flex> : null}
        {threadId ? messages.loading ? <Skeleton active paragraph={{ rows: 3 }} /> : messages.error ? <Alert type="error" showIcon title={messages.error.message} /> : <Flex vertical gap={token.paddingXS}>
          <Typography.Title level={5} style={{ margin: 0 }}>Two-Agent correspondence</Typography.Title>
          <ol className="background-work-list" aria-label="Direct Agent messages">{messages.data?.items.map(message => <li key={message.messageId} className="background-work-item"><Flex vertical gap={token.paddingXS}>
            <Typography.Text strong>{message.senderDisplayName ?? 'Agent'}</Typography.Text><Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{message.text}</Typography.Text><Typography.Text type="secondary">{formatChatTime(message.createdAt)} · {message.deliveryState === 'ActivationAdmitted' ? 'Recipient Run admitted' : message.deliveryState === 'NotificationPending' ? 'Wake pending' : message.deliveryState === 'Undeliverable' ? 'Wake unavailable' : 'Recorded in inbox'}</Typography.Text>
            {message.deliveryReason ? <Typography.Text type="secondary">{message.deliveryReason.replaceAll('_', ' ')}</Typography.Text> : null}
          </Flex></li>)}</ol>
          {messageHistory.length > 0 || messages.data?.hasMore ? <Flex wrap gap={token.paddingXS}>
            <Button disabled={!messageHistory.length} onClick={() => { setMessageCursor(messageHistory.at(-1) ?? null); setMessageHistory(history => history.slice(0, -1)); }}>Older messages</Button>
            <Button disabled={!messages.data?.hasMore} onClick={() => { setMessageHistory(history => [...history, messageCursor]); setMessageCursor(messages.data!.nextCursor); }}>Newer messages</Button>
          </Flex> : null}
          <Typography.Text type="secondary">Stored correspondence does not prove that a recipient model ran or replied.</Typography.Text>
        </Flex> : null}
      </>}
    </Flex> }]} /><TaskCenterDrawer tenant={tenant} instance={instance} open={tasksOpen && !denied} narrow={!screens.md} openerRef={opener} onClose={() => setTasksOpen(false)} /></>;
}
