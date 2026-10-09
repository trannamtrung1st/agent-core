import { AdminErrorNotice } from "./adminFailure";
import { useCallback, useEffect, useRef, useState, type Key } from "react";
import { Alert, App, Button, Descriptions, Empty, Flex, Form, Spin, Switch, Table, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { instanceContinuityRequest as request, type IdentityMaintenanceSettings, type ExperienceItem, type ExperienceReview } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";

import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

import { AdminSessionPicker } from "./AdminSessionPicker";

const date = (value: string | null) => value ? new Date(value).toLocaleString() : "Not yet";

// A mutation invalidates older reads; polling cannot supersede an in-progress owner action.
function useResponseOrder() {
  const order = useRef({ generation: 0, mutating: false });
  useEffect(() => () => { order.current.generation++; }, []);
  return order;
}


function Failure({ error, reload }: { error: AdminFailureNotice | null; reload: () => void }) {
  return error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button onClick={reload}>Reload</Button>} /> : null;
}

export function IdentityMaintenanceSection({ instanceId }: { instanceId: string }) {
  const { token } = theme.useToken();
  const [settings, setSettings] = useState<IdentityMaintenanceSettings | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [busy, setBusy] = useState(false);
  const order = useResponseOrder();
  const reload = useCallback(async () => {
    const generation = ++order.current.generation;
    setBusy(true);
    try {
      const value = await request<IdentityMaintenanceSettings>(instanceId, "maintenance");
      if (generation === order.current.generation) { setSettings(value); setError(null); }
    } catch (reason) {
      if (generation === order.current.generation) setError(describeAdminError(reason, "Unable to load consolidation settings. Reload and try again."));
    } finally { if (generation === order.current.generation) setBusy(false); }
  }, [instanceId, order]);
  useEffect(() => { setSettings(null); void reload(); }, [reload]);
  async function configure(allowAgentConsolidation: boolean) {
    if (!settings || order.current.mutating) return;
    order.current.mutating = true;
    const generation = ++order.current.generation;
    setBusy(true); setError(null);
    try {
      const value = await request<IdentityMaintenanceSettings>(instanceId, "maintenance", "PUT", { expectedRevision: settings.revision, allowAgentConsolidation });
      if (generation === order.current.generation) setSettings(value);
    } catch (reason) {
      if (generation === order.current.generation) setError(describeAdminError(reason, "Consolidation settings changed or could not be saved. Reload and try again."));
    } finally { order.current.mutating = false; if (generation === order.current.generation) setBusy(false); }
  }
  return <section className="admin-definition-panel" aria-label="Identity maintenance">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Identity maintenance</Typography.Title>
      <Typography.Text type="secondary">Keep redundant learned state coherent while retaining its sources.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.paddingXS}>
      <Flex wrap align="center" gap={token.paddingXS}>
        <Switch aria-label="Allow agent consolidation" checked={settings?.allowAgentConsolidation ?? false} loading={busy} disabled={!settings || busy || !!error}
          onChange={value => void configure(value)} />
        <Typography.Text>Allow agent consolidation</Typography.Text>
      </Flex>
      <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>Permits safe consolidation of repeated Experience and inferred identity memory through normal Automation Runs. It does not schedule runs, change memory scope, or permit silent forgetting. Protected changes require exact approval.</Typography.Paragraph>
      <Failure error={error} reload={() => void reload()} />
    </Flex></div>
  </section>;
}

