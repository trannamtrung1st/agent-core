import { useState } from 'react';
import { Button, Descriptions, Drawer, Empty, Flex, Form, Grid, Input, Select, Table, Tag, Typography } from 'antd';
import { ReloadOutlined, RightOutlined } from '@ant-design/icons';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';

interface AuditEntry {
  auditId: string; actorPrincipalId: string; action: string; target: string;
  decision: string; reasonCode: string; at: string;
  execution?: {
    context: { agentPrincipalId: string; originalInitiatorPrincipalId?: string | null; sessionId: string; runId: string;
      activationId: string; rootActivationId: string; parentRunId?: string | null; parentActivationId?: string | null; origin: string };
    runRevision: number; securityRevision: number; claimGeneration?: string | null; permission?: string | null;
    resource: { resourceType?: string | null; resourceId?: string | null }; approvalKind?: string | null; purpose: string;
    approvalRequestId?: string | null; operationId?: string | null; grantId?: string | null;
  } | null;
}

const activities: Record<string, string> = {
  'identity.bootstrap': 'Platform initialized', 'account.password.change': 'Account password changed',
  'system.accounts.status': 'Account status changed', 'system.browser.privacy.update': 'Browser privacy saved',
  'iam.roles.assign': 'Role assigned', 'iam.roles.revoke': 'Role revoked', 'iam.roles.manage': 'Role saved',
  'members.invite': 'Member invited', 'members.accept_invitation': 'Invitation accepted',
  'members.status': 'Membership status changed', 'members.revoke_invitation': 'Invitation revoked',
  'agents.create': 'Agent created', 'sessions.create': 'Conversation created', 'agent-runs.admit': 'Agent run created',
  'authorization.operation': 'Agent authorization checked', 'operations.preferences.apply': 'Conversation preference change',
  'operations.persona.apply': 'Agent persona change', 'approvals.request': 'Approval requested',
  'operations.credentials.check': 'Credential connection check', 'operations.credentials.check.dispatch': 'Credential check dispatched',
  'approvals.decide': 'Approval decision recorded', 'agent-runs.approval-continuation': 'Work resumed after review'
};
const purposes: Record<string, string> = { Inspection: 'Permission inspection', ApprovalPreparation: 'Approval request check',
  ApprovalDecision: 'Approval decision check', EffectEligibility: 'Effect eligibility check', ToolBoundary: 'Tool boundary check', EffectResult: 'Recorded effect result' };
const origins: Record<string, string> = { UserMessage: 'User message', ApprovalContinuation: 'Approval continuation',
  ScheduledOccurrence: 'Scheduled occurrence', ApplicationEvent: 'Application event', CoreEvent: 'Core event', ImmediateBackground: 'Background continuation',
  ConfigurationContinuation: 'Configuration continuation', CompletionReport: 'Completion report', ManualInvocation: 'Manual invocation', Initiative: 'Initiative' };
