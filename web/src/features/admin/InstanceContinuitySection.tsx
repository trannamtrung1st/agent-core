import { useCallback, useEffect, useRef, useState, type Key } from "react";
import { Alert, App, Button, Descriptions, Empty, Flex, Form, Input, InputNumber, Select, Spin, Switch, Table, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { drawerPageSearch, listModels, type DrawerPageQuery, type ModelDescriptor, type WorkItem, type WorkItemResult } from "../../services/api";
import { instanceContinuityRequest as request, type IdentityMaintenanceSettings, type ExperienceItem, type ExperienceReview, type ThoughtDraft, type ThoughtRegistration, type ThoughtReview } from "../../services/adminApi";
import { BackgroundWorkDrawer } from "../chat/BackgroundWorkDrawer";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";

import { ExecutionModelFields } from "./ExecutionModelFields";
import { AdminCollectionToolbar, useAdminCollectionSearch } from "./AdminCollectionToolbar";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

import { thoughtOutcomeLabel, runStatusLabel, type AutomationSelection, type RunSource } from "../chat/runPresentation";
import { useAutomationSelection } from "./useAutomationSelection";
import { AdminSessionPicker } from "./AdminSessionPicker";

const date = (value: string | null) => value ? new Date(value).toLocaleString() : "Not yet";

// A mutation invalidates older reads; polling cannot supersede an in-progress owner action.
function useResponseOrder() {
  const order = useRef({ generation: 0, mutating: false });
  useEffect(() => () => { order.current.generation++; }, []);
  return order;
}

const loadInstanceWork = async (id: string, query?: DrawerPageQuery) =>
  (await request<{ items: WorkItem[] }>(id, `work-items${drawerPageSearch(query)}`)).items;
const loadInstanceWorkResult = (id: string, workId: string) =>
  request<WorkItemResult>(id, `work-items/${workId}/result`);

const loadInstanceWorkItem = (id: string, workId: string) => request<WorkItem>(id, `work-items/${workId}`);
export function InstanceRunsSection({ instanceId, open, wide = true, inline = false, selectedWorkItemId, onClose, onSource }: {
  instanceId: string; open: boolean; wide?: boolean; inline?: boolean; selectedWorkItemId?: string; onClose: () => void;
  onSource?: (source: RunSource) => void;
}) {
  return <BackgroundWorkDrawer sessionId={instanceId} open={open} wide={wide} inline={inline} selectedWorkItemId={selectedWorkItemId}
    onClose={onClose} onSource={onSource} load={loadInstanceWork} loadOne={loadInstanceWorkItem} loadResult={loadInstanceWorkResult}
    cancel={(id, workId, expectedRevision) => request<WorkItem>(id, `work-items/${workId}/cancel`, "POST", { expectedRevision })}
    approve={(id, workId, approvalId, expectedRevision, expectedApprovalRevision, actionHash) => request<WorkItem>(id,
      `work-items/${workId}/approvals/${approvalId}/approve`, "POST", { expectedRevision, expectedApprovalRevision, actionHash })}
    reject={(id, workId, approvalId, expectedRevision, expectedApprovalRevision, actionHash) => request<WorkItem>(id,
      `work-items/${workId}/approvals/${approvalId}/reject`, "POST", { expectedRevision, expectedApprovalRevision, actionHash })} />;
}

function Failure({ error, reload }: { error: AdminFailureNotice | null; reload: () => void }) {
  return error ? <Alert type="error" showIcon title={error.message} action={<Button onClick={reload}>Reload</Button>}
    description={error.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: error.diagnosticId }} /> : undefined} /> : null;
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
      <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>Permits safe consolidation of repeated Experience and inferred identity memory through normal Thought runs. It does not schedule runs, change memory scope, or permit silent forgetting. Protected changes require exact approval.</Typography.Paragraph>
      <Failure error={error} reload={() => void reload()} />
    </Flex></div>
  </section>;
}