export type ExperienceSelection = { agentRunId: string; request: number };
export function ExperienceSection({ instanceId, onWork, selection, active = true }: { instanceId: string; active?: boolean; onWork: (workId?: string) => void; selection?: ExperienceSelection }) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const [review, setReview] = useState<ExperienceReview | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [session, setSession] = useState("");
  const [expanded, setExpanded] = useState<Key[]>([]);
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [tableVersion, setTableVersion] = useState(0);
  const appliedSelection = useRef<ExperienceSelection | undefined>(undefined);
  useEffect(() => {
    if (!selection || appliedSelection.current === selection) return;
    const item = review?.items.find(item => item.generationAgentRunId === selection.agentRunId);
    if (!item) return;
    appliedSelection.current = selection;
    setSearch(""); setExpanded([item.experienceId]); setTableVersion(value => value + 1);
  }, [selection, review, setSearch]);
  useEffect(() => {
    if (!selection) return;
    const frame = requestAnimationFrame(() => {
      const button = document.querySelector<HTMLButtonElement>(`[data-experience-generation-id="${selection.agentRunId}"]`);
      button?.scrollIntoView({ block: "nearest" });
      const table = button?.closest(".ant-table-content"); if (table) table.scrollLeft = 0;
      button?.focus({ preventScroll: true });
    });
    return () => cancelAnimationFrame(frame);
  }, [selection, tableVersion]);
  const order = useResponseOrder();
  const reload = useCallback(async () => {
    if (order.current.mutating) return;
    const generation = ++order.current.generation;
    setLoading(true); setError(null);
    try { const next = await request<ExperienceReview>(instanceId, "experience"); if (generation === order.current.generation) setReview(next); }
    catch (reason) { if (generation === order.current.generation) setError(describeAdminError(reason, "Experience could not be loaded.")); }
    finally { if (generation === order.current.generation) setLoading(false); }
  }, [instanceId, order]);
  useEffect(() => { if (active) void reload(); }, [reload, selection?.request, active]);
  useEffect(() => {
    if (!active || loading || !review?.items.some(item => ["Pending", "Queued", "Running", "WaitingToRetry", "WaitingForSignal"].includes(item.status))) return;
    const timer = window.setInterval(() => {
      if (order.current.mutating) return;
      const generation = ++order.current.generation;
      void request<ExperienceReview>(instanceId, "experience")
        .then(next => { if (generation === order.current.generation) setReview(next); })
        .catch(reason => { if (generation === order.current.generation) setError(describeAdminError(reason, "Experience status could not be refreshed.")); });
    }, 2000);
    return () => window.clearInterval(timer);
  }, [instanceId, review, order, loading, active]);
  async function mutate(path: string, method: string, body: unknown) {
    if (order.current.mutating) return;
    order.current.mutating = true; ++order.current.generation;
    setLoading(false);
    setBusy(true); setError(null);
    try {
      setReview(await request<ExperienceReview>(instanceId, path, method, body));
      if (path === "experience/reset") { setSearch(""); setExpanded([]); }
    }
    catch (reason) { setError(describeAdminError(reason, "Experience update failed. Reload and try again.")); }
    finally { order.current.mutating = false; setBusy(false); }
  }
  return <section className="admin-definition-panel" aria-label="Experience">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Experience</Typography.Title>
      <Typography.Text type="secondary">Derived observations about past work, kept separately from learned memory.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading experience" /> : null}
      {selection && !loading && !error && review && !review.items.some(item => item.generationAgentRunId === selection.agentRunId) ? <Alert type="info" showIcon title="This experience checkpoint is not available in the current records" description="It may have been deleted or be outside the bounded review. Its run remains available in Runs." /> : null}
      <Failure error={error} reload={() => void reload()} />
      {review ? <>
        <Flex wrap align="center" gap={token.paddingXS}><Switch aria-label="Enable experience" checked={review.enabled} disabled={busy}
          onChange={enabled => void mutate("experience/configuration", "PUT", { expectedRevision: review.settingsRevision, enabled })} />
          <Typography.Text>{review.enabled ? "Enabled · completed work may be retrospected" : "Disabled · experience is not supplied to the agent"}</Typography.Text>
          <Button onClick={() => void reload()} disabled={busy}>Refresh experience</Button></Flex>
        <Typography.Paragraph type="secondary">Configure recurring review under Automation. Review now starts an ordinary Run for the selected source.</Typography.Paragraph>
        <Form layout="vertical" className="admin-config-form" onFinish={() => void mutate("experience/checkpoints", "POST", { sessionId: session.trim() })}>
          <Form.Item label="Source conversation" extra="Only completed observable work in this instance is eligible. A repeated checkpoint creates no duplicate.">
            <AdminSessionPicker instanceId={instanceId} value={session} onChange={setSession} disabled={busy || !review.enabled} />
          </Form.Item>
          <Flex wrap gap={token.paddingXS}><Button htmlType="submit" disabled={busy || !review.enabled || !session.trim()}>Retrospect now</Button>
            <Button onClick={() => onWork()}>View runs</Button>
            <Button danger disabled={busy || review.items.length === 0} onClick={() => confirmAction(modal, {
              title: "Reset this instance's experience?", content: "Removes derived experience. Source conversations, learned memory and persona remain available.",
              okText: "Reset experience", danger: true, onOk: () => mutate("experience/reset", "POST", {})
            })}>Reset experience</Button></Flex>
        </Form>
        {review.items.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No experience yet. Enable experience and retrospect a completed task." /> : <>
        <AdminCollectionToolbar label="experience" value={search} onChange={setSearch} />
        <Table<ExperienceItem> key={tableVersion} aria-label="Experience table" className="admin-collection-table" size="small" rowKey="experienceId"
          dataSource={review.items.filter(item => [experienceGoal(item), item.status, experienceContext(item), item.sourceKind === "Session" ? "Session" : "Background work",
            item.sourceId, item.definitionId, item.modelKey, ...Object.values(item.content ?? {}).flat()]
            .some(value => String(value).toLowerCase().includes(search.trim().toLowerCase())))}
          scroll={{ x: 1050 }} pagination={pagination}
          locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={search.trim() || review.items.length
            ? "No matches. Clear search or filters to see all results." : "No experience yet. Enable experience and retrospect a completed task."} /> }}
          columns={[
            { title: "Goal", key: "goal", width: 350, ellipsis: true,
              render: (_, item) => <Button type="link" size="small" className="admin-collection-name" title={experienceGoal(item)}
                data-experience-generation-id={item.generationAgentRunId} aria-label={`View experience: ${experienceGoal(item)}`} aria-expanded={expanded.includes(item.experienceId)}
                onClick={() => setExpanded(expanded.includes(item.experienceId) ? [] : [item.experienceId])}>{experienceGoal(item)}</Button> },
            { title: "Status", dataIndex: "status", width: 120,
              filters: [...new Set(review.items.map(item => item.status))].map(value => ({ text: value, value })),
              onFilter: (value, item) => item.status === value,
              render: (status: string) => <Tag color={status === "Failed" ? "error" : undefined}>{status}</Tag> },
            { title: "Context", key: "context", width: 170,
              filters: ["Eligible for context", "Suppressed", "Superseded", "Not in context"].map(value => ({ text: value, value })),
              onFilter: (value, item) => experienceContext(item) === value,
              render: (_, item) => <Tag>{experienceContext(item)}</Tag> },
            { title: "Source", dataIndex: "sourceKind", width: 150,
              filters: [{ text: "Session", value: "Session" }, { text: "Background work", value: "AgentRun" }, { text: "Consolidated", value: "Consolidation" }],
              onFilter: (value, item) => item.sourceKind === value,
              render: (kind: string) => kind === "Consolidation" ? "Consolidated" : kind === "Session" ? "Session" : "Background work" },
            { title: "Checkpoint captured", key: "checkpointAt", width: 220, defaultSortOrder: "descend",
              sorter: (a, b) => Number(a.generationAgentRunId === selection?.agentRunId) - Number(b.generationAgentRunId === selection?.agentRunId) || (a.checkpointAt ?? a.sourceCreatedAt ?? a.sourceAt).localeCompare(b.checkpointAt ?? b.sourceCreatedAt ?? b.sourceAt),
              render: (_, item) => item.checkpointAt ? date(item.checkpointAt) : <Typography.Text type="secondary" title={`Source created: ${date(item.sourceCreatedAt ?? item.sourceAt)}`}>Not recorded</Typography.Text> }
          ]}
          expandable={{ fixed: "left", expandedRowKeys: expanded, onExpand: (open, item) => setExpanded(open ? [item.experienceId] : []),
            expandedRowRender: item => <Flex vertical gap={token.padding} style={{ whiteSpace: "normal" }}>
              <ExperienceDetails item={item} />
              <Flex wrap align="center" justify="space-between" gap={token.padding}>
                {item.generationAgentRunId !== "00000000-0000-0000-0000-000000000000" ? <Button onClick={() => onWork(item.generationAgentRunId)}>View generation run</Button> : null}
                <Flex wrap gap={token.paddingXS}>
                  <Button disabled={busy || item.visibility === "Superseded" || item.visibility === "Deleted"} onClick={() => void mutate(`experience/${item.experienceId}`, "PUT", { expectedRevision: item.revision, visibility: item.visibility === "Suppressed" ? "Eligible" : "Suppressed" })}>
                    {item.visibility === "Suppressed" ? "Include in context" : "Suppress experience"}</Button>
                  <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: "Delete this experience?", content: "The source work remains available. This checkpoint will not be regenerated.", okText: "Delete experience", danger: true,
                    onOk: () => mutate(`experience/${item.experienceId}`, "PUT", { expectedRevision: item.revision, visibility: "Deleted" }) })}>Delete experience</Button>
                </Flex>
              </Flex>
            </Flex>
          }} />
        </>}
      </> : null}
    </Flex></div>
  </section>;
}