const reasons: Record<string, string> = {
  allowed: 'Current permissions allow this operation.', review_required: 'Human review is required.',
  agent_missing_delegatable_permission: 'The agent needs authorization for this exact operation.',
  human_only: 'This operation is reserved for a person.', permission_denied: 'The required permission is missing.',
  no_eligible_approver: 'No qualified approver is available.', resource_unavailable: 'The requested resource is unavailable.',
  executor_unavailable: 'The executing agent is unavailable.', executor_read_required: 'The agent needs current read access to the target.', requester_unavailable: 'The original requester no longer has access.',
  tenant_effect_unavailable: 'This operation is unavailable for organization work.',
  operation_completed: 'The exact change was applied.', operation_witness_changed: 'The target changed before the operation could be applied.',
  operation_authority_changed: 'Authority changed before the operation could be applied.', grantor_authority_lost: 'The approver no longer has the required authority.',
  management_unavailable: 'The current management policy does not permit this change.',
  approval_rate_limited: 'Too many approval requests. No new request was created.',
  approval_root_limit: 'This execution root reached its approval request limit.',
  previously_rejected: 'Already rejected in this execution root.', previously_expired: 'Already expired in this execution root.',
  previously_cancelled: 'Already cancelled in this execution root.', already_pending: 'An approval request is already pending.',
  already_approved: 'This operation was already approved in this execution root.',
  approval_approved: 'The person approved the exact operation.', approval_rejected: 'The person rejected the operation.',
  approval_cancelled: 'The approval request was cancelled.', root_operation_conflict: 'An earlier decision in this execution root prevented the change.',
  effective_permissions_inspected: 'The agent inspected its current permissions.', stale_revision: 'State changed while authorization was being checked.',
  credential_check_dispatched: 'The exact connection check reserved the credential for one provider call.',
  credential_connection_checked: 'The bound connection check completed.', credential_reconnect_required: 'The check completed and the credential requires reconnection.',
  credential_check_uncertain: 'The provider outcome is uncertain. This operation will not run again; the credential requires reconnection.',
  credential_check_target_changed: 'The credential changed during the check. Its newer grant was retained and this operation will not run again.',
  credential_check_in_progress: 'Another check had reserved this credential. This operation did not call the provider.'
};

function Result({ entry }: { entry: AuditEntry }) {
  if (entry.reasonCode === 'approval_rejected') return <Tag color="error">Rejected</Tag>;
  if (entry.reasonCode === 'approval_approved') return <Tag color="success">Approved</Tag>;
  if (entry.reasonCode === 'approval_cancelled') return <Tag>Cancelled</Tag>;
  if (entry.execution?.purpose === 'EffectResult' && entry.decision === 'indeterminate') return <Tag color="warning">Outcome uncertain</Tag>;
  if (entry.decision === 'denied') return <Tag color="error">{entry.execution?.purpose === 'EffectResult' ? 'Not applied' : 'Denied'}</Tag>;
  if (entry.decision === 'requiresApproval') return <Tag color="warning">Approval required</Tag>;
  if (entry.execution?.purpose === 'EffectResult' && entry.decision === 'allowed') return <Tag color="success">{entry.action === 'operations.credentials.check' ? 'Check completed' : 'Applied'}</Tag>;
  return entry.action === 'authorization.operation' && entry.decision === 'allowed' ? <Tag color="success">Allowed</Tag> : <Tag>Recorded</Tag>;
}

function Details({ entry }: { entry: AuditEntry }) {
  const layout = useAdminDetailLayout(); const e = entry.execution;
  const reference = (value?: string | null) => value ? <Typography.Text copyable>{value}</Typography.Text> : '—';
  const items = [
    { key: 'actor', label: 'Actor principal', children: reference(entry.actorPrincipalId) },
    { key: 'action', label: 'Action', children: <Typography.Text code>{entry.action}</Typography.Text> },
    { key: 'reason', label: 'Reason code', children: <Typography.Text code>{entry.reasonCode}</Typography.Text> },
    { key: 'target', label: 'Target', children: reference(entry.target) },
    ...(e ? [
      { key: 'purpose', label: 'Check or effect', children: purposes[e.purpose] ?? e.purpose },
      { key: 'origin', label: 'Origin', children: origins[e.context.origin] ?? e.context.origin },
      { key: 'permission', label: 'Permission', children: e.permission ?? 'Permission inventory or boundary check' },
      { key: 'run', label: 'Agent run', children: reference(e.context.runId) },
      { key: 'root', label: 'Root activation', children: reference(e.context.rootActivationId) },
      { key: 'requester', label: 'Original requester', children: e.context.originalInitiatorPrincipalId ? reference(e.context.originalInitiatorPrincipalId) : 'No original person' },
      { key: 'session', label: 'Session', children: reference(e.context.sessionId) },
      ...(e.context.parentRunId ? [{ key: 'parent', label: 'Parent run', children: reference(e.context.parentRunId) }] : []),
      { key: 'runRevision', label: 'Observed run revision', children: e.runRevision },
      { key: 'securityRevision', label: 'Observed access revision', children: e.securityRevision },
      ...(e.approvalRequestId ? [{ key: 'approval', label: 'Approval request', children: reference(e.approvalRequestId) }] : []),
      ...(e.operationId ? [{ key: 'operation', label: 'Exact operation', children: reference(e.operationId) }] : []),
      ...(e.grantId ? [{ key: 'grant', label: 'One-time grant', children: reference(e.grantId) }] : [])
    ] : [])
  ];
  return <div id={`audit-${entry.auditId}`} role="region" aria-label="Audit entry details">
    {entry.action === 'authorization.operation' && <Typography.Paragraph type="secondary">This decision describes access at that time. Later work is checked again; applied changes have their own audit entries.</Typography.Paragraph>}
    <Descriptions {...layout} column={1} size="small" items={items} />
  </div>;
}

