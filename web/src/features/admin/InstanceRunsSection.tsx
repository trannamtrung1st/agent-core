import { useEffect, useState } from "react";
import { Alert, Button, Drawer, Empty, Flex, Spin, Table, Typography, theme } from "antd";
import { getInstanceAgentRun, listInstanceAgentRuns, type AgentRun } from "../../services/api";
import { AgentRunDetails, AgentRunStatus } from "../chat/AgentRunDetails";
import { useCursorPages } from "../chat/useCursorPages";
import { DrawerListFooter } from "../chat/DrawerListFooter";
import { runActivationLabel, type RunSource } from "../chat/runPresentation";
import { formatChatTime } from "../chat/chatTime";
import { AdminCollectionToolbar } from "./AdminCollectionToolbar";
import { useActivitySearch } from "./useActivitySearch";

export function InstanceRunsSection({ instanceId, open, wide = true, inline = false, selectedAgentRunId, onClose, onSource, onRun, detailsOnly, afterClose }: {
  instanceId: string; open: boolean; wide?: boolean; inline?: boolean; selectedAgentRunId?: string; onClose: () => void;
  onSource?: (source: RunSource) => void; onRun?: (runId: string) => void; detailsOnly?: boolean; afterClose?: () => void;
}) {
  const { token } = theme.useToken();
  const page = useCursorPages(instanceId, open && !detailsOnly, listInstanceAgentRuns);
  const { search, setSearch } = useActivitySearch();
  const [selected, setSelected] = useState<AgentRun | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    let current = true; let generation = 0; setSelected(null); setError(null);
    async function refresh() {
      if (!open || !selectedAgentRunId) return;
      const request = ++generation;
      try { const row = await getInstanceAgentRun(instanceId, selectedAgentRunId);
        if (current && request === generation) { setSelected(prior => prior && prior.revision > row.revision ? prior : row); setError(null); }
      } catch (reason) { if (current && request === generation) setError(reason instanceof Error ? reason.message : "Unable to load this run. Return to Runs and try again."); }
    }
    void refresh(); const timer = window.setInterval(() => void refresh(), 5_000);
    return () => { current = false; generation++; window.clearInterval(timer); };
  }, [instanceId, selectedAgentRunId, open]);
  const content = detailsOnly ? <Flex vertical gap={token.padding}>
    {selected?.automationId ? <Button onClick={() => onSource?.({ kind: "automation", automationId: selected.automationId! })}>View Automation</Button>
      : selected?.experienceId ? <Button onClick={() => onSource?.({ kind: "experience", agentRunId: selected.agentRunId })}>View Experience</Button> : null}
    {error ? <Alert type="error" showIcon title={error} /> : selected ? <AgentRunDetails run={selected} onChange={setSelected} /> : <Spin aria-label="Loading run details" />}
  </Flex> : <Flex vertical gap={token.padding}>
    <Typography.Text type="secondary">Individual invocations, including chat, automation and background work. Newest first.</Typography.Text>
    <AdminCollectionToolbar label="loaded runs" value={search} onChange={setSearch} />
    {page.error ? <Alert type="error" showIcon title={page.error} /> : null}
    <Table<AgentRun> aria-label="Runs table" className="admin-collection-table" size="small" rowKey="agentRunId"
      loading={page.loading} dataSource={page.items.filter(row => `${runActivationLabel(row.activationKind)} ${row.agentRunId} ${row.sessionId ?? ""} ${row.status} ${row.outcome?.summary ?? ""}`.toLowerCase().includes(search.toLowerCase()))} pagination={false} scroll={{ x: 850 }}
      locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={page.error ? "Runs could not be loaded. Retry below." : search ? "No matching loaded runs. Clear search or load older runs." : "No runs yet. Runs appear when this Agent takes a turn."} /> }}
      columns={[
        { title: "Run", key: "run", width: 300, render: (_, row) => <Flex vertical gap={token.paddingXS}>
          <Button type="link" className="admin-collection-name" onClick={() => onRun?.(row.agentRunId)}>{runActivationLabel(row.activationKind)}</Button>
          <Typography.Text type="secondary" className="admin-activity-id" copyable={{ text: row.agentRunId }} title={row.agentRunId}>ID {`${row.agentRunId.slice(0, 8)}…${row.agentRunId.slice(-8)}`}</Typography.Text>
          {row.outcome?.summary ? <Typography.Text type="secondary" ellipsis title={row.outcome.summary}>{row.outcome.summary}</Typography.Text> : null}
        </Flex> },
        { title: "Session", key: "session", width: 170, render: (_, row) => row.sessionId ? <Typography.Text type="secondary" copyable={{ text: row.sessionId }} title={row.sessionId}>{`${row.sessionId.slice(0, 8)}…${row.sessionId.slice(-8)}`}</Typography.Text> : "No session" },
        { title: "Created", key: "created", width: 180, render: (_, row) => <time dateTime={row.createdAt}>{formatChatTime(row.createdAt) ?? "Unknown time"}</time> },
        { title: "Status", key: "status", width: 150, render: (_, row) => <AgentRunStatus run={row} /> },
        { title: "Updated", key: "updated", width: 200, render: (_, row) => <time dateTime={row.updatedAt}>{formatChatTime(row.updatedAt) ?? "Unknown time"}</time> }
      ]} />
    <DrawerListFooter loadingMore={page.loadingMore} hasMore={page.hasMore} error={page.error} count={page.items.length} onLoadMore={() => void page.loadMore()} onRetry={() => void page.retry()} />
  </Flex>;
  if (inline) return <section aria-label="Runs">{content}</section>;
  return <Drawer title={detailsOnly ? "Run details" : "Runs"} open={open} onClose={onClose} className="background-work-drawer"
    size={wide ? "min(640px, 100vw)" : "100vw"} afterOpenChange={visible => { if (!visible) afterClose?.(); }}>{content}</Drawer>;
}
