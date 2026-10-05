import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, App, Button, Collapse, Descriptions, Empty, Flex, Form, Input, InputNumber, Spin, Switch, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { listModels, type ModelDescriptor, type WorkItem, type WorkItemResult } from "../../services/api";
import { instanceContinuityRequest as request, type ExperienceReview, type ThoughtDraft, type ThoughtRegistration, type ThoughtReview } from "../../services/adminApi";
import { BackgroundWorkDrawer } from "../chat/BackgroundWorkDrawer";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";

import { ExecutionModelFields } from "./ExecutionModelFields";

const date = (value: string | null) => value ? new Date(value).toLocaleString() : "Not yet";

// A mutation invalidates older reads; polling cannot supersede an in-progress owner action.
function useResponseOrder() {
  const order = useRef({ generation: 0, mutating: false });
  useEffect(() => () => { order.current.generation++; }, []);
  return order;
}

export function InstanceContinuitySection({ instanceId }: { instanceId: string }) {
  const { token } = theme.useToken();
  const [workOpen, setWorkOpen] = useState(false);
  const [wide, setWide] = useState(window.innerWidth >= 768);
  useEffect(() => {
    const resize = () => setWide(window.innerWidth >= 768);
    window.addEventListener("resize", resize); return () => window.removeEventListener("resize", resize);
  }, []);
  return <Flex vertical gap={token.padding}>
    <ExperienceSection key={`experience-${instanceId}`} instanceId={instanceId} onWork={() => setWorkOpen(true)} />
    <ThoughtSection key={`thought-${instanceId}`} instanceId={instanceId} onWork={() => setWorkOpen(true)} />
    <BackgroundWorkDrawer sessionId={instanceId} open={workOpen} wide={wide} onClose={() => setWorkOpen(false)}
      load={async id => (await request<{ items: WorkItem[] }>(id, "work-items")).items}
      loadResult={(id, workId) => request<WorkItemResult>(id, `work-items/${workId}/result`)}
      cancel={(id, workId, expectedRevision) => request<WorkItem>(id, `work-items/${workId}/cancel`, "POST", { expectedRevision })}
      approve={(id, workId, approvalId, expectedRevision, expectedApprovalRevision, actionHash) => request<WorkItem>(id,
        `work-items/${workId}/approvals/${approvalId}/approve`, "POST", { expectedRevision, expectedApprovalRevision, actionHash })}
      reject={(id, workId, approvalId, expectedRevision, expectedApprovalRevision, actionHash) => request<WorkItem>(id,
        `work-items/${workId}/approvals/${approvalId}/reject`, "POST", { expectedRevision, expectedApprovalRevision, actionHash })} />
  </Flex>;
}

function Failure({ error, reload }: { error: AdminFailureNotice | null; reload: () => void }) {
  return error ? <Alert type="error" showIcon title={error.message} action={<Button onClick={reload}>Reload</Button>}
    description={error.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: error.diagnosticId }} /> : undefined} /> : null;
}