export type ExperienceSelection = { workItemId: string; request: number };
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
    const item = review?.items.find(item => item.generationWorkItemId === selection.workItemId);
    if (!item) return;
    appliedSelection.current = selection;
    setSearch(""); setExpanded([item.experienceId]); setTableVersion(value => value + 1);
  }, [selection, review, setSearch]);
  useEffect(() => {
    if (!selection) return;
    const frame = requestAnimationFrame(() => {
      const button = document.querySelector<HTMLButtonElement>(`[data-experience-generation-id="${selection.workItemId}"]`);
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
    if (!active || loading || !review?.items.some(item => ["Pending", "Queued", "Running", "WaitingToRetry"].includes(item.status))) return;
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
      {selection && !loading && !error && review && !review.items.some(item => item.generationWorkItemId === selection.workItemId) ? <Alert type="info" showIcon title="This experience checkpoint is not available in the current records" description="It may have been deleted or be outside the bounded review. Its run remains available in Runs." /> : null}
      <Failure error={error} reload={() => void reload()} />
      {review ? <>
        <Flex wrap align="center" gap={token.paddingXS}><Switch aria-label="Enable experience" checked={review.enabled} disabled={busy}
          onChange={enabled => void mutate("experience/configuration", "PUT", { expectedRevision: review.settingsRevision, enabled })} />
          <Typography.Text>{review.enabled ? "Enabled · completed work may be retrospected" : "Disabled · experience is not supplied to the agent"}</Typography.Text>
          <Button onClick={() => void reload()} disabled={busy}>Refresh experience</Button></Flex>
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
                data-experience-generation-id={item.generationWorkItemId} aria-label={`View experience: ${experienceGoal(item)}`} aria-expanded={expanded.includes(item.experienceId)}
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
              filters: [{ text: "Session", value: "Session" }, { text: "Background work", value: "WorkItem" }, { text: "Consolidated", value: "Consolidation" }],
              onFilter: (value, item) => item.sourceKind === value,
              render: (kind: string) => kind === "Consolidation" ? "Consolidated" : kind === "Session" ? "Session" : "Background work" },
            { title: "Checkpoint captured", key: "checkpointAt", width: 220, defaultSortOrder: "descend",
              sorter: (a, b) => Number(a.generationWorkItemId === selection?.workItemId) - Number(b.generationWorkItemId === selection?.workItemId) || (a.checkpointAt ?? a.sourceCreatedAt ?? a.sourceAt).localeCompare(b.checkpointAt ?? b.sourceCreatedAt ?? b.sourceAt),
              render: (_, item) => item.checkpointAt ? date(item.checkpointAt) : <Typography.Text type="secondary" title={`Source created: ${date(item.sourceCreatedAt ?? item.sourceAt)}`}>Not recorded</Typography.Text> }
          ]}
          expandable={{ fixed: "left", expandedRowKeys: expanded, onExpand: (open, item) => setExpanded(open ? [item.experienceId] : []),
            expandedRowRender: item => <Flex vertical gap={token.padding} style={{ whiteSpace: "normal" }}>
              <ExperienceDetails item={item} />
              {item.generationWorkItemId !== "00000000-0000-0000-0000-000000000000" ? <Button style={{ alignSelf: "flex-start" }} onClick={() => onWork(item.generationWorkItemId)}>View generation run</Button> : null}
              <Flex wrap gap={token.paddingXS}>
                <Button disabled={busy || item.visibility === "Superseded" || item.visibility === "Deleted"} onClick={() => void mutate(`experience/${item.experienceId}`, "PUT", { expectedRevision: item.revision, visibility: item.visibility === "Suppressed" ? "Eligible" : "Suppressed" })}>
                  {item.visibility === "Suppressed" ? "Include in context" : "Suppress experience"}</Button>
                <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: "Delete this experience?", content: "The source work remains available. This checkpoint will not be regenerated.", okText: "Delete experience", danger: true,
                  onOk: () => mutate(`experience/${item.experienceId}`, "PUT", { expectedRevision: item.revision, visibility: "Deleted" }) })}>Delete experience</Button>
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
    {item.failureSummary ? <Alert type="error" showIcon title={item.failureSummary}
      description={item.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: item.diagnosticId }} /> : undefined} /> : null}
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

