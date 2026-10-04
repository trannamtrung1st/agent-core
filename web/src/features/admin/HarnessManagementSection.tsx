import { useEffect, useRef, useState } from "react";
import { Alert, App, Button, Checkbox, Collapse, Flex, Form, Input, Select, Spin, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { getHarnessReview, updateHarness, type HarnessReview, type HarnessMode, type HarnessScope, type HarnessApproval } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";

export const harnessScopes: { label: string; value: HarnessScope }[] = [
  { label: "Knowledge & resources", value: "KnowledgeResources" }, { label: "Skills", value: "Skills" },
  { label: "Operating instructions", value: "Instructions" }, { label: "Tool proposals", value: "ToolSelection" }
];
const labels: Record<string, string> = { Verified: "Verified", PartiallyVerified: "Partially verified", CannotVerify: "Cannot verify",
  RequiresExternalEvidence: "Requires external evidence", Failed: "Failed", AwaitingApproval: "Awaiting approval", Preparing: "Preparing",
  Ready: "Ready for review", Cancelled: "Cancelled", Published: "Published & adopted" };

export function HarnessManagementSection({ instanceId, eligibleTools, onUpdated }: {
  instanceId: string; eligibleTools: string[]; onUpdated: () => void
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const executionAbort = useRef<AbortController | null>(null);
  const [executing, setExecuting] = useState(false);
  const [review, setReview] = useState<HarnessReview | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [mode, setMode] = useState<HarnessMode>("Disabled");
  const [scopes, setScopes] = useState<HarnessScope[]>([]);
  const [sources, setSources] = useState("");
  const [tools, setTools] = useState<string[]>([]);
  const [purpose, setPurpose] = useState("");

  function accept(next: HarnessReview) {
    setReview(next); setMode(next.policy.mode); setScopes(next.policy.scopes);
    setSources(next.policy.sources.join("\n")); setTools(next.policy.eligibleTools);
  }
  useEffect(() => {
    let current = true;
    setReview(null); setLoading(true); setError(null);
    void getHarnessReview(instanceId).then(next => { if (current) accept(next); })
      .catch(reason => { if (current) setError(describeAdminError(reason, "Unable to load harness management.")); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; executionAbort.current?.abort(); };
  }, [instanceId]);

  async function run(action: string, body: unknown) {
    setBusy(true); setError(null);
    try {
      let abortSignal: AbortSignal | undefined;
      if (action === "continue") { const abort = new AbortController(); executionAbort.current = abort; setExecuting(true); abortSignal = abort.signal; }
      let next = await updateHarness(instanceId, action, body, ...(abortSignal ? [abortSignal] : []));
      accept(next);
      if (action === "prepare" && next.preparation) {
        const abort = new AbortController(); executionAbort.current = abort; setExecuting(true);
        next = await updateHarness(instanceId, "continue", { preparationId: next.preparation.preparationId }, abort.signal);
        accept(next);
      }
      onUpdated();
    }
    catch (reason) {
      if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(describeAdminError(reason, "Harness operation failed."));
      // Refresh stale revisions and durable failure state without replaying the operation.
      try {
        let latest = await getHarnessReview(instanceId);
        if (executionAbort.current?.signal.aborted && latest.preparation && ["Preparing", "Ready", "AwaitingApproval"].includes(latest.preparation.status))
          latest = await updateHarness(instanceId, "cancel", { expectedRevision: latest.instanceRevision });
        accept(latest);
      } catch { /* Preserve the original error. */ }
    } finally { executionAbort.current = null; setExecuting(false); setBusy(false); }
  }
  async function reload() {
    setLoading(true); setError(null);
    try { accept(await getHarnessReview(instanceId)); }
    catch (reason) { setError(describeAdminError(reason, "Unable to reload harness management.")); }
    finally { setLoading(false); }
  }
  const prep = review?.preparation;
  const enabled = review && !review.policy.frozen && review.policy.mode !== "Disabled";
  const pending = prep?.approvals.filter(a => a.status === "Pending") ?? [];
  const evidenceRevision = prep?.publishedDraftRevision ?? review?.draftRevision;
  const currentEvidence = prep?.evidence.filter(e => e.draftRevision === evidenceRevision) ?? [];
  const canPromote = prep?.status === "Ready" && pending.length === 0
    && currentEvidence.some(e => e.actor === "Agent") && currentEvidence.some(e => e.actor === "Core" && e.status === "Verified");
  const canStart = !prep || ["Published", "Cancelled", "Failed"].includes(prep.status);
  const policyChanged = review && (mode !== review.policy.mode || sources !== review.policy.sources.join("\n")
    || JSON.stringify(scopes) !== JSON.stringify(review.policy.scopes) || JSON.stringify(tools) !== JSON.stringify(review.policy.eligibleTools));

  function decide(approval: HarnessApproval, approve: boolean) {
    confirmAction(modal, { title: approve ? "Approve this candidate change?" : "Reject this candidate change?",
      content: <Flex vertical gap={token.paddingXS}><Typography.Text>{approval.operation.kind} · candidate revision {approval.operation.draftRevision}</Typography.Text>
        <Typography.Text>Approval applies to this exact draft change. Future tool actions use their normal authorization.</Typography.Text></Flex>,
      okText: approve ? "Approve change" : "Reject change", onOk: () => run(`approvals/${approval.approvalId}`,
        { expectedRevision: review!.instanceRevision, actionHash: approval.actionHash, approve }) });
  }

  return <section className="admin-definition-panel" aria-label="Harness management">
    <div className="admin-definition-panel-heading">
      <Typography.Title level={4}>Harness management</Typography.Title>
      <Typography.Text type="secondary">Let the agent prepare a candidate. You review and publish the version it will use.</Typography.Text>
    </div>
    <div className="admin-definition-panel-body">
      <Flex vertical gap={token.padding} className="harness-management-content">
        {loading ? <Spin aria-label="Loading harness management" /> : null}
        {error ? <Alert type="error" showIcon title={error.message} action={<Button onClick={() => void reload()} disabled={busy}>Reload</Button>}
          description={error.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: error.diagnosticId }} /> : undefined} /> : null}
        {review ? <>
          <Flex wrap gap={token.paddingXS} align="center">
            <Typography.Text strong>Active version {review.activeVersion}</Typography.Text>
            <Tag>{review.policy.frozen ? "Frozen" : review.policy.mode === "Disabled" ? "Manual" : review.policy.mode}</Tag>
            {prep ? <Tag>{labels[prep.status] ?? prep.status}</Tag> : <Typography.Text type="secondary">No candidate prepared</Typography.Text>}
          </Flex>
          {review.policy.frozen ? <Alert type="info" showIcon title="Self-management is frozen" description="The agent continues using its active version. Save a new policy to re-enable preparation with another draft." /> : null}
          <Collapse items={[{ key: "policy", label: review.policy.frozen ? "Re-enable / edit policy" : "Configure authoring policy", children:
            <Form layout="vertical">
              <HarnessPolicyModeScopes mode={mode} scopes={scopes} busy={busy} onMode={setMode} onScopes={setScopes} />
              {mode !== "Disabled" ? <>
                <Form.Item label="Permitted sources" help="One exact source per line: knowledge:identity, candidate:resource-path, or a public URL the active harness can read.">
                  <Input.TextArea aria-label="Permitted sources" value={sources} onChange={e => setSources(e.target.value)} disabled={busy} autoSize={{ minRows: 2, maxRows: 6 }} />
                </Form.Item>
                {scopes.includes("ToolSelection") ? <Form.Item label="Eligible tools" help="Choose from the active authorized tools. Every selection or configuration proposal needs approval.">
                  <Select mode="multiple" aria-label="Eligible tools" value={tools} onChange={setTools} disabled={busy}
                    options={[...new Set([...eligibleTools.filter(tool => !["skills.load", "app.message.send", "work.complete"].includes(tool)), "attachments.read", ...review.policy.eligibleTools])].map(value => ({ value, label: value }))} />
                </Form.Item> : null}
              </> : null}
              <Typography.Paragraph type="secondary">Tool changes and publication always need your explicit approval.</Typography.Paragraph>
              <Button disabled={busy || (!policyChanged && !review.policy.frozen) || (mode !== "Disabled" && scopes.length === 0)} onClick={() => void run("policy", {
                expectedRevision: review.instanceRevision, mode, scopes: mode === "Disabled" ? [] : scopes,
                sources: mode === "Disabled" ? [] : sources.split("\n").map(s => s.trim()).filter(Boolean), eligibleTools: mode === "Disabled" ? [] : tools, frozen: false
              })}>Save authoring policy</Button>
            </Form>
          }]} />
          {enabled && canStart ? <Form layout="vertical">
            <Form.Item label="Preparation purpose"><Input.TextArea aria-label="Preparation purpose" value={purpose} onChange={e => setPurpose(e.target.value)} disabled={busy}
              maxLength={2000} autoSize={{ minRows: 2, maxRows: 5 }} placeholder="Prepare the knowledge and procedures needed for store operations." /></Form.Item>
            <Button type="primary" disabled={busy || !purpose.trim()} loading={busy} onClick={() => void run("prepare", { expectedRevision: review.instanceRevision, purpose })}>Prepare harness</Button>
          </Form> : null}
          {busy ? <Typography.Text role="status">{executing ? "Preparing the candidate…" : "Saving the candidate…"}</Typography.Text> : null}
          {executing ? <Button onClick={() => executionAbort.current?.abort()}>Stop preparation</Button> : null}
          {prep ? <>
            <Typography.Paragraph><Typography.Text strong>Candidate purpose: </Typography.Text>{prep.purpose}</Typography.Paragraph>
            <Typography.Text type="secondary">Based on active version {prep.baseVersion} · candidate revision {prep.publishedDraftRevision ?? review.draftRevision ?? "published"}</Typography.Text>
            {prep.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: prep.diagnosticId }} /> : null}
            {pending.map(approval => <Flex key={approval.approvalId} vertical gap={token.paddingXS} className="harness-approval">
              <Typography.Text strong>Approval required: {approval.operation.kind}</Typography.Text>
              <Typography.Text>{approval.operation.id ?? approval.operation.skill?.name ?? "Operating instructions"} · revision {approval.operation.draftRevision}</Typography.Text>
              {approval.operation.enabled !== null ? <Typography.Text>{approval.operation.enabled ? "Enable" : "Disable"} this tool in the candidate</Typography.Text> : null}
              {approval.operation.allowUnreadUnsupportedTypes !== null ? <Typography.Text>Unsupported attachment readability: {approval.operation.allowUnreadUnsupportedTypes ? "allowed" : "disabled"}</Typography.Text> : null}
              {approval.operation.content ? <Typography.Paragraph className="harness-procedure">{approval.operation.content}</Typography.Paragraph> : null}
              {approval.operation.skill ? <Typography.Paragraph className="harness-procedure">{approval.operation.skill.procedure}</Typography.Paragraph> : null}
              <Flex wrap gap={token.paddingXS}><Button type="primary" disabled={busy || !enabled} onClick={() => decide(approval, true)}>Review &amp; approve change</Button>
                <Button disabled={busy || !enabled} onClick={() => decide(approval, false)}>Reject change</Button></Flex>
            </Flex>)}
            {prep.approvals.filter(a => a.status !== "Pending").map(a => <Typography.Text key={a.approvalId} type="secondary">{a.operation.kind} · {a.status}</Typography.Text>)}
            <Collapse items={[
              { key: "changes", label: "What changed", children: <Flex vertical gap={token.paddingXS}>
                {review.diff?.sections.filter(s => s.changeKind !== "Unchanged").map(s => <div key={s.sectionId}>
                  <Typography.Text strong>{s.label} · {s.changeKind}</Typography.Text>
                  <Typography.Paragraph>{s.beforeSummary ?? "No previous value"} → {s.afterSummary ?? "Removed"}</Typography.Paragraph>
                </div>)}
              </Flex> },
              { key: "knowledge", label: `Knowledge & resources (${review.knowledge.length})`, children: <Flex vertical gap={token.paddingXS}>
                {review.knowledge.length === 0 ? <Typography.Text type="secondary">No knowledge sources in this candidate.</Typography.Text> : review.knowledge.map(k => <div key={k.identity}>
                  <Typography.Text strong>{k.title}</Typography.Text><Typography.Paragraph>Source: {k.citation}</Typography.Paragraph>
                  <Typography.Text type="secondary">{k.resourcePath}</Typography.Text></div>)}
              </Flex> },
              { key: "skills", label: `Skills (${review.skills.length})`, children: <Flex vertical gap={token.padding}>
                {review.skills.length === 0 ? <Typography.Text type="secondary">No candidate Skills.</Typography.Text> : review.skills.map(skill => <div key={skill.id}>
                  <Typography.Text strong>{skill.name}</Typography.Text><Typography.Paragraph>{skill.description}</Typography.Paragraph>
                  <Typography.Paragraph className="harness-procedure">{skill.procedure}</Typography.Paragraph>
                  <Typography.Text type="secondary">Requires: {skill.requiredCapabilities.join(", ") || "No additional capabilities"}</Typography.Text>
                </div>)}
              </Flex> },
              { key: "instructions", label: "Operating instructions & selected tools", children: <>
                <Typography.Paragraph className="harness-procedure">{review.instructions}</Typography.Paragraph>
                <Typography.Text>Selected tools: {review.selectedTools.join(", ") || "None"}</Typography.Text></> },
              { key: "verification", label: `Verification & limitations (${currentEvidence.length} current checks)`, children: <Flex vertical gap={token.padding}>
                {prep.evidence.length === 0 ? <Typography.Text type="secondary">No verification evidence yet. Continue preparation before promotion.</Typography.Text> : prep.evidence.map((e, index) => <div key={index} className="harness-evidence">
                  <Flex wrap gap={token.paddingXS}><Typography.Text strong>{e.check}</Typography.Text><Tag>{e.actor === "Core" ? "Core check" : "Agent assessment"}</Tag>
                    <Tag color={e.status === "Failed" ? "error" : undefined}>{labels[e.status] ?? e.status}</Tag>
                    {e.draftRevision !== evidenceRevision ? <Tag>Stale · revision {e.draftRevision}</Tag> : null}</Flex>
                  <Typography.Paragraph>Expected: {e.expected}</Typography.Paragraph><Typography.Paragraph>Observed: {e.observed}</Typography.Paragraph>
                  {e.limitation ? <Alert type="info" title={e.limitation} /> : null}
                </div>)}
              </Flex> }
            ]} />
            {enabled && !canStart ? <Flex wrap gap={token.paddingXS}>
              <Button disabled={busy || pending.length > 0} onClick={() => void run("continue", { preparationId: prep.preparationId })}>Continue preparation</Button>
              <Button disabled={busy} onClick={() => void run("verify", { preparationId: prep.preparationId })}>Verify candidate</Button>
              <Button type="primary" disabled={busy || !canPromote} onClick={() => confirmAction(modal, {
                title: "Publish & adopt this candidate?", content: `Publish an immutable version from candidate revision ${review.draftRevision} and use it for this Agent Instance. Review the checks and external limitations first.`,
                okText: "Publish & adopt", onOk: () => run("publish-adopt", { expectedRevision: review.instanceRevision, draftRevision: review.draftRevision })
              })}>Publish &amp; adopt</Button>
              <Button disabled={busy} onClick={() => confirmAction(modal, { title: "Cancel preparation?", content: "Cancel authoring and pending approvals. The active version is unaffected.",
                okText: "Cancel preparation", onOk: () => run("cancel", { expectedRevision: review.instanceRevision }) })}>Cancel preparation</Button>
            </Flex> : null}
          </> : null}
          {enabled ? <Button disabled={busy} onClick={() => confirmAction(modal, { title: "Freeze self-management?",
            content: "Disable agent authoring. The agent keeps using the active published version. Future improvements require re-enabling and a new draft.",
            okText: "Freeze self-management", onOk: () => run("policy", { expectedRevision: review.instanceRevision, ...review.policy, mode: "Disabled", frozen: true })
          })}>Freeze self-management</Button> : null}
        </> : null}
      </Flex>
    </div>
  </section>;
}

/** Shared by creation and instance policy editing; Ant Design owns the controls. */
export function HarnessPolicyModeScopes({ mode, scopes, busy, onMode, onScopes }: {
  mode: HarnessMode; scopes: HarnessScope[]; busy: boolean; onMode: (mode: HarnessMode) => void; onScopes: (scopes: HarnessScope[]) => void
}) {
  return <>
    <Form.Item label="Authoring mode" help="Assisted asks you to approve each edit. Managed can edit granted areas of a draft.">
      <Select aria-label="Authoring mode" value={mode} disabled={busy} onChange={onMode} options={[
        { value: "Disabled", label: "Manual (off)" }, { value: "Assisted", label: "Assisted" }, { value: "Managed", label: "Managed" }
      ]} />
    </Form.Item>
    {mode !== "Disabled" ? <Form.Item label="Areas the agent may prepare"><Checkbox.Group aria-label="Authoring scopes" options={harnessScopes} value={scopes} disabled={busy}
      onChange={values => onScopes(values as HarnessScope[])} /></Form.Item> : null}
  </>;
}
