import { useEffect, useRef, useState } from 'react';
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Select, Table, Tag, Typography } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import { confirmAction } from '../../app/confirmAction';
import { navigateToAppPath } from '../../app/appRoute';
import { identityRequest, IdentityError, tenantApi, type TenantContext } from '../../services/identity';
import { adminCollectionPagination } from '../admin/AdminCollectionToolbar';
import { useAdminDetailLayout } from '../admin/useAdminDetailLayout';
import { savedAccountAction } from './AccountActionProposal';
import { AccountActionResult, type AccountActionRecordedResult } from './AccountActionResult';
import { AccountActionPreview } from './AccountActionPreview';
import { CompletionReadPreview, savedCompletionRead, savedReportSourceRead } from './CompletionReadPreview';
import { ArtifactPublicationPreview, savedArtifactPublication } from './ArtifactPublicationPreview';
import { WorkspaceMutationPreview, savedWorkspaceMutation } from './WorkspaceMutationPreview';
import { BrowserArtifactPublication, type BrowserArtifactResult } from './BrowserArtifactPublication';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantApprovalPath } from './accessRoute';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type Status = 'pending' | 'approved' | 'rejected' | 'cancelled' | 'expired';
export interface TenantApproval {
  destinationSessionId?: string | null; requestId: string; tenantId: string; runId: string | null; sessionId: string | null; rootActivationId: string;
  kind: 'review' | 'exceptionalGrant'; status: Status; requestRevision: number; securityRevision: number;
  summary: string; createdAt: string; expiresAt: string;
  preparedAction: { actionHash: string; operationId: string; operation?: string; expectedInstanceRevision: number; expectedPersonaRevision: number };
  decision: { principalId: string; status: Status; decidedAt: string } | null;
  operationResult: { outcome: string; reasonCode: string; recordedAt: string; browserArtifacts?: BrowserArtifactResult; accountAction?: AccountActionRecordedResult } | null;
  review: { agentName: string; requesterName: string | null; reviewerName: string | null;
    changes: { field: string; before: string | number | boolean | null; after: string | number | boolean | null; beforeAvailable: boolean; afterAvailable: boolean; restoresDefault: boolean }[];
    canDecide: boolean; canCancel: boolean; decisionUnavailableReason: string | null; independentReview: boolean; serverNow: string; executionState: string };
}
interface ApprovalPage { items: TenantApproval[]; total: number; page: number; pageSize: number }
const statuses: Record<Status, string> = { pending: 'Pending', approved: 'Approved', rejected: 'Rejected', cancelled: 'Cancelled', expired: 'Expired' };
const fields: Record<string, string> = { admissionOccurrence: 'Exact occurrence', admissionPrimaryPermission: 'Primary consent permission', admissionAutomation: 'Saved Automation', admissionSource: 'Occurrence source', admissionObjective: 'Exact task', admissionSchedule: 'Saved triggers', admissionDestination: 'Destination', admissionModel: 'Selected model', admissionScope: 'Required permissions', admissionLimit: 'Consent boundary', admissionEventSource: 'Captured event source', admissionEventFilter: 'Captured filter', admissionEventDispatch: 'Captured dispatch', admissionEventData: 'Captured event data', path: 'Exact path', name: 'Name', role: 'Role', description: 'Description', tone: 'Tone', responseLength: 'Response length', language: 'Language', askOneQuestionAtATime: 'Ask one question at a time', credential: 'Credential', bindingReference: 'Bound reference', connectionCheck: 'Connection check',
  reportDestination: 'Destination', reportContent: 'Completion to deliver', reportModel: 'Selected model',
  backgroundObjective: 'Task', backgroundTitle: 'Title', backgroundReportCompletion: 'Report completion here', backgroundModel: 'Selected model', backgroundPrimaryPermission: 'Primary creation permission',
  accountName: 'Account', accountPurpose: 'Account type', accountReference: 'Bound reference', accountReadTool: 'Requested read', accountReadArguments: 'Exact parameters',
  credentialMetadata: 'Requested read', readCredentials: 'Credential scope', readPage: 'Page',
  browserTool: 'Browser command', browserOrigin: 'Current origin', browserProfileMode: 'Browser state', browserArguments: 'Exact parameters', browserEffect: 'Requested action', browserLimit: 'Execution boundary',
  sandboxInspection: 'Stored read', readProfiles: 'Exact readable profiles', readDisclosure: 'Data disclosed', readLimit: 'Read boundary',
  sandboxControl: 'Control request', generation: 'Environment generation', execution: 'Exact execution', storedEnvironmentState: 'Stored environment state', storedExecutionState: 'Stored process state', cleanupConsequence: 'Cleanup behavior', fileRetention: 'File retention',
  sandboxProfile: 'Sandbox profile', image: 'Image', command: 'Command', workingDirectory: 'Working directory', timeoutSeconds: 'Timeout (seconds)', background: 'Run in background',
  homeAccess: 'Home access', cpu: 'CPU cores', memoryMiB: 'Memory (MiB)', storageBytes: 'Storage (bytes)', initializationRequired: 'Initialization required',
  profileInitialization: 'Profile initialization script', instanceInitialization: 'Instance initialization script',
  attachmentByteSize: 'Stored bytes', attachmentChecksum: 'File checksum (SHA-256)', attachmentDisplayName: 'File name', attachmentContentType: 'File type',
  artifactStoredByteSize: 'Stored bytes', artifactChecksum: 'File checksum (SHA-256)', artifactDisplayName: 'File name', artifactContentType: 'File type', artifactByteSize: 'UTF-8 bytes' };