function ExperienceSection({ instanceId, onWork }: { instanceId: string; onWork: () => void }) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const [review, setReview] = useState<ExperienceReview | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [session, setSession] = useState("");
  const order = useResponseOrder();
  const reload = useCallback(async () => {
    if (order.current.mutating) return;
    const generation = ++order.current.generation;
    setLoading(true); setError(null);
    try { const next = await request<ExperienceReview>(instanceId, "experience"); if (generation === order.current.generation) setReview(next); }
    catch (reason) { if (generation === order.current.generation) setError(describeAdminError(reason, "Experience could not be loaded.")); }
    finally { if (generation === order.current.generation) setLoading(false); }
  }, [instanceId, order]);
  useEffect(() => { void reload(); }, [reload]);
  useEffect(() => {
    if (loading || !review?.items.some(item => ["Pending", "Queued", "Running", "WaitingToRetry"].includes(item.status))) return;
    const timer = window.setInterval(() => {
      if (order.current.mutating) return;
      const generation = ++order.current.generation;
      void request<ExperienceReview>(instanceId, "experience")
        .then(next => { if (generation === order.current.generation) setReview(next); })
        .catch(reason => { if (generation === order.current.generation) setError(describeAdminError(reason, "Experience status could not be refreshed.")); });
    }, 2000);
    return () => window.clearInterval(timer);
  }, [instanceId, review, order, loading]);
  async function mutate(path: string, method: string, body: unknown) {
    order.current.mutating = true; ++order.current.generation;
    setLoading(false);
    setBusy(true); setError(null);
    try { setReview(await request<ExperienceReview>(instanceId, path, method, body)); }
    catch (reason) { setError(describeAdminError(reason, "Experience update failed. Reload and try again.")); }
    finally { order.current.mutating = false; setBusy(false); }
  }
  return <section className="admin-definition-panel" aria-label="Experience">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Experience</Typography.Title>
      <Typography.Text type="secondary">Derived observations about past work, kept separately from learned memory.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading experience" /> : null}
      <Failure error={error} reload={() => void reload()} />
      {review ? <>
        <Flex wrap align="center" gap={token.paddingXS}><Switch aria-label="Enable experience" checked={review.enabled} disabled={busy}
          onChange={enabled => void mutate("experience/configuration", "PUT", { expectedRevision: review.settingsRevision, enabled })} />
          <Typography.Text>{review.enabled ? "Enabled · completed work may be retrospected" : "Disabled · experience is not supplied to the agent"}</Typography.Text>
          <Button onClick={() => void reload()} disabled={busy}>Refresh experience</Button></Flex>
        <Form layout="vertical" onFinish={() => void mutate("experience/checkpoints", "POST", { sessionId: session.trim() })}>
          <Form.Item label="Source Session id" extra="Only completed observable work in this instance is eligible. A repeated checkpoint creates no duplicate.">
            <Input aria-label="Retrospection source Session" placeholder="Session id from conversation details" value={session} onChange={e => setSession(e.target.value)} disabled={busy || !review.enabled} />
          </Form.Item>
          <Flex wrap gap={token.paddingXS}><Button htmlType="submit" disabled={busy || !review.enabled || !session.trim()}>Retrospect now</Button>
            <Button onClick={onWork}>View background work</Button>
            <Button danger disabled={busy || review.items.length === 0} onClick={() => confirmAction(modal, {
              title: "Reset this instance's experience?", content: "Removes derived experience. Source conversations, learned memory and persona remain available.",
              okText: "Reset experience", danger: true, onOk: () => mutate("experience/reset", "POST", {})
            })}>Reset experience</Button></Flex>
        </Form>
        {review.items.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No experience yet. Enable experience and retrospect a completed task." /> :
          <Collapse items={review.items.map(item => ({ key: item.experienceId,
            label: <Flex vertical gap={token.paddingXS}><Typography.Text strong>{item.content?.goal ?? (item.status === "Failed" ? "Retrospection failed" : item.status === "Cancelled" ? "Retrospection cancelled" : "Retrospection in progress")}</Typography.Text>
              <Flex wrap gap={token.paddingXS}><Tag color={item.status === "Failed" ? "error" : undefined}>{item.status}</Tag>
                <Tag>{item.eligibleForContext ? "Eligible for context" : item.visibility === "Suppressed" ? "Suppressed" : "Not in context"}</Tag>
                <Typography.Text type="secondary">{item.checkpointAt ? `Checkpoint captured: ${date(item.checkpointAt)}` : `Source created: ${date(item.sourceCreatedAt ?? item.sourceAt)}`} · {item.definitionId} v{item.definitionVersion}</Typography.Text></Flex></Flex>,
            children: <Flex vertical gap={token.padding}>
              <Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>Source {item.sourceKind} {item.sourceId} · checkpoint {item.throughCursor} · model {item.modelKey}</Typography.Text>
              <Typography.Text type="secondary">Source created: {date(item.sourceCreatedAt ?? item.sourceAt)} · Checkpoint captured: {item.checkpointAt ? date(item.checkpointAt) : "Not recorded (legacy checkpoint)"}</Typography.Text>
              {item.failureSummary ? <Alert type="error" showIcon title={item.failureSummary} description={item.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: item.diagnosticId }} /> : undefined} /> : null}
              {item.content ? <Descriptions column={1} size="small">{(["attempts", "decisions", "outcomes", "corrections", "unresolved", "difficulties", "lessons"] as const)
                .filter(key => item.content![key].length > 0).map(key => <Descriptions.Item key={key} label={key[0].toUpperCase() + key.slice(1)}>
                  {item.content![key].join(" · ")}</Descriptions.Item>)}</Descriptions> : null}
              <Flex wrap gap={token.paddingXS}>
                <Button disabled={busy} onClick={() => void mutate(`experience/${item.experienceId}`, "PUT", { expectedRevision: item.revision, visibility: item.visibility === "Suppressed" ? "Eligible" : "Suppressed" })}>
                  {item.visibility === "Suppressed" ? "Include in context" : "Suppress experience"}</Button>
                <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: "Delete this experience?", content: "The source work remains available. This checkpoint will not be regenerated.", okText: "Delete experience", danger: true,
                  onOk: () => mutate(`experience/${item.experienceId}`, "PUT", { expectedRevision: item.revision, visibility: "Deleted" }) })}>Delete experience</Button>
              </Flex>
            </Flex>
          }))} />}
      </> : null}
    </Flex></div>
  </section>;
}