const thoughtIntervalUnits = [
  { value: 1, label: "Seconds" }, { value: 60, label: "Minutes" }, { value: 3600, label: "Hours" }
];
const intervalUnitFor = (seconds: number) => seconds % 3600 === 0 ? 3600 : seconds % 60 === 0 ? 60 : 1;
function thoughtIntervalLabel(seconds: number) {
  const unit = intervalUnitFor(seconds);
  const value = seconds / unit;
  const label = unit === 3600 ? "hour" : unit === 60 ? "minute" : "second";
  return `Every ${value} ${label}${value === 1 ? "" : "s"}`;
}
const blank: ThoughtDraft = { expectedRevision: 0, enabled: false, intervalSeconds: 3600, thinkingPrompt: "", modelKey: null, reasoningEffort: null };
const thoughtOutcome = (item: ThoughtRegistration) => ["Queued", "Running", "WaitingForApproval", "WaitingToRetry"].includes(item.executionStatus ?? "") ? runStatusLabel(item.executionStatus)
  : thoughtOutcomeLabel(item.lastOutcome ?? item.executionStatus);
export function ThoughtSection({ instanceId, onWork, selection, active = true }: { instanceId: string; active?: boolean; onWork: (workId?: string) => void; selection?: AutomationSelection }) {
  const { search, setSearch, pagination } = useAdminCollectionSearch();
  const [expanded, setExpanded] = useState<Key[]>([]);
  const detailLayout = useAdminDetailLayout();
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const [review, setReview] = useState<ThoughtReview | null>(null);
  const [models, setModels] = useState<ModelDescriptor[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [pendingAction, setPendingAction] = useState<string | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState<ThoughtDraft>(blank);
  const [intervalUnit, setIntervalUnit] = useState(3600);
  // Admission precedes the execution projection. Keep the click acknowledged across that gap.
  const startingRunsRef = useRef<Record<string, string | null>>({});
  const [startingRuns, setStartingRuns] = useState<Record<string, string | null>>({});
  const applyReview = useCallback((next: ThoughtReview) => {
    const remaining = Object.fromEntries(Object.entries(startingRunsRef.current).filter(([id, previousWorkId]) => {
      const item = next.items.find(row => row.registrationId === id);
      return item && (!item.lastWorkItemId || item.lastWorkItemId === previousWorkId);
    }));
    startingRunsRef.current = remaining;
    setStartingRuns(remaining);
    setReview(next);
  }, []);
  const minimumInterval = review?.minIntervalSeconds ?? 15;
  const invalidInterval = draft.intervalSeconds < minimumInterval || draft.intervalSeconds > 604800;
  const order = useResponseOrder();
  const reload = useCallback(async () => {
    if (order.current.mutating) return;
    const generation = ++order.current.generation;
    setLoading(true); setError(null);
    try { const [thoughts, available] = await Promise.all([request<ThoughtReview>(instanceId, "thoughts"), listModels()]); if (generation === order.current.generation) { applyReview(thoughts); setModels(available.models.filter(model => model.tools)); } }
    catch (reason) { if (generation === order.current.generation) setError(describeAdminError(reason, "Thoughts could not be loaded.")); }
    finally { if (generation === order.current.generation) setLoading(false); }
  }, [instanceId, order, applyReview]);
  useEffect(() => { void reload(); }, [reload]);
  useEffect(() => {
    if (selection?.kind === "thought") void reload();
  }, [selection, reload]);
  const previousActive = useRef(active);
  useEffect(() => {
    if (active && !previousActive.current) void reload();
    previousActive.current = active;
  }, [active, reload]);
  useEffect(() => {
    if (loading || !active) return;
    const timer = window.setInterval(() => {
      if (order.current.mutating) return;
      const generation = ++order.current.generation;
      void request<ThoughtReview>(instanceId, "thoughts").then(next => { if (generation === order.current.generation) applyReview(next); })
        .catch(reason => { if (generation === order.current.generation) setError(describeAdminError(reason, "Thought status could not be refreshed.")); });
    }, 5000);
    return () => window.clearInterval(timer);
  }, [instanceId, order, loading, applyReview, active]);
  async function action(path: string, body: unknown, method = "POST") {
    const runId = /^thoughts\/([^/]+)\/run$/.exec(path)?.[1];
    if (order.current.mutating || (runId && runId in startingRunsRef.current)) return;
    if (runId) {
      startingRunsRef.current = { ...startingRunsRef.current, [runId]: review?.items.find(item => item.registrationId === runId)?.lastWorkItemId ?? null };
      setStartingRuns(startingRunsRef.current);
    }
    let accepted = false;
    order.current.mutating = true; ++order.current.generation;
    setLoading(false);
    setBusy(true); setPendingAction(path); setError(null);
    try {
      await request(instanceId, path, method, body);
      accepted = true;
      applyReview(await request<ThoughtReview>(instanceId, "thoughts"));
      if (path === "thoughts" || method === "PUT") { setEditing(null); setDraft(blank); setIntervalUnit(3600); }
    } catch (reason) {
      if (runId && !accepted) {
        const remaining = { ...startingRunsRef.current }; delete remaining[runId];
        startingRunsRef.current = remaining; setStartingRuns(remaining);
      }
      setError(describeAdminError(reason, "Thought update failed. Reload for the current revision."));
    }
    finally { order.current.mutating = false; setBusy(false); setPendingAction(null); }
  }
  function edit(item: ThoughtRegistration) {
    setIntervalUnit(intervalUnitFor(item.intervalSeconds));
    setEditing(item.registrationId); setDraft({ expectedRevision: item.revision, enabled: item.enabled,
      intervalSeconds: item.intervalSeconds, thinkingPrompt: item.thinkingPrompt, modelKey: item.modelKey, reasoningEffort: item.reasoningEffort });
  }
  const tableVersion = useAutomationSelection(selection, "thought", review?.items.map(item => item.registrationId) ?? [], setSearch, setExpanded);
  return <section className="admin-definition-panel" aria-label="Thoughts">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Thoughts</Typography.Title>
      <Typography.Text type="secondary">Periodically review current context and decide whether useful action exists. Doing nothing is a valid outcome.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading thoughts" /> : null}
      {selection?.kind === "thought" && !loading && !error && review && !review.items.some(item => item.registrationId === selection.registrationId) ? <Alert type="info" showIcon title="This source configuration is no longer available" description="It may have been deleted or retired. Its run remains available in Runs." /> : null}
      <Failure error={error} reload={() => void reload()} />
      {review ? <>
        <Form layout="vertical" className="admin-config-form" onFinish={() => void action(editing ? `thoughts/${editing}` : "thoughts", draft, editing ? "PUT" : "POST")}>
          <Form.Item label="Thinking prompt" extra={<Flex vertical gap={token.paddingXS}><Typography.Text type="secondary">{draft.thinkingPrompt.length} / 2000 characters</Typography.Text>
            <span>For example: Review recent experience for repeated problems. Improve only when meaningful; otherwise do nothing. Tools and approvals still follow current policy.</span></Flex>}>
            <Input.TextArea aria-label="Thinking prompt" rows={4} maxLength={2000} value={draft.thinkingPrompt} disabled={busy}
              onChange={e => setDraft({ ...draft, thinkingPrompt: e.target.value })} />
          </Form.Item>
          <Flex wrap gap={token.padding} className="admin-form-row">
            <Form.Item label="Interval" validateStatus={invalidInterval ? "error" : undefined}
              help={invalidInterval ? `Choose an interval from ${minimumInterval} seconds to 7 days.` : undefined}
              extra={`Minimum ${minimumInterval} seconds. Short intervals are useful for demos; frequent runs use more model and tool resources.`}>
              <Flex gap={token.paddingXS}>
                <InputNumber aria-label="Thought interval" min={Math.ceil(minimumInterval / intervalUnit)} max={Math.floor(604800 / intervalUnit)}
                  precision={0} value={draft.intervalSeconds / intervalUnit} disabled={busy}
                  onChange={value => setDraft({ ...draft, intervalSeconds: (value ?? 0) * intervalUnit })} />
                <Select aria-label="Thought interval unit" style={{ width: "8rem", flex: "none" }} value={intervalUnit} options={thoughtIntervalUnits} disabled={busy}
                  onChange={unit => { setDraft({ ...draft, intervalSeconds: Math.round(draft.intervalSeconds / intervalUnit) * unit }); setIntervalUnit(unit); }} />
              </Flex>
            </Form.Item>
            <Form.Item label="Enable thought activation"><Switch aria-label="Enable thought activation" checked={draft.enabled} disabled={busy} onChange={enabled => setDraft({ ...draft, enabled })} /></Form.Item>
          </Flex>
          <Form.Item label="Execution model" extra="Uses this instance's unattended default unless you choose a model. More frequent runs use more model and tool resources.">
            <ExecutionModelFields models={models} modelKey={draft.modelKey ?? ""} reasoningEffort={draft.reasoningEffort ?? ""} disabled={busy}
              modelLabel="Thought execution model" effortLabel="Thought reasoning effort" defaultLabel="Unattended default"
              onChange={(modelKey, reasoningEffort) => setDraft({ ...draft, modelKey: modelKey || null, reasoningEffort: reasoningEffort || null })} />
          </Form.Item>
          <Flex wrap gap={token.paddingXS}><Button type="primary" htmlType="submit" aria-label={editing ? "Save thought" : "Create thought"}
            aria-busy={pendingAction === (editing ? `thoughts/${editing}` : "thoughts")}
            loading={pendingAction === (editing ? `thoughts/${editing}` : "thoughts")} disabled={!draft.thinkingPrompt.trim() || invalidInterval}>{editing ? "Save thought" : "Create thought"}</Button>
            {editing ? <Button disabled={busy} onClick={() => { setEditing(null); setDraft(blank); setIntervalUnit(3600); }}>Cancel edit</Button> : null}
            <Button onClick={() => void reload()} disabled={busy}>Refresh thoughts</Button><Button onClick={() => onWork()}>View runs</Button></Flex>
        </Form>
        {review.items.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No thoughts configured. Add one when you want this agent to periodically review whether action is useful." /> :
          <>
          <AdminCollectionToolbar label="thoughts" value={search} onChange={setSearch} />
          <Table<ThoughtRegistration> key={tableVersion} aria-label="Thoughts table" className="admin-collection-table" size="small" rowKey="registrationId"
            dataSource={review.items.filter(item => [item.thinkingPrompt, item.enabled ? "Enabled" : "Disabled", thoughtIntervalLabel(item.intervalSeconds),
              thoughtOutcome(item), item.executionStatus ?? "", item.effectiveModelKey ?? item.modelKey ?? "Unattended default"]
              .some(value => value.toLowerCase().includes(search.trim().toLowerCase()))).sort((a, b) => Number(b.registrationId === selection?.registrationId) - Number(a.registrationId === selection?.registrationId))}
            scroll={{ x: 1270 }} pagination={pagination}
            locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No matches. Clear search or filters to see all results." /> }}
            columns={[
              { title: "Thinking prompt", key: "prompt", width: 350, ellipsis: true,
                render: (_, item) => <Button type="link" size="small" className="admin-collection-name" title={item.thinkingPrompt}
                  data-automation-id={item.registrationId} aria-label={`View thought: ${item.thinkingPrompt}`} aria-expanded={expanded.includes(item.registrationId)}
                  onClick={() => setExpanded(expanded.includes(item.registrationId) ? [] : [item.registrationId])}>{item.thinkingPrompt}</Button> },
              { title: "Cadence", key: "cadence", width: 170,
                filters: [{ text: "Enabled", value: true }, { text: "Disabled", value: false }], onFilter: (value, item) => item.enabled === value,
                render: (_, item) => <Tag>{item.enabled ? thoughtIntervalLabel(item.intervalSeconds) : "Disabled"}</Tag> },
              { title: "Last run", key: "run", width: 220, render: (_, item) => item.lastWorkItemId ? <Button type="link" size="small" aria-label={`View run: ${item.thinkingPrompt}`} onClick={() => onWork(item.lastWorkItemId!)}>{date(item.lastRunAt)}</Button> : "Not yet" },
              { title: "Last outcome", key: "outcome", width: 180,
                filters: [...new Set(review.items.map(thoughtOutcome))].map(value => ({ text: value, value })),
                onFilter: (value, item) => thoughtOutcome(item) === value,
                render: (_, item) => <Tag color={item.executionStatus === "WaitingForApproval" ? "warning" : item.executionStatus === "Running" ? "processing" : undefined}>
                  {thoughtOutcome(item)}</Tag> },
              { title: "Next review", key: "next", width: 220,
                sorter: (a, b) => (a.enabled ? a.nextRunAt ?? "" : "").localeCompare(b.enabled ? b.nextRunAt ?? "" : ""),
                render: (_, item) => item.enabled ? date(item.nextRunAt) : "Disabled" },
              { title: "Model", key: "model", width: 180, ellipsis: true, render: (_, item) => item.effectiveModelKey ?? item.modelKey ?? "Unattended default" }
            ]}
            expandable={{ fixed: "left", expandedRowKeys: expanded, onExpand: (open, item) => setExpanded(open ? [item.registrationId] : []),
              expandedRowRender: item => <Flex vertical gap={token.padding} style={{ whiteSpace: "normal" }} role="region" aria-label="Thought details">
                <Descriptions bordered size="small" column={1} {...detailLayout}>
                  <Descriptions.Item label="Thinking prompt"><span style={{ whiteSpace: "pre-wrap" }}>{item.thinkingPrompt}</span></Descriptions.Item>
                  <Descriptions.Item label="Model">{item.effectiveModelKey ?? item.modelKey ?? "Unattended default"}</Descriptions.Item>
                  <Descriptions.Item label="Last run">{date(item.lastRunAt)}</Descriptions.Item>
                  <Descriptions.Item label="Last outcome">{thoughtOutcome(item)}</Descriptions.Item>
                </Descriptions>
              <Flex wrap gap={token.paddingXS}><Button aria-label="Run now" loading={item.registrationId in startingRuns} aria-busy={item.registrationId in startingRuns} disabled={busy || item.registrationId in startingRuns || !item.enabled || ["Queued", "Running", "WaitingForApproval", "WaitingToRetry"].includes(item.executionStatus ?? "")} onClick={() => void action(`thoughts/${item.registrationId}/run`, { expectedRevision: item.revision })}>{item.registrationId in startingRuns ? "Starting…" : "Run now"}</Button>
                <Button disabled={busy} onClick={() => edit(item)}>Edit thought</Button>
                <Button disabled={busy} onClick={() => void action(`thoughts/${item.registrationId}`, { ...item, expectedRevision: item.revision, enabled: !item.enabled }, "PUT")}>{item.enabled ? "Disable thought" : "Enable thought"}</Button>
                <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: "Delete this thought registration?", content: "Stops future activations. Existing runs remain inspectable and can be cancelled in Runs.", okText: "Delete thought", danger: true,
                  onOk: () => action(`thoughts/${item.registrationId}/delete`, { expectedRevision: item.revision }) })}>Delete thought</Button>
                {item.lastWorkItemId ? <Button onClick={() => onWork(item.lastWorkItemId!)}>View run</Button> : null}</Flex>
              </Flex>
            }} />
          </>}
      </> : null}
    </Flex></div>
  </section>;
}