const experienceGoal = (item: ExperienceItem) => item.content?.goal ?? (item.status === "Failed" ? "Retrospection failed"
  : item.status === "Cancelled" ? "Retrospection cancelled" : "Retrospection in progress");
const experienceContext = (item: ExperienceItem) => item.eligibleForContext ? "Eligible for context"
  : item.visibility === "Superseded" ? "Superseded" : item.visibility === "Deleted" ? "Deleted" : item.visibility === "Suppressed" ? "Suppressed" : "Not in context";

function ExperienceDetails({ item }: { item: ExperienceItem }) {
  const { token } = theme.useToken();
  const detailLayout = useAdminDetailLayout();
  return <Flex vertical gap={token.padding} role="region" aria-label="Experience details">
    {item.failureSummary ? <Alert type="error" showIcon title={<AdminErrorNotice message={item.failureSummary} diagnosticId={item.diagnosticId} showDetailsLabel />} /> : null}
    {item.content ? <Descriptions bordered column={1} size="small" {...detailLayout}
      items={[{ key: "goal", label: "Goal", children: item.content.goal },
        ...(["lessons", "corrections", "outcomes", "decisions", "attempts", "unresolved", "difficulties"] as const)
          .filter(key => item.content![key].length > 0).map(key => ({ key, label: key[0].toUpperCase() + key.slice(1),
            children: <ul style={{ margin: 0, paddingInlineStart: token.padding, maxWidth: "72ch" }}>
              {item.content![key].map((text, index) => <li key={index}>{text}</li>)}
            </ul> }))]} /> : null}
    <Descriptions title="Provenance" bordered column={1} size="small" {...detailLayout}
      items={[
        { key: "visibility", label: "State", children: <Tag>{item.visibility}</Tag> },
        ...(item.derivedFromExperienceIds?.length ? [
          { key: "lineage", label: `Consolidated from ${item.derivedFromExperienceIds.length} experiences`, children: <ul style={{ margin: 0, paddingInlineStart: token.padding }}>{item.derivedFromExperienceIds.map(id => <li key={id}>{id}</li>)}</ul> },
          { key: "origin", label: "Maintenance origin", children: item.maintenanceOrigin ?? "Not recorded" }
        ] : []),
        { key: "source", label: item.sourceKind === "Consolidation" ? "Maintenance operation" : `Source ${item.sourceKind === "Session" ? "Session" : "background work"}`, children: item.sourceId },
        { key: "checkpoint", label: "Checkpoint", children: item.throughCursor },
        { key: "definition", label: "Definition", children: `${item.definitionId} · v${item.definitionVersion}` },
        { key: "model", label: "Model", children: item.modelKey },
        { key: "created", label: "Source created", children: date(item.sourceCreatedAt ?? item.sourceAt) },
        { key: "captured", label: "Checkpoint captured", children: item.checkpointAt ? date(item.checkpointAt) : "Not recorded (legacy checkpoint)" }
      ]} />
  </Flex>;
}
