import { useEffect, useState } from 'react';
import { Alert, Button, Collapse, Empty, Flex, Pagination, Table, Tag, Typography } from 'antd';
import { IdentityError } from '../../services/identity';
import { followAppLink } from '../../app/appLink';
import { tenantAutomationsApi, type TenantAutomationEventActivity as EventItem, type TenantAutomationEventActivityPage } from '../../services/tenantAutomations';
import { AccessError, AccessLoading } from './AccessFeedback';
import { tenantAgentPath, tenantApprovalPath } from './accessRoute';
import { SavedApprovalText } from './SavedApprovalText';
import { useIdentityQuery } from './useIdentityQuery';

const statuses: Record<string, string> = { received: 'Received', matched: 'Matched', notMatched: 'Filter did not match', invalidFilter: 'Filter invalid', coalesced: 'Coalescing', coalescing: 'Coalescing', pending: 'Received', filtered: 'Filter did not match', filterError: 'Filter error', awaitingAdmission: 'Waiting for admission', admitted: 'Admitted', policySkipped: 'Not admitted', failed: 'Failed' };
export function TenantAutomationEventActivity({ tenant, instance, automation }: { tenant: string; instance: string; automation: string }) {
  const [page, setPage] = useState(0);
  const path = tenantAutomationsApi(tenant, instance, `/${automation}/events?page=${page}&pageSize=10`);
  const state = useIdentityQuery<TenantAutomationEventActivityPage>(path);
  const [retained, setRetained] = useState<{ path: string; view: TenantAutomationEventActivityPage }>();
  const readLost = state.error instanceof IdentityError && [401, 403, 404].includes(state.error.status);
  const transient = !!state.error && (!(state.error instanceof IdentityError) || state.error.status >= 500);
  useEffect(() => {
    if (readLost) setRetained(undefined);
    else if (state.data) setRetained({ path, view: { ...state.data, items: state.data.items.map(row => ({ ...row,
      data: null, bucketId: null, batchCount: null, occurrenceId: null, runId: null, sessionId: null, approvalRequestId: null })) } });
  }, [path, state.data, readLost]);
  const stale = !state.data && !readLost && retained?.path === path && (state.loading || transient);
  const view = state.data ?? (stale ? retained?.view : undefined);
  return <Flex vertical gap="var(--ac-space-section)">
    <Flex wrap justify="space-between" gap="var(--ac-space-compact)"><Typography.Text strong>Received event Activity</Typography.Text><Button onClick={state.reload} disabled={state.loading}>Reload event Activity</Button></Flex>
    <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Only events you can currently read are shown. Receipt, matching, admission, and completion are separate steps.</Typography.Paragraph>
    {stale && <Alert type="warning" showIcon title="Showing the last successful Activity read" description="These facts may be out of date. Reload to check the current outcome. Received data and links stay hidden until a successful read." />}
    {state.error && <AccessError error={state.error} retry={state.reload} />}
    {state.loading && !view && <AccessLoading />}
    {view && <>
      <Table<EventItem> size="small" tableLayout="fixed" pagination={false} rowKey={row => `${row.eventId}:${row.triggerId}`} dataSource={view.items}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No readable received events for this Automation." /> }} columns={[{ title: 'Event and outcome', render: (_, row) => <Flex vertical gap="var(--ac-space-compact)">
          <Typography.Text strong className="admin-instance-id">{row.sourceKind === 'builtin' ? 'Built-in' : 'Webhook'} · {row.sourceKey}</Typography.Text>
          <Flex wrap gap="var(--ac-space-compact)"><Tag>{statuses[row.status] ?? row.status}</Tag>{row.runStatus && <Typography.Text>Run: {row.runStatus}</Typography.Text>}</Flex>
          <Typography.Text type="secondary">Received {new Date(row.receivedAtUtc).toLocaleString()}</Typography.Text>
          {row.batchCount != null && <Typography.Text type="secondary">Batch: {row.batchCount} events</Typography.Text>}
          {!row.batchReadable && <Alert type="info" showIcon title="Batch details need additional source access" description="This event is readable. Related batch contents and work are hidden." />}
          {row.code && <Typography.Text className="admin-instance-id" type="secondary">Reason: {row.code}</Typography.Text>}
          <Flex wrap gap="var(--ac-space-compact)">{row.approvalRequestId && <Typography.Link href={tenantApprovalPath(tenant, row.approvalRequestId)} onClick={followAppLink}>Review admission</Typography.Link>}
            {row.runId && <Typography.Link href={`${tenantAgentPath(tenant, instance, 'activity-runs')}?run=${encodeURIComponent(row.runId)}`} onClick={followAppLink}>Open Run in Activity</Typography.Link>}</Flex>
          {!stale && row.data && <Collapse ghost items={[{ key: 'data', label: 'Inspect received data', children: <SavedApprovalText label="Received data" text={JSON.stringify(row.data, null, 2)} /> }]} />}
        </Flex> }]} />
      {view.total > 10 && <Pagination aria-label="Event Activity pages" current={page + 1} pageSize={10} total={view.total} disabled={stale || state.loading} showSizeChanger={false} onChange={value => setPage(value - 1)} />}
    </>}
  </Flex>;
}
