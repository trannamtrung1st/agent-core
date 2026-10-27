import { Alert, App, Button, Descriptions, Flex, Tag, Typography } from 'antd';
import { followAppLink } from '../../app/appLink';
import { identityRequest, tenantApi } from '../../services/identity';
import { freshTenantRevision, outcomeLabel } from '../../services/tenantEvaluations';
import { tenantApprovalPath } from './accessRoute';
import { AccessError, AccessLoading } from './AccessFeedback';
import { useIdentityQuery } from './useIdentityQuery';
import { useTenantCommand } from './useTenantCommand';

type NativeEvidence = { executionId: string; revision: number; actorId: string; executorId: string; profileRevision: number; inputKind: number; cancellationRequestedAt: string | null; checkId: string; phase: string; approvalRequestId: string; profileId: string;
  script: { scriptId: string; version: number; contentHash: string }; inputHash: string; outcome: number | null;
  reasonCode: string | null; exitCode: number | null; durationMs: number | null; outputHash: string | null; cleanupPending: boolean; terminationConfirmed: boolean };
export function EvaluationSandboxEvidence({ tenant, attempt }: { tenant: string; attempt: string }) {
  const state = useIdentityQuery<NativeEvidence[]>(tenantApi(tenant, `/evaluation-attempts/${attempt}/sandbox-executions`));
  const command = useTenantCommand(); const { message } = App.useApp();
  return state.loading ? <AccessLoading /> : state.error ? <AccessError error={state.error} retry={state.reload} /> : <Flex vertical gap="var(--ac-space-section)">
    {command.error !== undefined && <AccessError error={command.error} retry={state.reload} />}
    {!state.data?.length && <Alert type="info" title="No native execution evidence is available." />}
    {state.data?.map(value => <section key={value.executionId} aria-label={`Sandbox execution for ${value.checkId}`}><Flex vertical gap="var(--ac-space-compact)">
      <Typography.Title level={4}>Sandbox · {value.checkId}</Typography.Title>
      <Flex wrap gap="var(--ac-space-compact)"><Tag>{value.phase === 'AwaitingReview' ? 'Awaiting exact Review' : value.phase === 'Dispatching' || value.phase === 'Reserved' ? 'Running' : value.reasonCode?.includes('cancelled') ? 'Cancelled' : 'Completed'}</Tag><Tag>{outcomeLabel(value.outcome)}</Tag></Flex>
      {value.phase === 'AwaitingReview' && <Typography.Paragraph type="secondary">Execution waits for exact operation Review. Human assessment permission alone does not authorize Sandbox execution.</Typography.Paragraph>}
      <Typography.Link href={tenantApprovalPath(tenant, value.approvalRequestId)} onClick={followAppLink}>Open exact Sandbox Review</Typography.Link>
      {(value.phase === 'Reserved' || value.phase === 'Dispatching') && <Flex vertical gap="var(--ac-space-compact)">
        <Button danger loading={command.busy} disabled={!!value.cancellationRequestedAt || command.requiresReload} onClick={() => void command.run(async signal => identityRequest<NativeEvidence[]>(tenantApi(tenant, `/evaluation-attempts/${attempt}/cancel-sandbox`), { method: 'POST', signal,
          body: { executionId: value.executionId, expectedExecutionRevision: value.revision, expectedRevision: await freshTenantRevision(tenant, signal) } }), result => { state.reload(); void message.info(result.find(item => item.executionId === value.executionId)?.phase === 'Settled' ? 'Execution already settled. Its saved outcome was retained.' : 'Cancellation requested. Refresh to confirm termination.'); })}>{value.cancellationRequestedAt ? 'Cancellation requested' : 'Cancel Sandbox execution'}</Button>
        <Typography.Text type="secondary">The original requester or an authorized cancellation reviewer can stop this execution. Cancellation is confirmed only after the environment is retired.</Typography.Text>
      </Flex>}
      {value.reasonCode === 'sandbox_evaluation_interrupted' && <Alert type="warning" showIcon title="Interrupted execution · Inconclusive" description="Recovery retires the original environment without replaying its command. A deliberate new evaluation needs a new exact Review." />}
      {value.cleanupPending && value.phase === 'Settled' && <Alert type="warning" showIcon title="Sandbox cleanup is pending" description="Durable recovery will retire this exact environment. Refresh its saved status; do not submit another execution to recover it." />}
      {value.reasonCode && <Typography.Text type="secondary">{value.reasonCode.replaceAll('_', ' ')}</Typography.Text>}
      <Descriptions column={1} size="small" items={[
        { key: 'execution', label: 'Execution', children: <Typography.Text copyable style={{ overflowWrap: 'anywhere' }}>{value.executionId}</Typography.Text> },
        { key: 'profile', label: 'Profile', children: `${value.profileId} · revision ${value.profileRevision}` },
        { key: 'script', label: 'Exact Script', children: `Version ${value.script.version} · ${value.script.scriptId}` },
        { key: 'hash', label: 'Script hash', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{value.script.contentHash}</Typography.Text> },
        { key: 'actor', label: 'Requester', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{value.actorId}</Typography.Text> },
        { key: 'executor', label: 'Independent executor', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{value.executorId}</Typography.Text> },
        { key: 'inputKind', label: 'Permitted input', children: ['Run metadata', 'Saved Run output', 'Admitted task input'][value.inputKind] },
        { key: 'input', label: 'Input digest', children: <Typography.Text style={{ overflowWrap: 'anywhere' }}>{value.inputHash}</Typography.Text> },
        { key: 'exit', label: 'Exit code', children: value.exitCode ?? 'Not confirmed' },
        { key: 'duration', label: 'Duration', children: value.durationMs === null ? 'Not recorded' : `${value.durationMs} ms` },
        { key: 'termination', label: 'Termination', children: value.terminationConfirmed ? 'Confirmed' : 'Not confirmed' },
        { key: 'output', label: 'Bounded output digest', children: value.outputHash && <Typography.Text style={{ overflowWrap: 'anywhere' }}>{value.outputHash}</Typography.Text> }
      ]} />
      <Typography.Paragraph type="secondary">Inputs are read-only at /home/input.json. Scratch /working is disposable. Original workspaces and credentials are unavailable, and egress is denied. Only the saved assertion is assessed.</Typography.Paragraph>
    </Flex></section>)}
  </Flex>;
}
