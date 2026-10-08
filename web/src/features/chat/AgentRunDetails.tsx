import { ConversationDestination } from "./AutomationDestination";
import { useEffect, useRef, useState } from "react";
import { App, Alert, Button, Descriptions, Drawer, Empty, Flex, Spin, Tag, Typography, theme } from "antd";
import { CheckCircleOutlined, ClockCircleOutlined, CloseCircleOutlined, ExclamationCircleOutlined, LoadingOutlined, RedoOutlined, StopOutlined } from "@ant-design/icons";
import { cancelAgentRun, decideAgentRunApproval, listAgentRuns, type AgentRun } from "../../services/api";
import { useCursorPages } from "./useCursorPages";
import { DrawerListFooter } from "./DrawerListFooter";
import { DiagnosticDetails } from "./DiagnosticDetails";
import { runActivationLabel, runOutcomeLabel, runStatusLabel } from "./runPresentation";
import { confirmAction } from "../../app/confirmAction";
import { formatChatTime } from "./chatTime";

export function AgentRunStatus({ run }: { run: AgentRun }) {
  const status = ({ queued: { icon: <ClockCircleOutlined />, color: undefined }, running: { icon: <LoadingOutlined />, color: "processing" },
    needsApproval: { icon: <ExclamationCircleOutlined />, color: "warning" }, retrying: { icon: <RedoOutlined />, color: "gold" },
    completed: { icon: <CheckCircleOutlined />, color: "success" }, failed: { icon: <CloseCircleOutlined />, color: "error" },
    cancelled: { icon: <StopOutlined />, color: undefined } } as const)[run.status as "queued"];
  return <Tag variant="filled" icon={status?.icon} color={status?.color} className="background-work-status">{runStatusLabel(run.status)}</Tag>;
}
function ReadingRegion({ label, text }: { label: string; text: string }) {
  return <section className="background-work-detail-section">
    <Typography.Text type="secondary" className="background-work-detail-label">{label}</Typography.Text>
    <div role="region" aria-label={label} tabIndex={0} className="background-work-long-content ac-scroll-pane">
      <Typography.Paragraph className="background-work-detail-body">{text}</Typography.Paragraph>
    </div>
  </section>;
}
export function AgentRunDetails({ run, onChange }: { run: AgentRun; onChange: (run: AgentRun) => void }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false); const [now, setNow] = useState(Date.now);
  const action = useRef(false); const confirmation = useRef(false); const epoch = useRef(0);
  const latest = useRef(run); latest.current = run;
  useEffect(() => { epoch.current++; setError(null); setBusy(false); setConfirming(false); action.current = false; confirmation.current = false; return () => { epoch.current++; }; }, [run.agentRunId]);
  useEffect(() => {
    if (!run.approval) return;
    setNow(Date.now());
    const timer = window.setInterval(() => setNow(Date.now()), 1_000);
    return () => window.clearInterval(timer);
  }, [run.approval?.expiresAt]);
  const approvalExpired = !!run.approval && new Date(run.approval.expiresAt).getTime() <= now;
  async function perform(decision: "cancel" | "approve" | "reject") {
    if (action.current || confirmation.current || (decision !== "cancel" && approvalExpired)) return;
    const generation = epoch.current;
    confirmation.current = true; setConfirming(true);
    confirmAction(modal, {
      title: decision === "cancel" ? "Cancel this run?" : decision === "approve" ? "Approve this exact action?" : "Reject this action?",
      content: decision === "cancel" ? "The run will stop. Confirmed external effects remain recorded." : run.approval?.preview,
      okText: decision === "cancel" ? "Cancel run" : decision === "approve" ? "Approve action" : "Reject action",
      danger: decision !== "approve",
      onOk: () => {
        if (generation !== epoch.current) return;
        if (latest.current.revision !== run.revision || (decision !== "cancel" && new Date(run.approval!.expiresAt).getTime() <= Date.now())) {
          setError("The run changed or the approval expired. Review the current details and try again.");
          return;
        }
        return execute(decision);
      },
      afterClose: () => { if (generation === epoch.current) { confirmation.current = false; setConfirming(false); } }
    });
  }
  async function execute(decision: "cancel" | "approve" | "reject") {
    if (action.current) return;
    const generation = epoch.current; action.current = true; setBusy(true); setError(null);
    try {
      const updated = await (decision === "cancel" ? cancelAgentRun(run) : decideAgentRunApproval(run, decision));
      if (generation === epoch.current) onChange(updated);
    } catch (reason) {
      if (generation === epoch.current) setError(reason instanceof Error ? reason.message : "The run changed. Refresh and try again.");
    } finally { if (generation === epoch.current) { action.current = false; setBusy(false); } }
  }
  return <Flex vertical gap={token.padding} className="agent-run-details" data-agent-run-id={run.agentRunId}>
    <Flex align="center" wrap gap={token.paddingXS}><AgentRunStatus run={run} />
      <Typography.Text type="secondary">{runActivationLabel(run.activationKind)}</Typography.Text>
      {run.outcome?.attentionRequired ? <Typography.Text><ExclamationCircleOutlined /> Needs attention</Typography.Text> : null}
    </Flex>
    {run.sourceBackgroundSessionId ? <Typography.Text type="secondary">Background result from <ConversationDestination sessionId={run.sourceBackgroundSessionId} /></Typography.Text> : null}
    {run.progress ? <Typography.Paragraph style={{ marginBottom: 0 }}>{run.progress}</Typography.Paragraph> : null}
    <Descriptions size="small" column={1} items={[
      { key: "model", label: "Model", children: run.modelCatalogKey },
      { key: "attempt", label: "Attempt", children: `${run.attemptCount} of ${run.maxAttempts}` },
      { key: "updated", label: "Updated", children: <time dateTime={run.updatedAt}>{formatChatTime(run.updatedAt) ?? "Unknown time"}</time> }
    ]} />
    {run.nextRetryAt ? <Typography.Text type="secondary">Retry scheduled for {new Date(run.nextRetryAt).toLocaleString()}</Typography.Text> : null}
    {run.outcome ? <ReadingRegion label={runOutcomeLabel(run.outcome.kind)} text={run.outcome.summary || "No action was needed."} /> : null}
    {run.failureSummary ? <Alert type="error" showIcon title={run.failureSummary} action={<DiagnosticDetails fields={{
      diagnosticId: run.diagnosticId, sessionId: run.sessionId, agentRunId: run.agentRunId, code: run.failureCode
    }} />} /> : null}
    {run.knownEffectSummary ? <ReadingRegion label="Confirmed effects" text={run.knownEffectSummary} /> : null}
    {run.approval ? <Flex vertical gap={token.paddingXS}>
      <ReadingRegion label="Action awaiting approval" text={run.approval.preview} />
      <Typography.Text type="secondary" role={approvalExpired ? "status" : undefined}>{approvalExpired ? "Approval expired. Waiting for the run to update." : `Expires ${new Date(run.approval.expiresAt).toLocaleString()}`}</Typography.Text>
      <Flex wrap gap={token.paddingXS}><Button type="primary" disabled={busy || confirming || approvalExpired} loading={busy} onClick={() => void perform("approve")}>Approve action</Button>
        <Button disabled={busy || confirming || approvalExpired} onClick={() => void perform("reject")}>Reject action</Button></Flex>
    </Flex> : null}
    {run.cancellationAvailable ? <Button danger style={{ alignSelf: "flex-start" }} disabled={busy || confirming} onClick={() => void perform("cancel")}>Cancel run</Button> : null}
    {run.cancellationRequested && run.status !== "cancelled" ? <Typography.Text type="secondary" role="status">Cancellation requested</Typography.Text> : null}
    {error ? <Alert type="error" showIcon title={error} /> : null}
  </Flex>;
}
export function SessionRunHistory({ sessionId, open }: { sessionId: string; open: boolean }) {
  const page = useCursorPages(sessionId, open, listAgentRuns);
  const { token } = theme.useToken();
  return <Flex vertical gap={token.padding}>
    <Typography.Text strong>Run history</Typography.Text>
    {page.loading ? <Spin aria-label="Loading run history" /> : page.items.length === 0 && !page.error ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No runs yet" /> : null}
    {page.items.map(run => <section key={run.agentRunId} className="agent-run-history-item" aria-label={`${runActivationLabel(run.activationKind)} run`}>
      <AgentRunDetails run={run} onChange={updated => page.replace(rows => rows.map(row => row.agentRunId === updated.agentRunId ? updated : row))} />
    </section>)}
    {page.error ? <Alert type="error" showIcon title={page.error} /> : null}
    <DrawerListFooter loadingMore={page.loadingMore} hasMore={page.hasMore} error={page.error} count={page.items.length} onLoadMore={() => void page.loadMore()} onRetry={() => void page.retry()} />
  </Flex>;
}
export function AgentRunDetailDrawer({ run, open, wide = true, onClose, onChange, afterClose }: {
  run: AgentRun | null; open: boolean; wide?: boolean; onClose: () => void; onChange: (run: AgentRun) => void; afterClose?: () => void;
}) {
  return <Drawer title="Run details" open={open} onClose={onClose} size={wide ? "min(640px, 100vw)" : "100vw"}
    className="background-work-drawer run-details-drawer" afterOpenChange={visible => { if (!visible) afterClose?.(); }}>
    {run ? <AgentRunDetails run={run} onChange={onChange} /> : <Spin aria-label="Loading run details" />}
  </Drawer>;
}