const blank: ThoughtDraft = { expectedRevision: 0, enabled: false, intervalSeconds: 3600, thinkingPrompt: "", modelKey: null, reasoningEffort: null };
function ThoughtSection({ instanceId, onWork }: { instanceId: string; onWork: () => void }) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const [review, setReview] = useState<ThoughtReview | null>(null);
  const [models, setModels] = useState<ModelDescriptor[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [editing, setEditing] = useState<string | null>(null);
  const [draft, setDraft] = useState<ThoughtDraft>(blank);
  const order = useResponseOrder();
  const reload = useCallback(async () => {
    if (order.current.mutating) return;
    const generation = ++order.current.generation;
    setLoading(true); setError(null);
    try { const [thoughts, available] = await Promise.all([request<ThoughtReview>(instanceId, "thoughts"), listModels()]); if (generation === order.current.generation) { setReview(thoughts); setModels(available.models.filter(model => model.tools)); } }
    catch (reason) { if (generation === order.current.generation) setError(describeAdminError(reason, "Initiative could not be loaded.")); }
    finally { if (generation === order.current.generation) setLoading(false); }
  }, [instanceId, order]);
  useEffect(() => { void reload(); }, [reload]);
  useEffect(() => {
    if (loading) return;
    const timer = window.setInterval(() => {
      if (order.current.mutating) return;
      const generation = ++order.current.generation;
      void request<ThoughtReview>(instanceId, "thoughts").then(next => { if (generation === order.current.generation) setReview(next); })
        .catch(reason => { if (generation === order.current.generation) setError(describeAdminError(reason, "Initiative status could not be refreshed.")); });
    }, 5000);
    return () => window.clearInterval(timer);
  }, [instanceId, order, loading]);
  async function action(path: string, body: unknown, method = "POST") {
    order.current.mutating = true; ++order.current.generation;
    setLoading(false);
    setBusy(true); setError(null);
    try {
      await request(instanceId, path, method, body);
      setReview(await request<ThoughtReview>(instanceId, "thoughts"));
      if (path === "thoughts" || method === "PUT") { setEditing(null); setDraft(blank); }
    } catch (reason) { setError(describeAdminError(reason, "Initiative update failed. Reload for the current revision.")); }
    finally { order.current.mutating = false; setBusy(false); }
  }
  function edit(item: ThoughtRegistration) {
    setEditing(item.registrationId); setDraft({ expectedRevision: item.revision, enabled: item.enabled,
      intervalSeconds: item.intervalSeconds, thinkingPrompt: item.thinkingPrompt, modelKey: item.modelKey, reasoningEffort: item.reasoningEffort });
  }
  return <section className="admin-definition-panel" aria-label="Initiative">
    <div className="admin-definition-panel-heading"><Typography.Title level={4}>Initiative / Thought activation</Typography.Title>
      <Typography.Text type="secondary">Give the agent a bounded opportunity to review context and decide whether useful action exists.</Typography.Text></div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding}>
      {loading ? <Spin aria-label="Loading initiative" /> : null}
      <Failure error={error} reload={() => void reload()} />
      {review ? <>
        <Form layout="vertical" onFinish={() => void action(editing ? `thoughts/${editing}` : "thoughts", draft, editing ? "PUT" : "POST")}>
          <Form.Item label="Thinking prompt" extra={<Flex vertical gap={token.paddingXS}><Typography.Text type="secondary">{draft.thinkingPrompt.length} / 2000 characters</Typography.Text>
            <span>For example: Review recent experience for repeated problems. Improve only when meaningful; otherwise do nothing. Tools and approvals still follow current policy.</span></Flex>}>
            <Input.TextArea aria-label="Thinking prompt" rows={4} maxLength={2000} value={draft.thinkingPrompt} disabled={busy}
              onChange={e => setDraft({ ...draft, thinkingPrompt: e.target.value })} />
          </Form.Item>
          <Flex wrap gap={token.padding}>
            <Form.Item label="Interval (hours)"><InputNumber aria-label="Thought interval hours" min={1} max={168} precision={0} value={draft.intervalSeconds / 3600} disabled={busy}
              onChange={value => setDraft({ ...draft, intervalSeconds: (value ?? 1) * 3600 })} /></Form.Item>
            <Form.Item label="Enabled"><Switch aria-label="Enable thought activation" checked={draft.enabled} disabled={busy} onChange={enabled => setDraft({ ...draft, enabled })} /></Form.Item>
          </Flex>
          <Form.Item label="Execution model" extra="Uses this instance's unattended default unless you choose a model. More frequent runs use more model and tool resources.">
            <ExecutionModelFields models={models} modelKey={draft.modelKey ?? ""} reasoningEffort={draft.reasoningEffort ?? ""} disabled={busy}
              modelLabel="Thought execution model" effortLabel="Thought reasoning effort" defaultLabel="Unattended default"
              onChange={(modelKey, reasoningEffort) => setDraft({ ...draft, modelKey: modelKey || null, reasoningEffort: reasoningEffort || null })} />
          </Form.Item>
          <Flex wrap gap={token.paddingXS}><Button type="primary" htmlType="submit" loading={busy} disabled={!draft.thinkingPrompt.trim()}>{editing ? "Save thought" : "Create thought"}</Button>
            {editing ? <Button disabled={busy} onClick={() => { setEditing(null); setDraft(blank); }}>Cancel edit</Button> : null}
            <Button onClick={() => void reload()} disabled={busy}>Refresh initiative</Button><Button onClick={onWork}>View thought executions</Button></Flex>
        </Form>
        {review.items.length === 0 ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No thought activations configured. The agent stays quiet until you enable one." /> :
          <Collapse items={review.items.map(item => ({ key: item.registrationId, label: <Flex vertical gap={token.paddingXS} style={{ minWidth: 0, width: "100%" }}>
            <Typography.Paragraph ellipsis={{ rows: 2 }} style={{ marginBottom: 0, overflowWrap: "anywhere" }}>{item.thinkingPrompt}</Typography.Paragraph>
            <Flex wrap gap={token.paddingXS}><Tag>{item.enabled ? `Every ${item.intervalSeconds / 3600} hour${item.intervalSeconds === 3600 ? "" : "s"}` : "Disabled"}</Tag>
              {item.executionStatus ? <Tag color={item.executionStatus === "WaitingForApproval" ? "warning" : item.executionStatus === "Running" ? "processing" : undefined}>
                {item.executionStatus === "WaitingForApproval" ? "Needs approval" : item.lastOutcome ?? item.executionStatus}</Tag> : null}
              <Typography.Text type="secondary">Next: {item.enabled ? date(item.nextRunAt) : "Disabled"}</Typography.Text></Flex></Flex>,
            children: <Flex vertical gap={token.padding}><Typography.Paragraph style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere" }}>{item.thinkingPrompt}</Typography.Paragraph>
              <Descriptions size="small" column={1}><Descriptions.Item label="Model">{item.effectiveModelKey ?? item.modelKey ?? "Unattended default"}</Descriptions.Item>
                <Descriptions.Item label="Last activation">{date(item.lastRunAt)}</Descriptions.Item><Descriptions.Item label="Last outcome">{item.lastOutcome ?? item.executionStatus ?? "Not yet"}</Descriptions.Item></Descriptions>
              <Flex wrap gap={token.paddingXS}><Button disabled={busy || !item.enabled || ["Queued", "Running", "WaitingForApproval", "WaitingToRetry"].includes(item.executionStatus ?? "")} onClick={() => void action(`thoughts/${item.registrationId}/run`, { expectedRevision: item.revision })}>Run now</Button>
                <Button disabled={busy} onClick={() => edit(item)}>Edit thought</Button>
                <Button disabled={busy} onClick={() => void action(`thoughts/${item.registrationId}`, { ...item, expectedRevision: item.revision, enabled: !item.enabled }, "PUT")}>{item.enabled ? "Disable thought" : "Enable thought"}</Button>
                <Button danger disabled={busy} onClick={() => confirmAction(modal, { title: "Delete this thought registration?", content: "Stops future activations. Already admitted background work remains inspectable and can be cancelled there.", okText: "Delete thought", danger: true,
                  onOk: () => action(`thoughts/${item.registrationId}/delete`, { expectedRevision: item.revision }) })}>Delete thought</Button>
                {item.lastWorkItemId ? <Button onClick={onWork}>Inspect execution</Button> : null}</Flex>
            </Flex>
          }))} />}
      </> : null}
    </Flex></div>
  </section>;
}
