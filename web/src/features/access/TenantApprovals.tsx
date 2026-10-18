import { useEffect, useId, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Input, Select, Table, Tag, Typography } from 'antd';
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
    changes: { field: string; before: string | number | boolean | null; after: string | number | boolean | null; beforeAvailable: boolean; afterAvailable: boolean; restoresDefault: boolean }[];
    canDecide: boolean; canCancel: boolean; decisionUnavailableReason: string | null; independentReview: boolean; serverNow: string; executionState: string };
}
interface ApprovalPage { items: TenantApproval[]; total: number; page: number; pageSize: number }
const statuses: Record<Status, string> = { pending: 'Pending', approved: 'Approved', rejected: 'Rejected', cancelled: 'Cancelled', expired: 'Expired' };
const fields: Record<string, string> = { name: 'Name', role: 'Role', description: 'Description', tone: 'Tone', responseLength: 'Response length', language: 'Language', askOneQuestionAtATime: 'Ask one question at a time', credential: 'Credential', bindingReference: 'Bound reference', connectionCheck: 'Connection check',
  credentialMetadata: 'Requested read', readCredentials: 'Credential scope', readPage: 'Page',
  browserTool: 'Browser command', browserOrigin: 'Current origin', browserProfileMode: 'Browser state', browserArguments: 'Exact parameters', browserEffect: 'Requested action', browserLimit: 'Execution boundary',
  sandboxInspection: 'Stored read', readProfiles: 'Exact readable profiles', readDisclosure: 'Data disclosed', readLimit: 'Read boundary',
  sandboxControl: 'Control request', generation: 'Environment generation', execution: 'Exact execution', storedEnvironmentState: 'Stored environment state', storedExecutionState: 'Stored process state', cleanupConsequence: 'Cleanup behavior', fileRetention: 'File retention',
  sandboxProfile: 'Sandbox profile', image: 'Image', command: 'Command', workingDirectory: 'Working directory', timeoutSeconds: 'Timeout (seconds)', background: 'Run in background',
  homeAccess: 'Home access', cpu: 'CPU cores', memoryMiB: 'Memory (MiB)', storageBytes: 'Storage (bytes)', initializationRequired: 'Initialization required',
  profileInitialization: 'Profile initialization script', instanceInitialization: 'Instance initialization script',
  artifactStoredByteSize: 'Stored bytes', artifactChecksum: 'File checksum (SHA-256)', artifactDisplayName: 'File name', artifactContentType: 'File type', artifactByteSize: 'UTF-8 bytes' };