const reasons: Record<string, string> = {
  account_action_completed: 'The exact saved account action completed. Draft creation, message delivery and file changes remain separate facts; this grants no later action or recipient read access.',
  account_action_not_applied: 'The saved account action was not applied. Check current account and source access before requesting new work.',
  account_action_indeterminate: 'The action may have run, or earlier batch changes may remain. This request will not run again. Inspect the current account state under separate read access.',
  account_draft_already_sent: 'This immutable draft already has a confirmed send receipt. This request sent no additional message.',
  account_action_required: 'Current Session, account, source-read and destination permissions are required independently.',
  account_draft_unavailable: 'The exact immutable draft or its account source is unavailable. Cancel this request and prepare a fresh action.',
  account_read_authorized: 'Consent was recorded for this exact account read. Current access is checked before data reaches the agent. This grants no send, write or later-query authority.',
  account_source_changed: 'The account binding, saved source or current access changed. Refresh details or prepare a new exact action.',
  account_read_required: 'Current Session, Credential and Email or Drive read permissions are required independently.',
  account_unavailable: 'This bound account is unavailable. Check its connection, host setup and current access.',
  account_read_expired: 'The saved account-read authorization expired. Start new work to request a fresh read.',
  account_result_too_large: 'The account result exceeded the read budget. Start a new, narrower read.',
  report_source_read_authorized: 'Consent was recorded for this exact report source. Report creation and delivery remain separate and still require current authority.',
  completion_read_authorized: 'Consent was recorded for this exact saved result. Current read permissions and reviewer authority still govern its use. Completion accounting remains separate.',
  completion_source_changed: 'The saved result, file metadata or current access changed. Refresh details before continuing.',
  completion_not_ready: 'The background task has not finished. Wait for its terminal result before requesting this exact read.',
  completion_report_created: 'One report Run was created in the original conversation. Delivery and recipient access remain separately checked.',
  completion_unavailable: 'This completion can no longer be delivered. The finished background result remains separate.',
  report_approval_required: 'This completion already has its own authorization request. Review that saved action.',
  background_created: 'One background Session and Run were created. Execution status and results remain in Background work under separate access.',
  background_unavailable: 'The current task or capability no longer permits background creation. Cancel this request and prepare a new action.',
  attachment_read_authorized: 'Consent was recorded for this exact saved file and read window. Current access and file integrity are checked before delivery. File changes, later reads and recipient download access remain separate.',
  attachment_source_changed: 'The saved file, processing version or window changed. Cancel this request and prepare a fresh exact read.',
  attachment_content_unavailable: 'The original file could not be verified. No file content was delivered. Restore the source before requesting a new read.',
  workspace_read_authorized: 'Consent was recorded for one exact saved workspace observation. It grants no file changes, recipient download access or later-read authority.',
  workspace_source_changed: 'The saved workspace source or target changed. Cancel this request and ask for a fresh exact action.',
  workspace_mutation_completed: 'The exact saved workspace changes completed. File read and recipient download access remain separate.',
  workspace_mutation_uncertain: 'Earlier or partial changes may remain. This operation will not run again. Inspect current state under separate file read authority before requesting another action.',
  workspace_mutation_not_applied: 'The saved workspace mutation was not applied.',
  home_read_only: 'Home is read-only under the current State & Isolation policy.',
  home_access_disabled: 'Home access is disabled by the current State & Isolation policy.',
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
const isReadOperation = (operation?: string) => operation === 'read_connected_account' || operation === 'inspect_background_report_source' || operation === 'inspect_background_completion' || operation === 'read_attachment' || operation === 'inspect_workspace' || operation === 'verify_artifact' || operation === 'inspect_credential_metadata' || operation === 'inspect_sandbox_profiles' || operation === 'inspect_sandbox_status' || operation === 'inspect_sandbox_process' || operation === 'sandbox_read';
export function approvalResultLabel(operation: string | undefined, outcome: string) {
  if (operation === 'use_connected_account') return outcome === 'completed' ? 'Account action completed' : outcome === 'denied' ? 'Account action not applied' : 'Account outcome uncertain';
  if (operation === 'deliver_background_completion') return outcome === 'completed' ? 'Report Run created' : outcome === 'denied' ? 'Report Run not created' : 'Report creation outcome unavailable';
  if (operation === 'start_background_work') return outcome === 'completed' ? 'Background work created' : outcome === 'denied' ? 'Background work not created' : 'Creation outcome unavailable';
  if (operation === 'mutate_workspace') return outcome === 'completed' ? 'Workspace changes completed' : outcome === 'denied' ? 'Workspace changes not applied' : 'Workspace outcome uncertain';
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
  const accountAction = data?.preparedAction.operation === 'use_connected_account';
  const account = accountAction && data ? savedAccountAction(data.review.changes) : null;
  const reportSourceRead = data?.preparedAction.operation === 'inspect_background_report_source';
  const reportSource = reportSourceRead && data?.sessionId ? savedReportSourceRead(data.review.changes, data.sessionId, data.destinationSessionId) : null;
  const completionRead = data?.preparedAction.operation === 'inspect_background_completion';
  const completion = completionRead && data?.sessionId ? savedCompletionRead(data.review.changes, data.sessionId) : null;
  const backgroundReport = data?.preparedAction.operation === 'deliver_background_completion';
  const automationAdmission = data?.preparedAction.operation === 'admit_automation_occurrence';
  const backgroundCreation = data?.preparedAction.operation === 'start_background_work';
  const workspaceMutation = data?.preparedAction.operation === 'mutate_workspace';
  const artifactPublication = data?.preparedAction.operation === 'publish_artifact';
  const publication = artifactPublication && data ? savedArtifactPublication(data.review.changes) : null;
  const mutationField = (name: string) => { const field = data?.review.changes.find(change => change.field === name); return field?.afterAvailable ? field.after : undefined; };
  const mutation = workspaceMutation ? savedWorkspaceMutation(mutationField('workspaceMutationTool'), mutationField('workspaceMutationArguments')) : null;
  const backgroundFields = ['backgroundObjective', 'backgroundTitle', 'backgroundReportCompletion', 'backgroundModel', 'backgroundPrimaryPermission'];
  const validBackground = data?.review.changes.length === backgroundFields.length
    && backgroundFields.every(field => data.review.changes.filter(change => change.field === field && change.afterAvailable).length === 1)
    && typeof mutationField('backgroundObjective') === 'string' && String(mutationField('backgroundObjective')).trim().length > 0 && String(mutationField('backgroundObjective')).length <= 4000
    && typeof mutationField('backgroundTitle') === 'string' && String(mutationField('backgroundTitle')).trim().length > 0 && String(mutationField('backgroundTitle')).length <= 80
    && typeof mutationField('backgroundReportCompletion') === 'boolean'
    && typeof mutationField('backgroundModel') === 'string' && String(mutationField('backgroundModel')).trim().length > 0
    && ['sessions.create', 'agent-runs.create'].includes(String(mutationField('backgroundPrimaryPermission')));
  const reportFields = ['reportDestination', 'reportContent', 'reportModel'];
  const validDestination = typeof data?.destinationSessionId === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(data.destinationSessionId);
  const validReport = data?.review.changes.length === reportFields.length
    && reportFields.every(field => data.review.changes.filter(change => change.field === field && change.afterAvailable).length === 1
      && typeof mutationField(field) === 'string' && String(mutationField(field)).trim().length > 0)
    && validDestination;
  const admissionFields = ['admissionOccurrence', 'admissionPrimaryPermission', 'admissionAutomation', 'admissionSource', 'admissionObjective', 'admissionSchedule', 'admissionDestination', 'admissionModel', 'admissionScope', 'admissionLimit'];
  const eventSource = mutationField('admissionSource');
  const rawPermission = eventSource === 'Built-in event · no human requester' ? 'core-events.read' : eventSource === 'Webhook event · no human requester' ? 'webhooks.read' : null;
  const requiredAdmissionFields = rawPermission ? [...admissionFields, 'admissionEventSource', 'admissionEventFilter', 'admissionEventDispatch', 'admissionEventData'] : admissionFields;
  const primaryAdmissionPermission = String(mutationField('admissionPrimaryPermission'));
  const emailScope = rawPermission === 'core-events.read'
    && mutationField('admissionScope') === 'automations.run; sessions.create; agent-runs.create; core-events.read; credentials.read; credentials.use; email.read'
    && emailAdmissionData(mutationField('admissionEventData'));
  const validAdmission = ['Scheduled occurrence · no human requester', 'Explicit manual invocation', 'Built-in event · no human requester', 'Webhook event · no human requester'].includes(String(eventSource)) && data?.sessionId === null && data.runId === null && data.review.changes.length === requiredAdmissionFields.length
    && ([...['automations.run', 'sessions.create', 'agent-runs.create'], ...(rawPermission && data.kind === 'review' ? [rawPermission, ...(emailScope ? ['credentials.read', 'credentials.use', 'email.read'] : [])] : [])]).includes(primaryAdmissionPermission)
    && (emailScope || mutationField('admissionScope') === `automations.run; sessions.create; agent-runs.create${rawPermission ? `; ${rawPermission}` : ''}`)
    && requiredAdmissionFields.every(field => data.review.changes.filter(change => change.field === field && change.afterAvailable && typeof change.after === 'string' && change.after.trim().length > 0).length === 1);
  const invalidPreview = (automationAdmission && !validAdmission) || (accountAction && !account) || (reportSourceRead && !reportSource) || (completionRead && !completion) || (workspaceMutation && !mutation) || (artifactPublication && !publication) || (backgroundCreation && !validBackground) || (backgroundReport && !validReport);
  const blocked = !fresh || loading || !!acknowledged || expired || busy || command.requiresReload;
  const decide = (decision: 'approve' | 'reject' | 'cancel') => {
    if (!data || blocked || (invalidPreview && decision !== 'cancel') || (decision === 'cancel' ? !data.review.canCancel : !data.review.canDecide)) return;
    const saved = data; const label = decision === 'approve' ? saved.kind === 'exceptionalGrant' ? 'Authorize once' : 'Approve' : decision === 'reject' ? 'Reject' : 'Cancel request';
    returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setConfirming(true);
    confirmation.current = confirmAction(modal, { title: `${label}?`, okText: label, cancelText: 'Keep reviewing', danger: decision !== 'approve',
      content: <Flex vertical gap="var(--ac-space-compact)"><Typography.Text>{saved.summary} for {saved.review.agentName} in {organization}.</Typography.Text><Typography.Text>{decision === 'approve' ? saved.preparedAction.operation === 'admit_automation_occurrence' ? 'Authorize admission of this one exact saved occurrence. Current registration, configuration, requester, executor and reviewer authority are checked again. This creates no work until settlement. An exceptional grant replaces only the named primary permission. Later tools need their own authority.' : saved.preparedAction.operation === 'use_connected_account' ? `${account?.title ?? 'Authorize the saved account action'} once using only the exact saved content and targets. ${account?.disclosure ?? ''} Raw account use, source reads and reviewer authority are checked again. No role or later action authority is granted.` : saved.preparedAction.operation === 'read_connected_account' ? 'Authorize only the saved bounded read from this exact account. This permits no send, write or later query; data delivery still requires current access.' : saved.preparedAction.operation === 'inspect_background_report_source' ? 'Approve reading only this exact saved terminal result for its requested report. This creates no report Run, grants no file reads and confirms no delivery. Report creation needs separate authority; current source and reviewer access are checked again.' : saved.preparedAction.operation === 'inspect_background_completion' ? 'Approve only this exact saved completion and related file metadata for this task. Current source, raw read permissions and reviewer authority are checked again. Taking the result does not mark it handled; a successful saved answer is still required.' : saved.preparedAction.operation === 'deliver_background_completion' ? 'Authorize one report Run for this exact saved completion in the original conversation. Current source, destination, configuration and access are checked again. This grants no role, source read, new Session or later-report authority. Creating the Run does not confirm delivery.' : saved.preparedAction.operation === 'start_background_work' ? 'Authorize one child Session and Run for this exact saved task. Both creation permissions and current configuration are checked again. A one-time grant replaces only the primary creation permission. No role, child read access or later-task authority is granted.' : saved.preparedAction.operation === 'mutate_workspace' ? 'Authorize only these exact saved workspace changes once. Current source and access are checked again. No role, file read, recipient download or later mutation authority is granted. Earlier or partial copy/batch effects may remain; uncertain work will not be repeated.' : saved.preparedAction.operation === 'read_attachment' ? 'Approve only this exact saved file and read window for this continuation. Current file integrity and access are checked again. No role, file changes, recipient download access or later-read authority is granted.' : saved.preparedAction.operation === 'inspect_workspace' ? 'Approve one exact saved workspace observation for this continuation. This grants no file changes, recipient download access or later-read authority.' : saved.preparedAction.operation === 'verify_artifact' ? 'Approve one exact metadata inspection of this saved file. This grants no file creation, contents, download access or later-read authority.' : saved.preparedAction.operation === 'publish_artifact' ? publication?.source ? 'Authorize one immutable copy of only the exact saved source bytes, before expiry. Current source and access are checked again. No role, source read or recipient download access is granted.' : `${saved.kind === 'exceptionalGrant' ? 'Authorize' : 'Approve'} publication of only this exact saved file once, before expiry. No role or file read access is granted. Execution checks current access again.` : saved.kind === 'exceptionalGrant' ? 'Authorize only the exact saved action before its expiry. No role is assigned. Execution checks current access again.' : isReadOperation(saved.preparedAction.operation) ? 'Approve this exact stored read once. Current access is checked before data is returned to the agent.' : isSandboxControl(saved) ? 'Approve this exact cleanup request. Writes may continue until termination is confirmed. Canonical files are retained.' : 'Approve only the exact saved action. Execution checks current access again.' : 'The saved action will not be authorized by this request.'}</Typography.Text></Flex>,
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
  const attachmentRead = data?.preparedAction.operation === 'read_attachment';
  const artifactVerification = data?.preparedAction.operation === 'verify_artifact';
  const artifactChecksum = data?.review.changes.find(change => change.field === 'artifactChecksum' || change.field === 'attachmentChecksum');
  const savedTitle = automationAdmission ? 'Exact saved admission' : accountAction ? 'Exact saved account action' : reportSourceRead ? 'Exact saved report source' : completionRead ? 'Exact saved background result' : backgroundReport ? 'Exact saved completion report' : backgroundCreation ? 'Exact saved background task' : artifactPublication ? 'Exact saved file' : workspaceMutation ? 'Exact saved workspace changes' : savedRead ? 'Exact saved read' : sandboxControl ? 'Exact saved control request' : sandboxCommand || browserCommand ? 'Exact saved command' : 'Exact saved changes';
  return <Drawer open title="Approval details" aria-label="Approval details" className="access-editor-drawer" size="large" styles={{ wrapper: { maxWidth: '100vw' } }} destroyOnHidden
    onClose={() => { if (!busy) onClose(); }} closable={!busy} mask={{ closable: !busy }} keyboard={!busy}
    footer={<Flex wrap gap="var(--ac-space-compact)" justify="end">
      <Button ref={refreshButton} onClick={reload} disabled={busy} loading={loading} icon={<ReloadOutlined aria-hidden />}>Refresh details</Button>
      {status === 'pending' && <><Button danger disabled={blocked || !data?.review.canCancel} onClick={() => decide('cancel')}>Cancel request</Button><Button danger disabled={blocked || invalidPreview || !data?.review.canDecide} onClick={() => decide('reject')}>Reject</Button><Button type="primary" disabled={blocked || invalidPreview || !data?.review.canDecide} onClick={() => decide('approve')}>{data?.kind === 'exceptionalGrant' ? 'Authorize once' : 'Approve'}</Button></>}
    </Flex>}>
    <Flex vertical gap="var(--ac-space-section)">
      {acknowledged && <Alert type="success" showIcon title={`${statuses[acknowledged]} decision recorded.`} description="The operation result is separate and may still be pending. Refresh details to check it." />}
      {command.error !== undefined && <AccessError error={command.error} retry={reload} />}
      {error !== undefined && <AccessError error={error} retry={reload} />}
      {!data ? loading ? <AccessLoading /> : null : <>
        <Flex vertical align="start" gap="var(--ac-space-compact)"><Typography.Text strong>{data.summary}</Typography.Text><Typography.Text>{data.review.agentName}</Typography.Text><Flex wrap gap="var(--ac-space-tight)"><StatusTag status={status!} /><Tag color={data.kind === 'exceptionalGrant' ? 'warning' : undefined}>{kindLabel(data.kind)}</Tag></Flex></Flex>
        <Alert type={data.kind === 'exceptionalGrant' ? 'warning' : 'info'} showIcon title={kindLabel(data.kind)} description={accountAction && data.kind === 'exceptionalGrant' ? 'The agent lacks the primary action permission. Authorize this exact action once; account use and source or workspace access remain independently required. No role is assigned.' : backgroundCreation && data.kind === 'exceptionalGrant' ? 'The agent lacks the primary creation permission shown below. Authorize this one task; the other creation permission remains independently required. No role or access to the new Session is granted.' : workspaceMutation && data.kind === 'exceptionalGrant' ? 'The agent lacks the primary mutation permission. Authorize only these changes once; independent source reads and secondary changes still require current permission. No role is assigned.' : data.kind === 'exceptionalGrant' ? 'This request was prepared because the agent lacked permission. Authorizing once permits only the action shown, before it expires. It does not assign a role.' : 'This request was prepared for review with agent permission. Your decision reviews this action only.'} />
        <Descriptions {...layout} column={1} size="small" items={[
          { key: 'organization', label: 'Organization', children: organization },
          { key: 'requester', label: 'Requested by', children: data.review.requesterName ?? 'No original person' },
          { key: 'created', label: 'Requested', children: approvalDate(data.createdAt) },
          { key: 'expiry', label: 'Expires', children: approvalDate(data.expiresAt) },
          ...(data.decision ? [{ key: 'reviewer', label: 'Decided by', children: data.review.reviewerName ?? 'Unavailable member' }, { key: 'decided', label: 'Decided', children: approvalDate(data.decision.decidedAt) }] : [])
        ]} />
        <section aria-label={savedTitle}><Typography.Title level={5}>{savedTitle}</Typography.Title>
          {accountAction ? <AccountActionPreview proposal={account} /> : reportSourceRead ? <CompletionReadPreview proposal={reportSource} /> : completionRead ? <CompletionReadPreview proposal={completion} /> : workspaceMutation ? <WorkspaceMutationPreview proposal={mutation} /> : artifactPublication ? <ArtifactPublicationPreview publication={publication} /> : automationAdmission || backgroundReport || backgroundCreation || browserCommand || sandboxCommand || sandboxControl || savedRead ? <Descriptions {...layout} column={1} size="small" items={data.review.changes.filter(change => change.field !== 'artifactChecksum' && change.field !== 'attachmentChecksum').map(change => ({ key: change.field, label: fields[change.field] ?? 'Saved field', children: change.afterAvailable ? value(change.after) : 'Not recorded for this request' }))} /> : <Flex vertical gap="var(--ac-space-section)">{data.review.changes.map(change => <Descriptions key={change.field} {...layout} title={fields[change.field] ?? 'Saved field'} column={1} size="small" items={[
            { key: 'before', label: 'Before', children: change.beforeAvailable ? value(change.before) : 'Not recorded for this request' },
            { key: 'after', label: 'After', children: <Flex vertical gap="var(--ac-space-tight)">{change.afterAvailable ? value(change.after) : 'Definition default was not recorded'}{change.restoresDefault && <Typography.Text type="secondary">Restore the Definition default</Typography.Text>}</Flex> }
          ]} />)}</Flex>}
        </section>
        {status === 'pending' && <>
          {data.review.independentReview && <Typography.Text type="secondary">Independent review is required. The original requester cannot decide.</Typography.Text>}
          {(expired || data.review.decisionUnavailableReason) && <Alert type="warning" showIcon title="Decision unavailable" description={expired ? reasons.approval_expired : reasons[data.review.decisionUnavailableReason!] ?? 'Current authority or configuration does not permit this decision. Refresh details or ask an owner.'} />}
          {!fresh && <Typography.Text role="status" type="secondary">Refresh details before deciding. A saved decision is never submitted again automatically.</Typography.Text>}
        </>}
        <section aria-label="Operation result"><Flex vertical gap="var(--ac-space-compact)"><Typography.Title level={5} style={{ margin: 0 }}>Operation result</Typography.Title>{data.operationResult ? <Alert showIcon type={data.operationResult.outcome === 'completed' ? 'success' : 'warning'} title={resultLabel(data)} description={<>{accountAction && data.operationResult.outcome === 'completed' && account?.tool === 'email.create_draft' ? 'The exact saved draft was created in this conversation. No message was sent. Sending requires separate consent.' : accountAction && data.operationResult.outcome === 'completed' && account?.tool === 'email.send' ? 'The provider confirmed acceptance of the exact saved draft. This is not proof that a recipient read the message.' : artifactPublication && data.operationResult.outcome === 'completed' ? 'The exact saved file was published. Viewing or downloading it requires separate current access.' : reasons[data.operationResult.reasonCode] ?? 'The exact operation result was recorded.'} {approvalDate(data.operationResult.recordedAt)}</>} /> : <Typography.Text type="secondary">{automationAdmission ? ({ admitted: 'One Background Session and Run were admitted. Execution and completion remain separate facts in Activity.', notAdmitted: 'This occurrence was not admitted. No Session or Run was created.', awaitingAdmission: 'Decision recorded. No admission result yet. Refresh details to check.', waitingDecision: 'Waiting for a human decision. No Session or Run exists for this occurrence.' }[data.review.executionState] ?? 'Admission state is unavailable. Refresh details.') : status === 'approved' ? 'No effect result recorded yet. Approval does not mean the action has run. Refresh details to check.' : status === 'pending' ? 'Waiting for a human decision.' : 'This request did not authorize the action.'}</Typography.Text>}{accountAction && data.operationResult && <AccountActionResult proposal={account} result={data.operationResult.accountAction} outcome={data.operationResult.outcome} />}</Flex></section>
        {browserCommand && data.sessionId && data.operationResult?.browserArtifacts && <BrowserArtifactPublication key={`${tenant}:${id}:${version}`} result={data.operationResult.browserArtifacts} tenantId={tenant} sessionId={data.sessionId} />}
        <Collapse items={[{ key: 'technical', label: 'Technical references', children: <Descriptions {...layout} column={1} size="small" items={[
          { key: 'request', label: 'Request', children: data.requestId }, { key: 'run', label: 'Source Run', children: data.runId ?? 'No source Run' }, { key: 'session', label: 'Session', children: data.sessionId ?? 'No source Session' }, { key: 'root', label: 'Execution root', children: data.rootActivationId },
          ...(browserCommand ? (data.operationResult?.browserArtifacts?.files ?? []).map((file, index) => ({ key: `browser-file-${index}`, label: file.kind === 'capture' ? 'Captured Artifact' : 'Downloaded Artifact', children: value(file.artifactId) })) : []),
          { key: 'hash', label: 'Exact action hash', children: data.preparedAction.actionHash },
          ...((attachmentRead || artifactVerification || publication?.source) && artifactChecksum?.afterAvailable ? [{ key: 'artifact-checksum', label: fields.artifactChecksum, children: value(artifactChecksum.after) }] : []), { key: 'revision', label: 'Request revision', children: data.requestRevision }, { key: 'access', label: 'Access revision', children: data.securityRevision }
        ]} /> }]} />
        {automationAdmission ? <Typography.Text type="secondary">Admission consent has no source conversation. Admitted work is available in Activity under separate current access.</Typography.Text> : (backgroundReport || reportSourceRead) && !validDestination ? <Typography.Text type="secondary">Destination is not recorded for this request.</Typography.Text>
          : <Typography.Link href={`/t/${tenant}/c/${backgroundReport || reportSourceRead ? data.destinationSessionId : data.sessionId}`}>{backgroundReport || reportSourceRead ? 'Open destination conversation' : 'Open original conversation'}</Typography.Link>}
      </>}
    </Flex>
  </Drawer>;
}
function value(content: string | number | boolean | null) { return <Typography.Text style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{typeof content === 'boolean' ? content ? 'Yes' : 'No' : content ?? 'Not set'}</Typography.Text>; }

function emailAdmissionData(value: unknown): boolean {
  if (typeof value !== 'string') return false;
  try {
    const data: unknown = JSON.parse(value);
    const sources = Array.isArray(data) ? data.map(entry => entry?.data) : [data];
    return sources.length > 0 && sources.length <= 24 && sources.every(source => source && typeof source === 'object'
      && Object.keys(source).sort().join(',') === 'accountId,accountRef,messageRef,provider'
      && typeof source.accountId === 'string' && /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/.test(source.accountId)
      && typeof source.accountRef === 'string' && /^[a-z0-9][a-z0-9-]{0,63}$/.test(source.accountRef)
      && typeof source.messageRef === 'string' && source.messageRef.length > 0 && source.messageRef.length <= 512 && !/[\u0000-\u001f\u007f-\u009f]/.test(source.messageRef)
      && ['Gmail', 'Custom'].includes(source.provider));
  } catch { return false; }
}
