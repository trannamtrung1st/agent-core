import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Select, Table, Tag, Typography } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import { confirmAction } from '../../app/confirmAction';
import { navigateToAppPath } from '../../app/appRoute';
import { identityRequest, IdentityError, tenantApi, type TenantContext } from '../../services/identity';
import { adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantApprovalPath } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type Status = 'pending' | 'approved' | 'rejected' | 'cancelled' | 'expired';
export interface TenantApproval {
  requestId: string; tenantId: string; runId: string; sessionId: string; rootActivationId: string;
  kind: 'review' | 'exceptionalGrant'; status: Status; requestRevision: number; securityRevision: number;
  summary: string; createdAt: string; expiresAt: string;
  preparedAction: { actionHash: string; operationId: string; operation?: string; expectedInstanceRevision: number; expectedPersonaRevision: number };
  decision: { principalId: string; status: Status; decidedAt: string } | null;
  operationResult: { outcome: string; reasonCode: string; recordedAt: string } | null;
  review: { agentName: string; requesterName: string | null; reviewerName: string | null;
    changes: { field: string; before: string | boolean | null; after: string | boolean | null; beforeAvailable: boolean; afterAvailable: boolean; restoresDefault: boolean }[];
    canDecide: boolean; canCancel: boolean; decisionUnavailableReason: string | null; independentReview: boolean; serverNow: string; executionState: string };
}
interface ApprovalPage { items: TenantApproval[]; total: number; page: number; pageSize: number }
const statuses: Record<Status, string> = { pending: 'Pending', approved: 'Approved', rejected: 'Rejected', cancelled: 'Cancelled', expired: 'Expired' };
const fields: Record<string, string> = { name: 'Name', role: 'Role', description: 'Description', tone: 'Tone', responseLength: 'Response length', language: 'Language', askOneQuestionAtATime: 'Ask one question at a time', credential: 'Credential', bindingReference: 'Bound reference', connectionCheck: 'Connection check' };
const reasons: Record<string, string> = {
  approval_expired: 'This request has expired. Ask the agent to prepare a new action.',
  executor_unavailable: 'The agent is unavailable.', run_unavailable: 'The source work can no longer accept this decision.',
  operation_witness_changed: 'The saved target or configuration changed. Ask the agent to prepare a new action.',
  operation_configuration_changed: 'The current configuration no longer supports this action.',
  requester_unavailable: 'The original requester no longer has the required access.',
  independent_reviewer_required: 'A different qualified person must review your request.',
  approver_unqualified: 'Your current permissions do not qualify you to decide this request.',
  no_eligible_approver: 'No qualified reviewer is currently available.',
  approval_authority_changed: 'Current authority no longer supports this saved request.',
  management_unavailable: 'The current agent management policy does not permit this change.',
  operation_completed: 'The exact change was applied.', operation_authority_changed: 'Authority changed before the action could be applied.',
  grantor_authority_lost: 'The approver no longer has the required authority.',
  executor_read_required: 'The agent no longer has the required credential read permission.',
  credential_binding_changed: 'The saved credential binding changed. Prepare a new action with the current binding.',
  credential_connection_checked: 'The bound connection check completed. This result does not authorize another credential use.',
  credential_reconnect_required: 'The check completed and the credential requires reconnection.',
  credential_check_in_progress: 'Another check already reserved this credential. This request did not run a provider check.',
  credential_check_uncertain: 'The provider outcome is uncertain. This operation will not run again. Reconnect the credential before another check.',
  credential_check_target_changed: 'The credential changed while the check was in progress. The newer credential was retained. This operation will not run again.'
};
function resultLabel(item: TenantApproval) {
  return item.operationResult?.outcome === 'completed' ? item.preparedAction.operation === 'check_credential_connection' ? 'Check completed' : 'Applied'
    : item.operationResult?.outcome === 'denied' ? 'Not applied' : 'Outcome uncertain';
}
export const approvalDate = (value: string) => <time dateTime={value}>{new Intl.DateTimeFormat(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', timeZoneName: 'short' }).format(new Date(value))}</time>;
function StatusTag({ status }: { status: Status }) { return <Tag color={status === 'pending' ? 'warning' : status === 'approved' ? 'success' : status === 'rejected' ? 'error' : undefined}>{statuses[status]}</Tag>; }
const kindLabel = (kind: TenantApproval['kind']) => kind === 'exceptionalGrant' ? 'One-time authorization' : 'Review';

export function TenantApprovals({ context, requestId }: { context: TenantContext; requestId?: string }) {
  const tenant = context.tenant.tenantId;
  const [status, setStatus] = useState('pending'); const [kind, setKind] = useState('all'); const [page, setPage] = useState(1); const [pageSize, setPageSize] = useState(20);
  const query = new URLSearchParams({ page: String(page - 1), pageSize: String(pageSize), ...(status !== 'all' ? { status } : {}), ...(kind !== 'all' ? { kind } : {}) });
  const state = useIdentityQuery<ApprovalPage>(tenantApi(tenant, `/approvals?${query}`));
  return <Flex vertical gap="var(--ac-space-default)">
    <Flex wrap justify="space-between" align="center" gap="var(--ac-space-compact)">
      <Flex wrap gap="var(--ac-space-compact)">
        <Select aria-label="Approval status" value={status} onChange={value => { setStatus(value); setPage(1); }} options={[{ value: 'all', label: 'All statuses' }, ...Object.entries(statuses).map(([value, label]) => ({ value, label }))]} />
        <Select aria-label="Approval type" value={kind} onChange={value => { setKind(value); setPage(1); }} options={[{ value: 'all', label: 'All types' }, { value: 'review', label: 'Review' }, { value: 'exceptionalGrant', label: 'One-time authorization' }]} />
      </Flex>
      <Button icon={<ReloadOutlined aria-hidden />} onClick={state.reload}>Refresh inbox</Button>
    </Flex>
    <Typography.Text type="secondary">Requests you can read and whose underlying action you can authorize. A decision and its execution result are recorded separately.</Typography.Text>
    {state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Table<TenantApproval> rowKey="requestId" className="admin-collection-table" dataSource={state.data?.items}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No matching requests available to you. Try another status or type." /> }}
      pagination={{ ...adminCollectionPagination, current: page, pageSize, total: state.data?.total, showTotal: total => `${total} visible requests`, onChange: (next, size) => { setPage(size === pageSize ? next : 1); setPageSize(size); } }}
      columns={[
        { title: 'Action', render: (_, item) => <Flex vertical align="start" gap="var(--ac-space-tight)">
          <Typography.Link href={tenantApprovalPath(tenant, item.requestId)} onClick={event => { if (!event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) { event.preventDefault(); navigateToAppPath(tenantApprovalPath(tenant, item.requestId)); } }}>{item.summary}</Typography.Link>
          <Typography.Text>{item.review.agentName}</Typography.Text><Typography.Text type="secondary">{kindLabel(item.kind)} · {approvalDate(item.createdAt)}</Typography.Text>
          <Flex wrap gap="var(--ac-space-tight)"><StatusTag status={item.status} />{item.operationResult && <Tag>{resultLabel(item)}</Tag>}</Flex>
        </Flex> },
        { title: 'Requested by', responsive: ['md'], render: (_, item) => item.review.requesterName ?? 'No original person' }
      ]} />}
    {requestId && <TenantApprovalDetail key={`${tenant}:${requestId}`} tenant={tenant} organization={context.tenant.name} id={requestId} onChanged={state.reload} onClose={() => navigateToAppPath(tenantApprovalPath(tenant))} />}
  </Flex>;
}

export function TenantApprovalDetail({ tenant, organization, id, onChanged, onClose }: { tenant: string; organization: string; id: string; onChanged: () => void; onClose: () => void }) {
  const { modal } = App.useApp(); const layout = useAdminDetailLayout(); const command = useTenantCommand();
  const [data, setData] = useState<TenantApproval>(); const [error, setError] = useState<unknown>(); const [fresh, setFresh] = useState(false);
  const [loading, setLoading] = useState(true); const [version, setVersion] = useState(0); const [expired, setExpired] = useState(false);
  const [acknowledged, setAcknowledged] = useState<Status>(); const [confirming, setConfirming] = useState(false);
  const active = useRef(true); const confirmation = useRef<ReturnType<typeof modal.confirm> | null>(null);
  const reload = () => { command.clearError(); setFresh(false); setVersion(value => value + 1); };
  useEffect(() => { active.current = true; return () => { active.current = false; confirmation.current?.destroy(); }; }, []);
  useEffect(() => {
    const abort = new AbortController(); setLoading(true); setFresh(false); setError(undefined);
    void identityRequest<TenantApproval>(tenantApi(tenant, `/approvals/${id}`), { signal: abort.signal }).then(result => {
      if (!abort.signal.aborted) { setData(result); setFresh(true); setLoading(false); }
    }).catch(failure => { if (!abort.signal.aborted) { if (failure instanceof IdentityError && failure.status < 500 && failure.status !== 409) setData(undefined); setError(failure); setLoading(false); } });
    return () => abort.abort();
  }, [tenant, id, version]);
  useEffect(() => {
    if (!data) return;
    const remaining = new Date(data.expiresAt).getTime() - new Date(data.review.serverNow).getTime();
    setExpired(remaining <= 0);
    if (remaining <= 0) return;
    const timer = window.setTimeout(() => setExpired(true), Math.min(remaining, 2147483647));
    return () => window.clearTimeout(timer);
  }, [data]);
  const busy = command.busy || confirming;
  const blocked = !fresh || loading || !!acknowledged || expired || busy || command.requiresReload;
  const decide = (decision: 'approve' | 'reject' | 'cancel') => {
    if (!data || blocked || (decision === 'cancel' ? !data.review.canCancel : !data.review.canDecide)) return;
    const saved = data; const label = decision === 'approve' ? saved.kind === 'exceptionalGrant' ? 'Authorize once' : 'Approve' : decision === 'reject' ? 'Reject' : 'Cancel request';
    setConfirming(true);
    confirmation.current = confirmAction(modal, { title: `${label}?`, okText: label, cancelText: 'Keep reviewing', danger: decision !== 'approve',
      content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text>{saved.summary} for {saved.review.agentName} in {organization}.</Typography.Text><Typography.Text>{decision === 'approve' ? saved.kind === 'exceptionalGrant' ? 'Authorize only the exact saved action before its expiry. No role is assigned. Execution checks current access again.' : 'Approve only the exact saved action. Execution checks current access again.' : 'The saved action will not be authorized by this request.'}</Typography.Text></Flex>,
      afterClose: () => { if (active.current) setConfirming(false); },
      onOk: () => command.run(async signal => {
        if (!active.current) throw new Error('Review closed.');
        setFresh(false);
        return identityRequest<{ status: Status }>(tenantApi(tenant, `/approvals/${id}/${decision === 'cancel' ? 'cancel' : 'decision'}`), { method: 'POST', signal,
          body: { ...(decision !== 'cancel' ? { decision } : {}), actionHash: saved.preparedAction.actionHash, expectedRevision: saved.securityRevision, expectedRequestRevision: saved.requestRevision } });
      }, result => { setAcknowledged(result.status); onChanged(); reload(); })
    });
  };
  const status = acknowledged ?? data?.status;
  return <Drawer open title="Approval details" aria-label="Approval details" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden
    onClose={() => { if (!busy) onClose(); }} closable={!busy} mask={{ closable: !busy }} keyboard={!busy}
    footer={<Flex wrap gap="var(--ac-space-compact)" justify="end">
      <Button onClick={reload} disabled={busy} loading={loading} icon={<ReloadOutlined aria-hidden />}>Refresh details</Button>
      {status === 'pending' && <><Button danger disabled={blocked || !data?.review.canCancel} onClick={() => decide('cancel')}>Cancel request</Button><Button danger disabled={blocked || !data?.review.canDecide} onClick={() => decide('reject')}>Reject</Button><Button type="primary" disabled={blocked || !data?.review.canDecide} onClick={() => decide('approve')}>{data?.kind === 'exceptionalGrant' ? 'Authorize once' : 'Approve'}</Button></>}
    </Flex>}>
    <Flex vertical gap="var(--ac-space-section)">
      {acknowledged && <Alert type="success" showIcon title={`${statuses[acknowledged]} decision recorded.`} description="The operation result is separate and may still be pending. Refresh details to check it." />}
      {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
      {error !== undefined && <AccessError error={error} retry={reload} />}
      {!data ? loading ? <AccessLoading /> : null : <>
        <Flex vertical align="start" gap="var(--ac-space-compact)"><Typography.Text strong>{data.summary}</Typography.Text><Typography.Text>{data.review.agentName}</Typography.Text><Flex wrap gap="var(--ac-space-tight)"><StatusTag status={status!} /><Tag color={data.kind === 'exceptionalGrant' ? 'warning' : undefined}>{kindLabel(data.kind)}</Tag></Flex></Flex>
        <Alert type={data.kind === 'exceptionalGrant' ? 'warning' : 'info'} showIcon title={kindLabel(data.kind)} description={data.kind === 'exceptionalGrant' ? 'This request was prepared because the agent lacked permission. Authorizing once permits only the action shown, before it expires. It does not assign a role.' : 'This request was prepared for review with agent permission. Your decision reviews this action only.'} />
        <Descriptions {...layout} column={1} size="small" items={[
          { key: 'organization', label: 'Organization', children: organization },
          { key: 'requester', label: 'Requested by', children: data.review.requesterName ?? 'No original person' },
          { key: 'created', label: 'Requested', children: approvalDate(data.createdAt) },
          { key: 'expiry', label: 'Expires', children: approvalDate(data.expiresAt) },
          ...(data.decision ? [{ key: 'reviewer', label: 'Decided by', children: data.review.reviewerName ?? 'Unavailable member' }, { key: 'decided', label: 'Decided', children: approvalDate(data.decision.decidedAt) }] : [])
        ]} />
        <section aria-label="Exact saved changes"><Typography.Title level={5}>Exact saved changes</Typography.Title>
          <Flex vertical gap="var(--ac-space-section)">{data.review.changes.map(change => <Descriptions key={change.field} {...layout} title={fields[change.field] ?? 'Saved field'} column={1} size="small" items={[
            { key: 'before', label: 'Before', children: change.beforeAvailable ? value(change.before) : 'Not recorded for this request' },
            { key: 'after', label: 'After', children: <Flex vertical gap="var(--ac-space-tight)">{change.afterAvailable ? value(change.after) : 'Definition default was not recorded'}{change.restoresDefault && <Typography.Text type="secondary">Restore the Definition default</Typography.Text>}</Flex> }
          ]} />)}</Flex>
        </section>
        {status === 'pending' && <>
          {data.review.independentReview && <Typography.Text type="secondary">Independent review is required. The original requester cannot decide.</Typography.Text>}
          {(expired || data.review.decisionUnavailableReason) && <Alert type="warning" showIcon title="Decision unavailable" description={expired ? reasons.approval_expired : reasons[data.review.decisionUnavailableReason!] ?? 'Current authority or configuration does not permit this decision. Refresh details or ask an owner.'} />}
          {!fresh && <Typography.Text role="status" type="secondary">Refresh details before deciding. A saved decision is never submitted again automatically.</Typography.Text>}
        </>}
        <section aria-label="Operation result"><Typography.Title level={5}>Operation result</Typography.Title>{data.operationResult ? <Alert showIcon type={data.operationResult.outcome === 'completed' ? 'success' : 'warning'} title={resultLabel(data)} description={<>{reasons[data.operationResult.reasonCode] ?? 'The exact operation result was recorded.'} {approvalDate(data.operationResult.recordedAt)}</>} /> : <Typography.Text type="secondary">{status === 'approved' ? 'No effect result recorded yet. Approval does not mean the action has run. Refresh details to check.' : status === 'pending' ? 'Waiting for a human decision.' : 'This request did not authorize the action.'}</Typography.Text>}</section>
        <Collapse items={[{ key: 'technical', label: 'Technical references', children: <Descriptions {...layout} column={1} size="small" items={[
          { key: 'request', label: 'Request', children: data.requestId }, { key: 'run', label: 'Source Run', children: data.runId }, { key: 'session', label: 'Session', children: data.sessionId }, { key: 'root', label: 'Execution root', children: data.rootActivationId },
          { key: 'hash', label: 'Exact action hash', children: data.preparedAction.actionHash }, { key: 'revision', label: 'Request revision', children: data.requestRevision }, { key: 'access', label: 'Access revision', children: data.securityRevision }
        ]} /> }]} />
        <Typography.Link href={`/t/${tenant}/c/${data.sessionId}`}>Open original conversation</Typography.Link>
      </>}
    </Flex>
  </Drawer>;
}
function value(content: string | boolean | null) { return <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{typeof content === 'boolean' ? content ? 'Yes' : 'No' : content ?? 'Not set'}</Typography.Text>; }