const reasons: Record<string, string> = {
  artifact_metadata_read_authorized: 'The exact metadata inspection was authorized. File contents and download access require separate authority.',
  artifact_source_changed: 'The saved file or current access changed. Refresh details before continuing.',
  artifact_verification_review_required: 'A human must review this exact metadata inspection.',
  credential_metadata_read_authorized: 'This exact metadata page was authorized. Current access is checked before data is returned; no protected value or use permission is granted.',
  credential_source_changed: 'The Credential metadata source or current access changed. Refresh details or prepare a new request.',
  browser_command_completed: "The Browser command completed. Its effect in the application has not been independently verified.",
  browser_command_uncertain: "The Browser command may have run. Check the current application state before requesting another action. This request will not run again.",
  browser_command_not_dispatched: 'The Browser command was not dispatched. Resolve the current state before preparing a new request.',
  browser_source_changed: 'The Browser source or current authority changed before dispatch.',
  browser_target_changed: 'The Browser target changed. Cancel this request and prepare a new action for the current page.',
  browser_policy_changed: 'Current Browser capability or isolation policy no longer permits this action.',
  browser_target_busy: 'The Browser is busy. Refresh details when its current action finishes.',
  policy_transition_pending: 'The Browser isolation selection changed. Finish that transition before preparing a new action.',
  permission_denied: 'Current permissions no longer support this action.',
  provider_unavailable: 'The required provider is unavailable. Restore it, then prepare a new action.',
  tenant_effect_unavailable: 'Execution is not available for this action yet. Approval alone does not run it.',
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
  executor_read_required: 'The agent no longer has the required read permission.',
  credential_binding_changed: 'The saved credential binding changed. Prepare a new action with the current binding.',
  credential_connection_checked: 'The bound connection check completed. This result does not authorize another credential use.',
  credential_reconnect_required: 'The check completed and the credential requires reconnection.',
  credential_check_in_progress: 'Another check already reserved this credential. This request did not run a provider check.',
  credential_check_uncertain: 'The provider outcome is uncertain. This operation will not run again. Reconnect the credential before another check.',
  credential_check_target_changed: 'The credential changed while the check was in progress. The newer credential was retained. This operation will not run again.',
  sandbox_operation_witness_changed: 'The saved Sandbox target or configuration changed. Ask the agent to prepare a new action.',
  sandbox_read_authorized: 'The exact read was authorized. Current access still governs delivery of the stored observation to the agent.',
  sandbox_read_target_changed: 'The saved environment, receipt or selection changed. Ask the agent to prepare a new read.',
  sandbox_stop_requested: 'Stop request recorded. Process cleanup is pending; writes may continue until termination is confirmed. Canonical files are retained.',
  sandbox_reset_requested: 'Reset request recorded. Retirement is pending; writes may continue until it is confirmed. Canonical files are retained.',
  sandbox_stop_unavailable: 'The exact process is no longer available for Stop. Refresh its status before preparing another action.',
  sandbox_policy_changed: 'Current Sandbox capability or policy no longer permits this action.',
  sandbox_source_unavailable: 'The saved Sandbox image source changed or is unavailable. Restore the source and prepare a new command.',
  sandbox_recovery_required: 'The saved Sandbox environment requires recovery. Resolve its state before preparing a new command.',
  sandbox_profile_not_admitted: 'This Sandbox profile was not available to the source work. Prepare a new command in new work with an allowed profile.'
};
const isSandboxControl = (item?: TenantApproval) => item?.preparedAction.operation === 'stop_sandbox_process' || item?.preparedAction.operation === 'reset_sandbox_environment';
const isReadOperation = (operation?: string) => operation === 'verify_artifact' || operation === 'inspect_credential_metadata' || operation === 'inspect_sandbox_profiles' || operation === 'inspect_sandbox_status' || operation === 'inspect_sandbox_process' || operation === 'sandbox_read';
export function approvalResultLabel(operation: string | undefined, outcome: string) {
  if (operation === 'verify_artifact' && (outcome === 'completed' || outcome === 'denied')) return outcome === 'completed' ? 'Metadata inspection authorized' : 'Metadata not authorized';
  if (operation === 'publish_artifact' && (outcome === 'completed' || outcome === 'denied')) return outcome === 'completed' ? 'File published' : 'File not published';
  if (outcome === 'completed') return operation === 'browser_command' ? 'Command completed' : isReadOperation(operation) ? 'Read authorized' : operation === 'check_credential_connection' ? 'Check completed'
    : operation === 'sandbox_control' || operation === 'stop_sandbox_process' || operation === 'reset_sandbox_environment' ? 'Request recorded' : 'Applied';
  return outcome === 'denied' ? isReadOperation(operation) ? 'Read not authorized' : 'Not applied' : 'Outcome uncertain';
}
function resultLabel(item: TenantApproval) { return approvalResultLabel(item.preparedAction.operation, item.operationResult?.outcome ?? ''); }

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
  const returnFocus = useRef<HTMLElement | null>(null); const refreshButton = useRef<HTMLButtonElement>(null);
  const contentId = useId();
  useEffect(() => {
    if (!confirming && returnFocus.current) {
      const target = returnFocus.current; returnFocus.current = null;
      (target.isConnected && !target.matches(':disabled') ? target : refreshButton.current)?.focus();
    }
  }, [confirming]);
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
    returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setConfirming(true);
    confirmation.current = confirmAction(modal, { title: `${label}?`, okText: label, cancelText: 'Keep reviewing', danger: decision !== 'approve',
      content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text>{saved.summary} for {saved.review.agentName} in {organization}.</Typography.Text><Typography.Text>{decision === 'approve' ? saved.preparedAction.operation === 'verify_artifact' ? 'Approve one exact metadata inspection of this saved file. This grants no file creation, contents, download access or later-read authority.' : saved.preparedAction.operation === 'publish_artifact' ? `${saved.kind === 'exceptionalGrant' ? 'Authorize' : 'Approve'} publication of only this exact saved file once, before expiry. No role or file read access is granted. Execution checks current access again.` : saved.kind === 'exceptionalGrant' ? 'Authorize only the exact saved action before its expiry. No role is assigned. Execution checks current access again.' : isReadOperation(saved.preparedAction.operation) ? 'Approve this exact stored read once. Current access is checked before data is returned to the agent.' : isSandboxControl(saved) ? 'Approve this exact cleanup request. Writes may continue until termination is confirmed. Canonical files are retained.' : 'Approve only the exact saved action. Execution checks current access again.' : 'The saved action will not be authorized by this request.'}</Typography.Text></Flex>,
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
  const browserCommand = data?.preparedAction.operation === 'browser_command';
  const sandboxCommand = data?.preparedAction.operation === 'run_sandbox_command';
  const sandboxControl = isSandboxControl(data);
  const savedRead = isReadOperation(data?.preparedAction.operation);
  const artifactVerification = data?.preparedAction.operation === 'verify_artifact';
  const artifactChecksum = data?.review.changes.find(change => change.field === 'artifactChecksum');
  const artifactPublication = data?.preparedAction.operation === 'publish_artifact';
  const fileContent = data?.review.changes.find(change => change.field === 'artifactContent');
  const savedTitle = artifactPublication ? 'Exact saved file' : savedRead ? 'Exact saved read' : sandboxControl ? 'Exact saved control request' : sandboxCommand || browserCommand ? 'Exact saved command' : 'Exact saved changes';
  return <Drawer open title="Approval details" aria-label="Approval details" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden
    onClose={() => { if (!busy) onClose(); }} closable={!busy} mask={{ closable: !busy }} keyboard={!busy}
    footer={<Flex wrap gap="var(--ac-space-compact)" justify="end">
      <Button ref={refreshButton} onClick={reload} disabled={busy} loading={loading} icon={<ReloadOutlined aria-hidden />}>Refresh details</Button>
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
        <section aria-label={savedTitle}><Typography.Title level={5}>{savedTitle}</Typography.Title>
          {artifactPublication ? <Flex vertical gap="var(--ac-space-default)">
            <Descriptions {...layout} column={1} size="small" items={data.review.changes.filter(change => change.field !== 'artifactContent').map(change => ({ key: change.field, label: fields[change.field] ?? 'Saved field', children: change.afterAvailable ? value(change.after) : 'Not recorded for this request' }))} />
            <Flex vertical gap="var(--ac-space-compact)"><label htmlFor={contentId}>Exact file content</label>
              {fileContent?.afterAvailable && typeof fileContent.after === 'string' ? <Input.TextArea id={contentId} readOnly value={fileContent.after} autoSize={{ minRows: 6, maxRows: 12 }} /> : <Typography.Text>Content was not recorded for this request.</Typography.Text>}
              <Typography.Text type="secondary">Untrusted file content is shown as plain text. Publication grants no file read access.</Typography.Text>
            </Flex>
          </Flex> : browserCommand || sandboxCommand || sandboxControl || savedRead ? <Descriptions {...layout} column={1} size="small" items={data.review.changes.filter(change => change.field !== 'artifactChecksum').map(change => ({ key: change.field, label: fields[change.field] ?? 'Saved field', children: change.afterAvailable ? value(change.after) : 'Not recorded for this request' }))} /> : <Flex vertical gap="var(--ac-space-section)">{data.review.changes.map(change => <Descriptions key={change.field} {...layout} title={fields[change.field] ?? 'Saved field'} column={1} size="small" items={[
            { key: 'before', label: 'Before', children: change.beforeAvailable ? value(change.before) : 'Not recorded for this request' },
            { key: 'after', label: 'After', children: <Flex vertical gap="var(--ac-space-tight)">{change.afterAvailable ? value(change.after) : 'Definition default was not recorded'}{change.restoresDefault && <Typography.Text type="secondary">Restore the Definition default</Typography.Text>}</Flex> }
          ]} />)}</Flex>}
        </section>
        {status === 'pending' && <>
          {data.review.independentReview && <Typography.Text type="secondary">Independent review is required. The original requester cannot decide.</Typography.Text>}
          {(expired || data.review.decisionUnavailableReason) && <Alert type="warning" showIcon title="Decision unavailable" description={expired ? reasons.approval_expired : reasons[data.review.decisionUnavailableReason!] ?? 'Current authority or configuration does not permit this decision. Refresh details or ask an owner.'} />}
          {!fresh && <Typography.Text role="status" type="secondary">Refresh details before deciding. A saved decision is never submitted again automatically.</Typography.Text>}
        </>}
        <section aria-label="Operation result"><Typography.Title level={5}>Operation result</Typography.Title>{data.operationResult ? <Alert showIcon type={data.operationResult.outcome === 'completed' ? 'success' : 'warning'} title={resultLabel(data)} description={<>{artifactPublication && data.operationResult.outcome === 'completed' ? 'The exact saved file was published. Viewing or downloading it requires separate current access.' : reasons[data.operationResult.reasonCode] ?? 'The exact operation result was recorded.'} {approvalDate(data.operationResult.recordedAt)}</>} /> : <Typography.Text type="secondary">{status === 'approved' ? 'No effect result recorded yet. Approval does not mean the action has run. Refresh details to check.' : status === 'pending' ? 'Waiting for a human decision.' : 'This request did not authorize the action.'}</Typography.Text>}</section>
        <Collapse items={[{ key: 'technical', label: 'Technical references', children: <Descriptions {...layout} column={1} size="small" items={[
          { key: 'request', label: 'Request', children: data.requestId }, { key: 'run', label: 'Source Run', children: data.runId }, { key: 'session', label: 'Session', children: data.sessionId }, { key: 'root', label: 'Execution root', children: data.rootActivationId },
          { key: 'hash', label: 'Exact action hash', children: data.preparedAction.actionHash },
          ...(artifactVerification && artifactChecksum?.afterAvailable ? [{ key: 'artifact-checksum', label: fields.artifactChecksum, children: value(artifactChecksum.after) }] : []), { key: 'revision', label: 'Request revision', children: data.requestRevision }, { key: 'access', label: 'Access revision', children: data.securityRevision }
        ]} /> }]} />
        <Typography.Link href={`/t/${tenant}/c/${data.sessionId}`}>Open original conversation</Typography.Link>
      </>}
    </Flex>
  </Drawer>;
}
function value(content: string | number | boolean | null) { return <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{typeof content === 'boolean' ? content ? 'Yes' : 'No' : content ?? 'Not set'}</Typography.Text>; }