interface AuditFilters { action?: string; decision?: string; actorPrincipalId?: string; from?: string; through?: string }
interface AuditPage { items: AuditEntry[]; total: number; page: number; pageSize: number }

export function AuthorizationAuditCollection({ endpoint, scope }: { endpoint: string; scope: 'organization' | 'platform' }) {
  const [form] = Form.useForm<AuditFilters>();
  const [filters, setFilters] = useState<AuditFilters>({});
  const [page, setPage] = useState(0); const [pageSize, setPageSize] = useState(30);
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  for (const [key, raw] of Object.entries(filters)) {
    const value = raw?.trim(); if (value) query.set(key, key === 'from' || key === 'through' ? new Date(value).toISOString() : value);
  }
  const state = useIdentityQuery<AuditPage>(endpoint + '?' + query);

  const [expanded, setExpanded] = useState<string>(); const screens = Grid.useBreakpoint();
  if (state.loading) return <AccessLoading />;
  if (state.error) return <AccessError error={state.error} retry={state.reload} />;
  const data = state.data;
  if (!data || !Array.isArray(data.items) || !Number.isInteger(data.total) || data.total < data.items.length
      || data.page !== page || data.pageSize !== pageSize || data.items.length > pageSize
      || data.items.some(entry => !entry || ['auditId', 'actorPrincipalId', 'action', 'target', 'decision', 'reasonCode', 'at'].some(key => typeof entry[key as keyof AuditEntry] !== 'string')))
    return <AccessError error={new Error('Audit results are unavailable. Reload to read the current page.')} retry={state.reload} />;
  const selected = data.items.find(entry => entry.auditId === expanded);
  const activityFallback = scope === 'platform' ? 'Platform activity' : 'Organization activity';
  const apply = (values: AuditFilters) => { setExpanded(undefined); setPage(0); setFilters(values); };
  const dateRule = { validator: (_: unknown, value?: string) => !value || !Number.isNaN(new Date(value).getTime())
    ? Promise.resolve() : Promise.reject(new Error('Choose a valid local date and time.')) };
  return <Flex vertical gap="var(--ac-space-default)">
    {scope === 'platform' && <Typography.Paragraph type="secondary">Platform bootstrap, Account security and Browser privacy records. Organization activity requires access to that organization's Audit page.</Typography.Paragraph>}
    <Form form={form} layout="vertical" className="access-bounded-form access-collection-filters" onFinish={apply}>
      <Flex wrap gap="var(--ac-space-default)">
        <Form.Item name="action" label="Activity code" rules={[{ pattern: /^[a-zA-Z0-9_.-]{1,128}$/, message: 'Enter an exact activity code using letters, numbers, dots, dashes or underscores.' }]}>
          <Input allowClear maxLength={128} placeholder={scope === 'platform' ? 'For example, system.browser.privacy.update' : 'For example, approvals.request'} />
        </Form.Item>
        <Form.Item name="decision" label="Result"><Select allowClear placeholder="All results" options={[
          { value: 'allowed', label: 'Allowed' }, { value: 'denied', label: 'Denied' },
          { value: 'requiresApproval', label: 'Approval required' }, { value: 'indeterminate', label: 'Outcome uncertain' }
        ]} /></Form.Item>
        <Form.Item name="actorPrincipalId" label="Actor principal" rules={[{ validator: (_, value?: string) => !value
          || /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) && value !== '00000000-0000-0000-0000-000000000000'
            ? Promise.resolve() : Promise.reject(new Error('Enter a complete principal ID.')) }]}><Input allowClear placeholder="Exact principal ID" /></Form.Item>
        <Form.Item name="from" label="From (local time)" rules={[dateRule]}><Input type="datetime-local" step={1} /></Form.Item>
        <Form.Item name="through" label="Through (local time)" dependencies={['from']} rules={[dateRule, ({ getFieldValue }) => ({ validator: (_, value?: string) =>
          !value || !getFieldValue('from') || new Date(value) >= new Date(getFieldValue('from')) ? Promise.resolve() : Promise.reject(new Error('Choose a time on or after From.')) })]}><Input type="datetime-local" step={1} /></Form.Item>
      </Flex>
      <Flex wrap gap="var(--ac-space-compact)"><Button type="primary" htmlType="submit">Apply filters</Button><Button onClick={() => { form.resetFields(); apply({}); }}>Clear filters</Button></Flex>
    </Form>
    <Flex justify="space-between" align="start" gap="var(--ac-space-compact)">
      <Typography.Text type="secondary">{data.total} {data.total === 1 ? 'entry' : 'entries'}</Typography.Text>
      <Button icon={<ReloadOutlined aria-hidden />} onClick={() => { setExpanded(undefined); state.reload(); }}>Refresh</Button>
    </Flex>
    <Table<AuditEntry> className="admin-collection-table" rowKey="auditId" dataSource={data.items}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No audit entries yet." /> }}
      pagination={{ current: page + 1, pageSize, total: data.total, responsive: true, showSizeChanger: true, pageSizeOptions: [10, 30, 50, 100], onChange: (next, size) => { setExpanded(undefined); setPage(size === pageSize ? next - 1 : 0); setPageSize(size); } }}
      expandable={{ showExpandColumn: false, expandedRowKeys: screens.md && expanded ? [expanded] : [], expandedRowRender: entry => screens.md ? <Details entry={entry} /> : null }}
      columns={[
        { title: 'Activity', render: (_, entry) => <Flex vertical align="start" gap="var(--ac-space-tight)">
          <Button type="link" icon={<RightOutlined rotate={expanded === entry.auditId ? 90 : 0} />}
            aria-label={`${expanded === entry.auditId ? 'Hide' : 'View'} details: ${activities[entry.action] ?? activityFallback}`}
            aria-expanded={expanded === entry.auditId} aria-controls={expanded === entry.auditId ? `audit-${entry.auditId}` : undefined}
            onClick={() => setExpanded(expanded === entry.auditId ? undefined : entry.auditId)}>{activities[entry.action] ?? activityFallback}</Button>
          {reasons[entry.reasonCode] && <Typography.Text>{reasons[entry.reasonCode]}</Typography.Text>}
          <Typography.Text type="secondary"><time dateTime={entry.at}>{new Date(entry.at).toLocaleString()}</time></Typography.Text>
          {!screens.sm && <Result entry={entry} />}
        </Flex> },
        { title: 'Result', width: 160, responsive: ['sm'], render: (_, entry) => <Result entry={entry} /> }
      ]} />
    {!screens.md && <Drawer className="access-editor-drawer" title="Audit details" open={!!selected} onClose={() => setExpanded(undefined)}
      size="large" styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden>
      {selected && <Flex vertical gap="var(--ac-space-section)">
        <Flex vertical gap="var(--ac-space-compact)">
          <Typography.Text strong>{activities[selected.action] ?? activityFallback}</Typography.Text>
          <Flex><Result entry={selected} /></Flex>
          {reasons[selected.reasonCode] && <Typography.Text>{reasons[selected.reasonCode]}</Typography.Text>}
        </Flex>
        <Details entry={selected} />
      </Flex>}
    </Drawer>}
  </Flex>;
}
