import { sessionPath } from "../../app/sessionRoute";
import { followAppLink } from '../../app/appLink';
import { CheckCircleOutlined, ClockCircleOutlined, CloseCircleOutlined, ExclamationCircleOutlined, LoadingOutlined, RedoOutlined, StopOutlined } from "@ant-design/icons";
import { Alert, App, Button, Collapse, Descriptions, Drawer, Empty, Flex, Spin, Tag, Typography, theme } from "antd";
import { useEffect, useRef, useState } from "react";
import { confirmAction } from "../../app/confirmAction";
import type { ActivitySource, RunActions } from "../../services/activitySource";
import { type AgentRun } from "../../services/api";
import { IdentityError } from "../../services/identity";
import { ConversationDestination } from "./AutomationDestination";
import { formatChatTime } from "./chatTime";
import { DiagnosticDetails } from "./DiagnosticDetails";
import { DrawerListFooter } from "./DrawerListFooter";
import { runActivationLabel, runBudgetLabel, runOutcomeLabel, runRecoveryLabel, runStatusLabel } from "./runPresentation";
import { useCursorPages } from "./useCursorPages";

export function AgentRunStatus({ run }: { run: AgentRun }) {
  const status = ({ queued: { icon: <ClockCircleOutlined />, color: undefined }, running: { icon: <LoadingOutlined />, color: "processing" },
    waitingForSignal: { icon: <ClockCircleOutlined />, color: "processing" }, needsApproval: { icon: <ExclamationCircleOutlined />, color: "warning" }, retrying: { icon: <RedoOutlined />, color: "gold" },
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
export function AgentRunDetails({ run, onChange, showContextProvenance = false, actions }: { run: AgentRun; onChange: (run: AgentRun) => void; showContextProvenance?: boolean; actions?: RunActions }) {
  const { token } = theme.useToken(); const { modal } = App.useApp();
  const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const [recoveryRequired, setRecoveryRequired] = useState(false);
  const [restricted, setRestricted] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const action = useRef(false); const confirmation = useRef(false); const epoch = useRef(0);
  const latest = useRef(run); latest.current = run;
  useEffect(() => { epoch.current++; setError(null); setRecoveryRequired(false); setRestricted(false); setBusy(false); setConfirming(false); action.current = false; confirmation.current = false; return () => { epoch.current++; }; }, [run.agentRunId]);
  async function perform() {
    if (!actions || recoveryRequired || action.current || confirmation.current) return;
    const generation = epoch.current;
    confirmation.current = true; setConfirming(true);
    confirmAction(modal, {
      title: "Cancel this run?",
      content: "The run will stop. Confirmed external effects remain recorded.",
      okText: "Cancel run",
      danger: true,
      onOk: () => {
        if (generation !== epoch.current) return;
        if (latest.current.revision !== run.revision) {
          setError("The run changed. Review the current details and try again.");
          return;
        }
        return execute();
      },
      afterClose: () => { if (generation === epoch.current) { confirmation.current = false; setConfirming(false); } }
    });
  }
  async function execute() {
    if (!actions || action.current) return;
    const generation = epoch.current; action.current = true; setBusy(true); setError(null);
    try {
      const updated = await actions.cancel(run);
      if (generation === epoch.current) onChange(updated);
    } catch (reason) {
      if (generation === epoch.current) { setError(reason instanceof Error ? reason.message : "The run changed. Refresh and try again."); if (actions) { setRecoveryRequired(true); if (reason instanceof IdentityError && [401,403,404].includes(reason.status)) setRestricted(true); } }
    } finally { if (generation === epoch.current) { action.current = false; setBusy(false); } }
  }
  async function recover() {
    if (!actions || action.current) return;
    const generation = epoch.current; action.current = true; setBusy(true);
    try { const saved = await actions.read(run); if (generation === epoch.current) { onChange(saved); setRecoveryRequired(false); setRestricted(false); setError(null); } }
    catch (reason) { if (generation === epoch.current) setError(reason instanceof Error ? reason.message : "Run access could not be read."); }
    finally { if (generation === epoch.current) { action.current = false; setBusy(false); } }
  }
  if (restricted) return <Alert type="error" showIcon title={error ?? "This Run is unavailable."} action={<Button disabled={busy} onClick={() => void recover()}>Reload run</Button>} />;
  return <Flex vertical gap={token.padding} className="agent-run-details" data-agent-run-id={run.agentRunId}>
    <Flex align="center" wrap gap={token.paddingXS}><AgentRunStatus run={run} />
      <Typography.Text type="secondary">{runActivationLabel(run.activationKind)}</Typography.Text>
      {run.outcome?.attentionRequired ? <Typography.Text><ExclamationCircleOutlined /> Needs attention</Typography.Text> : null}
    </Flex>
    {run.verificationSummary && <section aria-label="Verification" className="background-work-detail-section"><Flex vertical gap={token.paddingXS}>
      <Typography.Text strong>Verification · {run.verificationSummary.status.replace(/([a-z])([A-Z])/g, '$1 $2')}</Typography.Text>
      <Typography.Text type="secondary">{run.verificationSummary.passedChecks} of {run.verificationSummary.requiredChecks} required checks passed. Execution outcome and evaluation evidence are separate.</Typography.Text>
      {run.evaluationUrl && <Typography.Link href={run.evaluationUrl} onClick={followAppLink}>Inspect evaluation evidence</Typography.Link>}
    </Flex></section>}
    {run.sourceBackgroundSessionId ? <Typography.Text type="secondary">Background result from <ConversationDestination sessionId={run.sourceBackgroundSessionId} /></Typography.Text> : null}
    {run.taskIntent ? <ReadingRegion label="Original task" text={run.taskIntent} /> : null}
    {run.progress ? <Typography.Paragraph className="background-work-progress" style={{ marginBottom: 0 }}>{run.progress}</Typography.Paragraph> : null}
    {run.executionDiagnostic?.protocolRecovery ? <section aria-label="Response recovery" className="background-work-detail-section">
      <Flex vertical gap={token.paddingXS}>
        <Typography.Text strong>Response recovery</Typography.Text>
        <Typography.Text style={{ overflowWrap: "anywhere" }}>{runRecoveryLabel(run.executionDiagnostic.protocolRecovery.reason)}{run.executionDiagnostic.protocolRecovery.channel ? ` · ${runRecoveryLabel(run.executionDiagnostic.protocolRecovery.channel)}` : ""}</Typography.Text>
        <Typography.Text>{runRecoveryLabel(run.executionDiagnostic.protocolRecovery.eligibility)}{run.executionDiagnostic.protocolRecovery.strategy ? ` · ${runRecoveryLabel(run.executionDiagnostic.protocolRecovery.strategy)}` : ""}</Typography.Text>
        {run.executionDiagnostic.protocolRecovery.outcome ? <Typography.Text>{runRecoveryLabel(run.executionDiagnostic.protocolRecovery.outcome)}</Typography.Text> : null}
        {run.executionDiagnostic.protocolRecovery.strategy ? <Typography.Text type="secondary">Recorded tool results were retained. The correction cannot run substantive tools.</Typography.Text> : null}
      </Flex>
    </section> : null}
    {run.goal && <Typography.Paragraph><Typography.Link href={`${sessionPath(run.sessionId)}?goal=${encodeURIComponent(run.goal.goalId)}`} onClick={event => followAppLink(event)}>Inspect Session goal</Typography.Link> · accepted plan {run.goal.planRevision}<br /><Typography.Text type="secondary" style={{ overflowWrap: 'anywhere' }}>Checkpoint {run.goal.checkpointId}</Typography.Text></Typography.Paragraph>}
    <Descriptions size="small" column={1} items={[
      { key: "decision", label: "Model selection", children: run.modelDecision ? <Flex vertical><Typography.Text>{({ fixed: "Fixed", inherited: "Inherited", ranked: "Auto ranked", singleEligible: "Auto · only eligible model", configuredDefault: "Auto · configured default used", fixedAutoReasoning: "Fixed model · Auto reasoning" } as Record<string, string>)[run.modelDecision.basis] ?? "Recorded selection"} · {run.reasoningEffort ?? "Model default"}</Typography.Text><Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>{run.modelDecision.engineId} · {run.modelDecision.engineVersion} · policy {run.modelDecision.policyRevision} · settings {run.modelDecision.settingsRevision}{run.modelDecision.diagnosticCode ? ` · ${run.modelDecision.diagnosticCode}` : ''}</Typography.Text></Flex> : 'Captured fixed or inherited selection' },
      { key: "model", label: "Model", children: <Typography.Text className="background-work-progress">{run.modelOfferingId}</Typography.Text> },
      ...(run.configuration ? [
        { key: "configuration", label: "Run configuration", children: `Published Definition v${run.configuration.definitionVersion} · Instance revision ${run.configuration.instanceRevision} · Persona revision ${run.configuration.personaRevision}` },
        { key: "configurationHash", label: "Configuration SHA-256", children: <Typography.Text copyable style={{ overflowWrap: "anywhere" }}>{run.configuration.configurationHash}</Typography.Text> },
        { key: "resources", label: "Pinned resources", children: <Flex vertical gap={token.paddingXS}>{run.configuration.resources.length ? run.configuration.resources.map(resource => <Typography.Text key={resource.key} style={{ overflowWrap: "anywhere" }}>{resource.virtualPath} · {resource.byteLength.toLocaleString()} bytes · SHA-256 {resource.contentSha256}</Typography.Text>) : "None"}</Flex> }
      ] : []),
      ...(run.budget ? [
        { key: "budget", label: "Execution budget", children: `${runBudgetLabel(run.budget.class)} · ${runBudgetLabel(run.budget.source)} · ${run.budget.maxSteps} steps · ${run.budget.durationSeconds / 60} minutes` },
        { key: 'toolTimeout', label: 'Tool limits', children: `${run.budget.perToolSeconds}s ordinary · ${run.budget.sandboxCommandSeconds ?? 120}s sandbox · remaining Run deadline still applies` },
        { key: "consumed", label: "Consumed", children: `${run.budget.stepsConsumed} / ${run.budget.maxSteps} steps · ${(run.budget.activeExecutionMs / 1000).toFixed(1)}s active` },
        { key: "phase", label: "Phase", children: runBudgetLabel(run.budget.phase) },
        { key: "reason", label: "Termination reason", children: run.budget.terminationReason ? runBudgetLabel(run.budget.terminationReason) : 'None recorded' },
        { key: "cleanup", label: "Requested cleanup", children: runBudgetLabel(run.budget.cleanupStatus) },
        { key: "logout", label: "Sign-out", children: run.budget.logoutRequested == null ? 'Request not recorded · Unverified' : !run.budget.logoutRequested ? 'Not requested' : run.budget.logoutVerified ? 'Independently verified' : 'Requested · Unverified. Observed evidence remains in the result; Core has no sign-out-specific proof.' },
        { key: "closure", label: "Browser closure", children: `${run.budget.closureRequested == null ? 'Request not recorded' : run.budget.closureRequested ? 'Requested' : 'Not requested'} · ${run.budget.closureConfirmed ? 'Confirmed' : 'Unverified'}` },
        { key: "cleanupBlocked", label: "Cleanup blocker", children: run.budget.cleanupBlocked ? 'Unresolved browser failure; see result evidence.' : 'None recorded' }
      ] : []),
      ...(run.triggerOrigin ? [{ key: "trigger", label: "Trigger", children: <Flex vertical gap={token.paddingXS}><Typography.Text>{run.triggerOrigin.summary}</Typography.Text>{run.triggerOrigin.source ? <Typography.Link href={`/admin/connections/events?${run.triggerOrigin.source.kind === "builtin" ? `builtin=${encodeURIComponent(run.triggerOrigin.source.key ?? "")}` : `event=${run.triggerOrigin.source.eventId}`}`}>View Event definition</Typography.Link> : null}{run.triggerOrigin.triggerId ? <Typography.Text type="secondary">Subscription {run.triggerOrigin.triggerId}{run.triggerOrigin.triggerRevision ? ` · revision ${run.triggerOrigin.triggerRevision}` : ""}</Typography.Text> : null}</Flex> }] : []),
      ...(run.executionDiagnostic ? [
        { key: "checkpoint", label: "Last tool checkpoint", children: `${run.executionDiagnostic.lastTool ?? "No tool result"} · ${run.executionDiagnostic.recordedToolResults} recorded receipts` },
        { key: "remaining", label: "Remaining time", children: `${(run.executionDiagnostic.remainingBudgetMs / 1000).toFixed(1)}s at last checkpoint` },
        { key: "providerAttempts", label: "Recorded provider failures", children: run.executionDiagnostic.failedAttempts.length
          ? <Flex vertical gap={token.paddingXS}>{run.executionDiagnostic.failedAttempts.map((attempt, index) => <Typography.Text key={index} style={{ overflowWrap: "anywhere" }}>
            Attempt {attempt.attempt}: {attempt.code}{attempt.reason ? ` · ${attempt.reason}` : ""}{attempt.channel ? ` · ${runRecoveryLabel(attempt.channel)}` : ""}{attempt.maxOutputTokens ? ` · ${attempt.maxOutputTokens.toLocaleString()} token ceiling` : ""}{attempt.transport ? ` · ${attempt.transport}` : ""}
          </Typography.Text>)}</Flex> : "None recorded" }
      ] : []),
      { key: "attempt", label: "Attempt", children: `${run.attemptCount} of ${run.maxAttempts}` },
      { key: "updated", label: "Updated", children: <time dateTime={run.updatedAt}>{formatChatTime(run.updatedAt) ?? "Unknown time"}</time> }
    ]} />
    {showContextProvenance && run.contextProvenanceAvailable !== false ? <section aria-label="Context provenance" className="background-work-detail-section"><Flex vertical gap={token.paddingXS}>
      <Typography.Text strong>Context provenance</Typography.Text>
      <Typography.Paragraph type="secondary" style={{ margin: 0 }}>Read-only section metadata from the last model request. Contents and credentials are excluded.</Typography.Paragraph>
      {run.contextManifest ? <Collapse size="small" items={(["Core", "Runtime", "Evidence"] as const).map(layer => ({
        key: layer, label: `${layer} · ${run.contextManifest!.sections.filter(s => s.layer === layer).length} sections`,
        children: <Flex vertical gap={token.paddingXS}>{run.contextManifest!.sections.filter(s => s.layer === layer).map(section =>
          <Flex vertical key={section.id} gap={token.paddingXXS}>
            <Typography.Text style={{ overflowWrap: "anywhere" }}>{section.id}</Typography.Text>
            <Typography.Text type="secondary" style={{ overflowWrap: "anywhere" }}>{section.omitted ? "Omitted by Evidence budget · " : ""}{section.source}{section.sourceRevision ? ` · revision ${section.sourceRevision}` : ""} · {section.characters.toLocaleString()} characters · ~{section.estimatedTokens.toLocaleString()} tokens</Typography.Text>
          </Flex>)}{!run.contextManifest!.sections.some(s => s.layer === layer) ? <Typography.Text type="secondary">No sections selected for this request.</Typography.Text> : null}</Flex>
      }))} /> : <Typography.Text type="secondary">No context manifest recorded. Historical Runs retain their original results.</Typography.Text>}
      {run.contextManifest?.fieldSources?.["instructions.agentInstructions"] ? <Typography.Text type="secondary">Agent instructions: {run.contextManifest.fieldSources["instructions.agentInstructions"].kind} · {run.contextManifest.fieldSources["instructions.agentInstructions"].revision}</Typography.Text> : null}
      {run.contextManifest ? <Typography.Text type="secondary">Context format {run.contextManifest.contextFormatVersion} · Core contract {run.contextManifest.coreContractVersion}</Typography.Text> : null}
    </Flex></section> : null}
    {run.composerInput ? <section aria-label="Selected input" className="background-work-detail-section">
      <Typography.Text strong>Selected for this turn</Typography.Text>
      {run.composerInput.explicitSkillKeys.length ? <ReadingRegion label="Explicit Skills" text={run.composerInput.explicitSkillKeys.join("\n")} /> : null}
      {run.composerInput.error ? <Alert type="warning" title={run.composerInput.error} showIcon /> : null}
      {run.composerInput.references.map(reference => <Flex vertical key={reference.locator} gap={token.paddingXXS}>
        <Typography.Text>{reference.label} · {reference.status}{reference.truncated ? " · truncated" : ""}</Typography.Text>
        <Typography.Text type="secondary">{reference.locator}{reference.revision ? ` · revision ${reference.revision}` : ""}</Typography.Text>
      </Flex>)}
    </section> : null}
    {run.wait ? <Flex vertical gap={token.paddingXS} role="status" aria-label="Execution wait">
      <Typography.Text>{run.wait.mode === "Duration" ? "Waiting for the requested duration." : `Waiting for ${run.wait.until === "All" ? "all" : "any"} task signals.`}</Typography.Text>
      <Typography.Text type="secondary">{run.wait.mode === "Duration" ? "Resumes" : "Timeout"} {new Date(run.wait.deadline).toLocaleString()}</Typography.Text>
      {run.wait.mode === "Tasks" ? <Typography.Text type="secondary">{run.wait.taskIds.length} selected {run.wait.taskIds.length === 1 ? "task" : "tasks"}</Typography.Text> : null}
    </Flex> : null}
    {run.nextRetryAt ? <Typography.Text type="secondary">Retry scheduled for {new Date(run.nextRetryAt).toLocaleString()}</Typography.Text> : null}
    {run.outcome ? <ReadingRegion label={runOutcomeLabel(run.outcome.kind)} text={run.outcome.summary || "No action was needed."} /> : null}
    {run.failureSummary ? <Alert type="error" showIcon title={run.failureSummary} action={<DiagnosticDetails fields={{
      diagnosticId: run.diagnosticId, sessionId: run.sessionId, agentRunId: run.agentRunId, code: run.failureCode
    }} />} /> : null}
    {run.knownEffectSummary ? <ReadingRegion label="Confirmed effects" text={run.knownEffectSummary} /> : null}
    {actions && run.cancellationAvailable ? <Button danger style={{ alignSelf: "flex-start" }} disabled={busy || confirming || recoveryRequired} onClick={() => void perform()}>Cancel run</Button> : null}
    {run.cancellationRequested && run.status !== "cancelled" ? <Typography.Text type="secondary" role="status">Cancellation requested</Typography.Text> : null}
    {error ? <Alert type="error" showIcon title={error} description={recoveryRequired ? "Read saved Run facts before another action. Reload does not repeat cancellation." : undefined} action={recoveryRequired ? <Button disabled={busy} onClick={() => void recover()}>Reload run</Button> : undefined} /> : null}
  </Flex>;
}
export function SessionRunHistory({ sessionId, open, source }: { sessionId: string; open: boolean; source: ActivitySource }) {
  const page = useCursorPages(`${source.scope}:${sessionId}`, open, (_scope, cursor) => source.listRuns(sessionId, cursor));
  const { token } = theme.useToken();
  return <Flex vertical gap={token.padding}>
    <Typography.Text strong>Run history</Typography.Text>
    {page.loading ? <Spin aria-label="Loading run history" /> : page.items.length === 0 && !page.error ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="No runs yet" /> : null}
    {(source.tenantId && page.error ? [] : page.items).map(run => <section key={run.agentRunId} className="agent-run-history-item" aria-label={`${runActivationLabel(run.activationKind)} run`}>
      <AgentRunDetails run={run} actions={source.actions} onChange={updated => page.replace(rows => rows.map(row => row.agentRunId === updated.agentRunId ? updated : row))} />
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
