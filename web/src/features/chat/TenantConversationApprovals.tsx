import { useEffect, useRef, useState } from 'react';
import { Alert, Badge, Button, Flex, Pagination, Tag, Tooltip, Typography } from 'antd';
import { CheckSquareOutlined, ReloadOutlined } from '@ant-design/icons';
import { tenantApi, IdentityError } from '../../services/identity';
import { releaseDurableApprovalWait, retireUnavailableTenantChat } from '../../services/realtime';
import { TenantApprovalDetail, approvalDate, approvalResultLabel } from '../access/TenantApprovals';
import { tenantApprovalPath } from '../access/accessRoute';
import { useApprovalUpdates } from '../access/useApprovalUpdates';

type Approval = { requestId: string; runId: string; responseId: string | null; operation: string; kind: string; status: string;
  createdAt: string; expiresAt: string; waiting: boolean; canInspect: boolean; outcome: string | null };
const titles: Record<string, string> = { verify_artifact: 'Verify file metadata', publish_artifact: 'Publish exact file', inspect_credential_metadata: 'Read bound Credential metadata', browser_command: 'Review exact Browser command', update_persona: 'Update agent persona', update_preferences: 'Update conversation preferences', check_credential_connection: 'Check credential connection', run_sandbox_command: 'Run exact Sandbox command', sandbox_control: 'Review Sandbox cleanup request', sandbox_read: 'Review stored Sandbox read' };
const statuses: Record<string, string> = { pending: 'Waiting for review', approved: 'Approved', rejected: 'Rejected', expired: 'Expired', cancelled: 'Cancelled' };
const result = (item: Approval) => approvalResultLabel(item.operation, item.outcome ?? '');
export function TenantConversationApprovals({ tenant, organization, session }: { tenant: string; organization: string; session: string }) {
  const [page, setPage] = useState(0); const [selected, setSelected] = useState<string>(); const opener = useRef<HTMLElement | null>(null);
  const state = useApprovalUpdates<{ items: Approval[]; total: number }>(tenantApi(tenant, `/sessions/${session}/approvals?page=${page}&pageSize=10`));
  useEffect(() => {
    if (state.error instanceof IdentityError && [401, 403, 404].includes(state.error.status)) retireUnavailableTenantChat();
  }, [state.error]);
  useEffect(() => {
    for (const item of state.data?.items ?? []) if (item.waiting && item.responseId) releaseDurableApprovalWait(session, item.responseId, item.runId);
  }, [session, state.data]);
  useEffect(() => {
    if (selected && state.data && !state.data.items.some(item => item.requestId === selected && item.canInspect)) setSelected(undefined);
  }, [selected, state.data]);
  const close = () => { setSelected(undefined); requestAnimationFrame(() => opener.current?.focus()); };
  if (!state.data?.items.length && state.error === undefined) return null;
  return <section aria-label="Conversation approvals"><Flex vertical gap="var(--ac-space-default)">
    <Flex align="center" wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Text strong>Conversation approvals</Typography.Text><Button size="small" icon={<ReloadOutlined aria-hidden />} onClick={state.reload}>Refresh approvals</Button></Flex>
    {state.error !== undefined && <Alert type="warning" showIcon title="Approval status could not be refreshed." description="The last known status may have changed. Refresh approvals to check current state." />}
    {state.data?.items.map(item => <Flex vertical align="start" gap="var(--ac-space-tight)" key={item.requestId}>
      <Typography.Text>{titles[item.operation] ?? 'Saved action'}</Typography.Text>
      <Flex wrap gap="var(--ac-space-tight)"><Tag color={item.status === 'pending' ? 'warning' : undefined}>{statuses[item.status] ?? 'Unavailable'}</Tag><Typography.Text type="secondary">{item.kind === 'exceptionalGrant' ? 'One-time authorization' : 'Review'}</Typography.Text>{item.outcome && <Tag>{result(item)}</Tag>}</Flex>
      <Typography.Text type="secondary">{item.status === 'pending' ? <>Expires {approvalDate(item.expiresAt)}</> : <>Requested {approvalDate(item.createdAt)}</>}</Typography.Text>
      <Typography.Text type="secondary">{item.status === 'pending' ? 'A qualified person must review this saved action. You can continue the conversation.' : item.status === 'approved' && !item.outcome ? 'Execution result not recorded yet. Approval does not mean the action has run.' : item.status !== 'approved' ? 'This request did not authorize the action.' : null}</Typography.Text>
      {item.canInspect ? <Typography.Link href={tenantApprovalPath(tenant, item.requestId)} onClick={event => { if (!event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) { event.preventDefault(); opener.current = event.currentTarget; setSelected(item.requestId); } }}>Review details</Typography.Link>
        : <Typography.Text type="secondary">Action details require separate approval and resource access.</Typography.Text>}
    </Flex>)}
    {!!state.data?.total && state.data.total > 10 && <Pagination size="small" current={page + 1} pageSize={10} total={state.data.total} showSizeChanger={false} onChange={next => setPage(next - 1)} />}
    {selected && <TenantApprovalDetail key={selected} tenant={tenant} organization={organization} id={selected} onChanged={() => { state.reload(); window.dispatchEvent(new Event('agentcore:approval-updated')); }} onClose={close} />}
  </Flex></section>;
}

export function TenantApprovalNotification({ tenant }: { tenant: string }) {
  const state = useApprovalUpdates<{ total: number }>(tenantApi(tenant, '/approvals?status=pending&pageSize=1'), 10000);
  const count = state.error === undefined ? state.data?.total ?? 0 : 0;
  return <Badge count={count} size="small"><Tooltip title={state.error !== undefined ? 'Approvals could not be refreshed. Open the inbox to retry.' : count ? `${count} pending requests available to you` : 'Approvals'}>
    <Button type="text" className="chat-header-icon-action" href={tenantApprovalPath(tenant)} aria-label={count ? `Approvals, ${count} pending` : 'Approvals'} icon={<CheckSquareOutlined />} />
  </Tooltip></Badge>;
}
