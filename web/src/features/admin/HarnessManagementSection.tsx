import { useEffect, useState } from "react";
import { InfoCircleOutlined } from "@ant-design/icons";
import { Alert, App, Button, Checkbox, Collapse, Flex, Form, Select, Spin, Tag, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { getHarnessReview, updateHarness, type HarnessReview, type HarnessMode, type HarnessScope } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";

export const harnessScopes: { label: string; value: HarnessScope }[] = [
  { label: "Knowledge & resources", value: "KnowledgeResources" }, { label: "Skills", value: "Skills" },
  { label: "Operating instructions", value: "Instructions" }, { label: "Tool proposals", value: "ToolSelection" }
];
const evidenceLabels: Record<string, string> = { PartiallyVerified: "Partially verified", CannotVerify: "Cannot verify", RequiresExternalEvidence: "Requires external evidence" };

export function HarnessManagementSection({ instanceId, onUpdated }: {
  instanceId: string; eligibleTools: string[]; onUpdated: () => void
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const [review, setReview] = useState<HarnessReview | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [mode, setMode] = useState<HarnessMode>("Disabled");
  const [scopes, setScopes] = useState<HarnessScope[]>([]);
  function accept(next: HarnessReview) { setReview(next); setMode(next.policy.mode); setScopes(next.policy.scopes); }
  useEffect(() => {
    let current = true;
    setReview(null); setLoading(true); setError(null);
    void getHarnessReview(instanceId).then(next => { if (current) accept(next); })
      .catch(reason => { if (current) setError(describeAdminError(reason, "Unable to load harness policy.")); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [instanceId]);
  async function reload() {
    setLoading(true); setError(null);
    try { accept(await getHarnessReview(instanceId)); }
    catch (reason) { setError(describeAdminError(reason, "Unable to reload harness policy.")); }
    finally { setLoading(false); }
  }
  async function run(action: string, body: unknown) {
    setBusy(true); setError(null);
    try { accept(await updateHarness(instanceId, action, body)); onUpdated(); }
    catch (reason) {
      setError(describeAdminError(reason, "Harness policy update failed."));
      try { accept(await getHarnessReview(instanceId)); } catch { /* Keep the original failure. */ }
    } finally { setBusy(false); }
  }
  const prep = review?.preparation;
  const enabled = review && !review.policy.frozen && review.policy.mode !== "Disabled";
  const changed = review && (mode !== review.policy.mode || JSON.stringify(scopes) !== JSON.stringify(review.policy.scopes));
  const testedRevision = prep?.publishedDraftRevision ?? review?.draftRevision;
  return <section className="admin-definition-panel" aria-label="Harness management">
    <div className="admin-definition-panel-heading">
      <Typography.Title level={4}>Harness management</Typography.Title>
      <Typography.Text type="secondary">Teach the agent in Chat. Control what it may save for future conversations here.</Typography.Text>
    </div>
    <div className="admin-definition-panel-body"><Flex vertical gap={token.padding} className="harness-management-content">
      {loading ? <Spin aria-label="Loading harness management" /> : null}
      {error ? <Alert type="error" showIcon title={error.message} action={<Button onClick={() => void reload()} disabled={busy}>Reload</Button>}
        description={error.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: error.diagnosticId }} /> : undefined} /> : null}
      {review ? <>
        <Flex wrap gap={token.paddingXS}><Typography.Text strong>Active version {review.activeVersion}</Typography.Text>
          <Tag>{review.policy.frozen ? "Frozen" : review.policy.mode === "Disabled" ? "Manual" : review.policy.mode}</Tag></Flex>
        <Form layout="vertical">
          <HarnessPolicyModeScopes mode={mode} scopes={scopes} busy={busy} onMode={setMode} onScopes={setScopes} />
          <Typography.Paragraph type="secondary">Managed may save knowledge and Skills automatically. Instructions and tool changes always need approval in Chat. Existing conversations keep their current version.</Typography.Paragraph>
          <Button disabled={busy || (!changed && !review.policy.frozen) || (mode !== "Disabled" && scopes.length === 0)} onClick={() => void run("policy", {
            expectedRevision: review.instanceRevision, mode, scopes: mode === "Disabled" ? [] : scopes, sources: [], eligibleTools: [], frozen: false
          })}>Save authoring policy</Button>
        </Form>
        {review.policy.frozen ? <Alert type="info" showIcon title="Self-management is frozen" description="Chat continues normally. Save an enabled policy to allow new durable improvements." />
          : !enabled ? <Typography.Text type="secondary">Manual mode: Chat can discuss material without saving it to the harness.</Typography.Text> : null}
        {prep ? <Collapse items={[{ key: "recent", label: "Recent harness change & verification", children: <Flex vertical gap={token.padding}>
          <Flex vertical gap={token.paddingXS}>
            <Flex align="center" wrap gap={token.paddingXS}>
              <Typography.Text strong>{prep.status === "Published" ? `Saved for future conversations · version ${prep.publishedVersion}` : prep.status}</Typography.Text>
              {prep.diagnosticId ? <DiagnosticDetails fields={{ diagnosticId: prep.diagnosticId }} trigger={
                <Button type="text" icon={<InfoCircleOutlined />} aria-label="Error details">Error details</Button>
              } /> : null}
            </Flex>
            <Typography.Text type="secondary">Tested candidate revision {testedRevision} · based on version {prep.baseVersion}</Typography.Text>
          </Flex>
          {review.diff?.sections.filter(s => s.changeKind !== "Unchanged").map(s => <div key={s.sectionId}>
            <Typography.Text strong>{s.label}</Typography.Text><Typography.Paragraph>{s.beforeSummary ?? "No previous value"} → {s.afterSummary ?? "Removed"}</Typography.Paragraph>
          </div>)}
          {prep.evidence.map((e, index) => <div key={index} className="harness-evidence">
            <Flex wrap gap={token.paddingXS}><Typography.Text strong>{e.check}</Typography.Text><Tag>{e.actor === "Core" ? "Core check" : "Agent assessment"}</Tag>
              <Tag color={e.status === "Failed" ? "error" : undefined}>{evidenceLabels[e.status] ?? e.status}</Tag>
              {e.draftRevision !== testedRevision ? <Tag>Stale · revision {e.draftRevision}</Tag> : null}</Flex>
            <Typography.Paragraph>Expected: {e.expected}</Typography.Paragraph><Typography.Paragraph>Observed: {e.observed}</Typography.Paragraph>
            {e.limitation ? <Typography.Paragraph type="secondary">{e.limitation}</Typography.Paragraph> : null}
          </div>)}
          {!["Published", "Cancelled", "Failed"].includes(prep.status) ? <><Alert type="warning" showIcon title="Unfinished candidate" description="Discard this earlier candidate before teaching another improvement in Chat. The active version will stay unchanged." />
            <Button disabled={busy} onClick={() => confirmAction(modal, { title: "Discard unfinished candidate?", content: "Cancel its authoring grant and pending approvals. The active version stays unchanged.", okText: "Discard candidate", onOk: () => run("cancel", { expectedRevision: review.instanceRevision }) })}>Discard unfinished candidate</Button></> : null}
        </Flex> }]} /> : <Typography.Text type="secondary">No harness changes yet. Start by teaching reusable knowledge or a procedure in Chat.</Typography.Text>}
        {enabled ? <Button disabled={busy} onClick={() => confirmAction(modal, { title: "Freeze self-management?", content: "Disable harness changes in Chat. The agent keeps using its active version.",
          okText: "Freeze self-management", onOk: () => run("policy", { expectedRevision: review.instanceRevision, ...review.policy, mode: "Disabled", frozen: true }) })}>Freeze self-management</Button> : null}
      </> : null}
    </Flex></div>
  </section>;
}

export function HarnessPolicyModeScopes({ mode, scopes, busy, onMode, onScopes }: {
  mode: HarnessMode; scopes: HarnessScope[]; busy: boolean; onMode: (mode: HarnessMode) => void; onScopes: (scopes: HarnessScope[]) => void
}) {
  return <>
    <Form.Item label="Authoring mode" help="Assisted asks for approval in Chat. Managed may save granted knowledge and Skills automatically.">
      <Select aria-label="Authoring mode" value={mode} disabled={busy} onChange={onMode} options={[
        { value: "Disabled", label: "Manual (off)" }, { value: "Assisted", label: "Assisted" }, { value: "Managed", label: "Managed" }
      ]} />
    </Form.Item>
    {mode !== "Disabled" ? <Form.Item label="Areas the agent may manage"><Checkbox.Group aria-label="Authoring scopes" options={harnessScopes} value={scopes} disabled={busy}
      onChange={values => onScopes(values as HarnessScope[])} /></Form.Item> : null}
  </>;
}
