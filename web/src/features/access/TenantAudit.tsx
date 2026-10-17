import { useState } from 'react';
import { Button, Descriptions, Drawer, Empty, Flex, Grid, Table, Tag, Typography } from 'antd';
import { ReloadOutlined, RightOutlined } from '@ant-design/icons';
import { tenantApi, type TenantContext } from '../../services/identity';
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

export function TenantAudit({ context }: { context: TenantContext }) {
  const state = useIdentityQuery<{ items: AuditEntry[] }>(tenantApi(context.tenant.tenantId, '/audit?limit=100'));
  const [expanded, setExpanded] = useState<string>(); const screens = Grid.useBreakpoint();
  const selected = state.data?.items.find(entry => entry.auditId === expanded);
  if (state.loading) return <AccessLoading />;
  if (state.error) return <AccessError error={state.error} retry={state.reload} />;
  return <Flex vertical gap="var(--ac-space-default)">
    <Flex justify="space-between" align="start" gap="var(--ac-space-compact)">
      <Typography.Text type="secondary">Up to 100 recent entries.</Typography.Text>
      <Button icon={<ReloadOutlined aria-hidden />} onClick={() => { setExpanded(undefined); state.reload(); }}>Refresh</Button>
    </Flex>
    <Table<AuditEntry> className="admin-collection-table" rowKey="auditId" dataSource={state.data?.items}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No audit entries yet." /> }}
      pagination={{ pageSize: 10, responsive: true, showSizeChanger: false, onChange: () => setExpanded(undefined) }}
      expandable={{ showExpandColumn: false, expandedRowKeys: screens.md && expanded ? [expanded] : [], expandedRowRender: entry => screens.md ? <Details entry={entry} /> : null }}
      columns={[
        { title: 'Activity', render: (_, entry) => <Flex vertical align="start" gap="var(--ac-space-tight)">
          <Button type="link" icon={<RightOutlined rotate={expanded === entry.auditId ? 90 : 0} />}
            aria-label={`${expanded === entry.auditId ? 'Hide' : 'View'} details: ${activities[entry.action] ?? 'Organization activity'}`}
            aria-expanded={expanded === entry.auditId} aria-controls={expanded === entry.auditId ? `audit-${entry.auditId}` : undefined}
            onClick={() => setExpanded(expanded === entry.auditId ? undefined : entry.auditId)}>{activities[entry.action] ?? 'Organization activity'}</Button>
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
          <Typography.Text strong>{activities[selected.action] ?? 'Organization activity'}</Typography.Text>
          <Flex><Result entry={selected} /></Flex>
          {reasons[selected.reasonCode] && <Typography.Text>{reasons[selected.reasonCode]}</Typography.Text>}
        </Flex>
        <Details entry={selected} />
      </Flex>}
    </Drawer>}
  </Flex>;
}
