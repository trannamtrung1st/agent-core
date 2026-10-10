import { AgentConfigurationPanel } from './AgentConfigurationLayout';
import { AdminErrorNotice } from "./adminFailure";
import { useEffect, useId, useRef, useState } from "react";
import { InfoCircleOutlined } from "@ant-design/icons";
import { Alert, App, Button, Checkbox, Collapse, Flex, Form, Select, Spin, Tag, Typography, theme } from "antd";
import { listInstanceSettings, listInstanceResources } from "../../services/instanceConfiguration";
import { confirmAction } from "../../app/confirmAction";
import { getHarnessReview, updateHarness, type HarnessReview, type HarnessMode, type HarnessScope } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";

export const harnessScopes: { label: string; value: HarnessScope }[] = [
  { label: "Knowledge & resources", value: "KnowledgeResources" },
  { label: "Operating instructions", value: "Instructions" }, { label: "Tool proposals", value: "ToolSelection" }
];
const evidenceLabels: Record<string, string> = { PartiallyVerified: "Partially verified", CannotVerify: "Cannot verify", RequiresExternalEvidence: "Requires external evidence" };

export function HarnessManagementSection({ instanceId, onUpdated }: {
  instanceId: string; eligibleTools: string[]; onUpdated: () => void
}) {
  const { token } = theme.useToken();
  const { modal } = App.useApp();
  const owner = useRef(instanceId); owner.current = instanceId;
  const [choices, setChoices] = useState<{ label: string; value: string }[] | null>(null); const [selection, setSelection] = useState<string[]>([]);
  const [review, setReview] = useState<HarnessReview | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [mode, setMode] = useState<HarnessMode>("Disabled");
  const [scopes, setScopes] = useState<HarnessScope[]>([]);
  function accept(next: HarnessReview) { if (owner.current !== next.instanceId) return; setReview(next); setMode(next.policy.mode); setScopes(next.policy.scopes); }
  useEffect(() => {
    let current = true;
    setChoices(null); setSelection([]); setReview(null); setBusy(false); setLoading(true); setError(null);
    void getHarnessReview(instanceId).then(next => { if (current) accept(next); })
      .catch(reason => { if (current) setError(describeAdminError(reason, "Unable to load harness policy.")); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [instanceId]);
  async function reload() {
    setLoading(true); setError(null);
    try { accept(await getHarnessReview(instanceId)); }
    catch (reason) { if (owner.current === instanceId) setError(describeAdminError(reason, "Unable to reload harness policy.")); }
    finally { if (owner.current === instanceId) setLoading(false); }
  }
  async function run(action: string, body: unknown) {
    setBusy(true); setError(null);
    try { const next = await updateHarness(instanceId, action, body); if (owner.current !== instanceId) return; accept(next); onUpdated(); }
    catch (reason) {
      if (owner.current !== instanceId) return;
      setError(describeAdminError(reason, "Harness policy update failed."));
      try { accept(await getHarnessReview(instanceId)); } catch { /* Keep the original failure. */ }
    } finally { if (owner.current === instanceId) setBusy(false); }
  }
  const prep = review?.preparation;
  const enabled = review && !review.policy.frozen && review.policy.mode !== "Disabled";
  const changed = review && (mode !== review.policy.mode || JSON.stringify(scopes) !== JSON.stringify(review.policy.scopes));
  const testedRevision = prep?.publishedDraftRevision ?? review?.draftRevision;
  return <AgentConfigurationPanel title="Harness management" label="Harness management"
    description="Chat saves only to this Instance. Changes apply to the next new Run, including in existing conversations." bodyGap="section" bodyClassName="harness-management-content">
      {loading ? <Spin aria-label="Loading harness management" /> : null}
      {error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button onClick={() => void reload()} disabled={busy}>Reload</Button>} /> : null}
      {review ? <>
        <Flex wrap gap={token.paddingXS}><Typography.Text strong>Active version {review.activeVersion}</Typography.Text>
          <Tag>{review.policy.frozen ? "Frozen" : review.policy.mode === "Disabled" ? "Manual" : review.policy.mode}</Tag></Flex>
        <Form layout="vertical" className="admin-config-form">
          <Flex vertical gap={token.padding}>
            <HarnessPolicyModeScopes mode={mode} scopes={scopes} busy={busy} onMode={setMode} onScopes={setScopes} />
            <Typography.Text type="secondary">Current Runs keep their admitted configuration. New Runs resolve current Instance settings.</Typography.Text>
            <Flex wrap gap={token.paddingXS}>
              <Button type="primary" disabled={busy || (!changed && !review.policy.frozen) || (mode !== "Disabled" && scopes.length === 0)} onClick={() => void run("policy", {
                expectedRevision: review.instanceRevision, mode, scopes: mode === "Disabled" ? [] : scopes, sources: [], eligibleTools: [], frozen: false
              })}>Save authoring policy</Button>
              {enabled ? <Button disabled={busy} onClick={() => confirmAction(modal, { title: "Freeze self-management?", content: "Disable harness changes in Chat. The agent keeps using its active version.",
                okText: "Freeze self-management", onOk: () => run("policy", { expectedRevision: review.instanceRevision, ...review.policy, mode: "Disabled", frozen: true }) })}>Freeze self-management</Button> : null}
            </Flex>
          </Flex>
        </Form>
        {review.policy.frozen ? <Alert type="info" showIcon title="Self-management is frozen" description="Chat continues normally. Save an enabled policy to allow new durable improvements." />
          : !enabled ? <Typography.Text type="secondary">Manual mode: Chat can discuss material without saving it to the harness.</Typography.Text> : null}
        {!!review.instanceChanges?.length && <Collapse items={[{ key: "local", label: "Recent Instance changes", children: <Flex vertical gap={token.paddingSM}>{review.instanceChanges.slice().reverse().map(change => <div key={change.operationId}>
          <Typography.Text strong>{change.operation} · Instance revision {change.instanceRevision}</Typography.Text>
          <Typography.Paragraph type="secondary">Saved to this Instance for the next Run. Current Run unchanged.</Typography.Paragraph>
          <Typography.Paragraph>Expected: {change.evidence.expected}<br />Observed: {change.evidence.observed}</Typography.Paragraph>
          <Typography.Text type="secondary">{change.evidence.limitation}</Typography.Text>
        </div>)}</Flex> }]} />}
        {enabled && <Collapse items={[{ key: "shared", label: "Propose selected improvements for a shared Definition", children: <Flex vertical gap={token.paddingSM}>
          <Typography.Paragraph>Choose local fields and resources explicitly. This creates a draft for review and verification. Publishing creates a shared version; other Instances keep their selected version.</Typography.Paragraph>
          <Button disabled={busy || loading} onClick={async () => {
            const id = instanceId; setBusy(true); setError(null);
            try { const [settings, resources] = await Promise.all([listInstanceSettings(id), listInstanceResources(id)]);
              if (owner.current === id) setChoices([...settings.flatMap(s => Object.keys(s.overrides).map(f => ({ label: `${s.section}.${f}`, value: `field:${s.section}:${f}` }))), ...resources.resources.filter(r => r.origin === 'Instance').map(r => ({ label: `Resource: ${r.logicalPath}`, value: `resource:${r.key}` }))]); }
            catch (e) { if (owner.current === id) setError(describeAdminError(e, 'Unable to read promotion choices. Retry.')); }
            finally { if (owner.current === id) setBusy(false); }
          }}>Load local promotion choices</Button>
          {choices && <Select aria-label="Selected shared promotion content" mode="multiple" options={choices} value={selection} disabled={busy} onChange={setSelection} placeholder="Select local fields and resources" />}
          <Button disabled={busy || !selection.length || !choices} onClick={() => {
            const fields: Record<string, string[]> = {}; const resources: string[] = [];
            selection.forEach(value => { if (value.startsWith('resource:')) resources.push(value.slice(9)); else { const [, section, field] = value.split(':'); (fields[section] ??= []).push(field); } });
            confirmAction(modal, { title: 'Create a shared promotion draft?', content: selection.map(value => choices?.find(c => c.value === value)?.label).join(', '), okText: 'Create reviewed draft', onOk: () => run('propose-shared', { expectedRevision: review.instanceRevision, purpose: 'Promote selected Instance improvements', selectedFields: fields, resourceKeys: resources }) });
          }}>Create shared proposal</Button>
        </Flex> }]} />}
        {prep ? <Collapse items={[{ key: "recent", label: "Recent harness change & verification", children: <Flex vertical gap={token.padding}>
          <Flex vertical gap={token.paddingXS}>
            <Flex align="center" wrap gap={token.paddingXS}>
              <Typography.Text strong>{prep.status === "Published" ? `Published shared Definition · version ${prep.publishedVersion}` : prep.status}</Typography.Text>
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
            <Flex wrap gap={token.paddingXS}><Typography.Text strong>{e.check}</Typography.Text><Tag>{e.actor === "Core" ? "Core check" : e.actor === "Owner" ? "Owner review" : "Agent assessment"}</Tag>
              <Tag color={e.status === "Failed" ? "error" : undefined}>{evidenceLabels[e.status] ?? e.status}</Tag>
              {e.draftRevision !== testedRevision ? <Tag>Stale · revision {e.draftRevision}</Tag> : null}</Flex>
            <Typography.Paragraph>Expected: {e.expected}</Typography.Paragraph><Typography.Paragraph>Observed: {e.observed}</Typography.Paragraph>
            {e.limitation ? <Typography.Paragraph type="secondary">{e.limitation}</Typography.Paragraph> : null}
          </div>)}
          {prep.status === 'Preparing' || prep.status === 'Ready' ? <Flex wrap gap={token.paddingXS}>
            <Button disabled={busy} onClick={() => void run('verify', { preparationId: prep.preparationId })}>Verify shared candidate</Button>
            {prep.status === 'Ready' && <Button disabled={busy} onClick={() => confirmAction(modal, { title: 'Publish and adopt this reviewed candidate?', content: 'Creates a reusable Definition version and adopts it only for this Instance. Other Instances stay on their selected version. Current Runs are unchanged.', okText: 'Publish and adopt', onOk: () => run('publish-adopt', { expectedRevision: review.instanceRevision, draftRevision: review.draftRevision }) })}>Publish and adopt reviewed candidate</Button>}
          </Flex> : null}
          {!["Published", "Cancelled", "Failed"].includes(prep.status) ? <><Alert type="warning" showIcon title="Unfinished candidate" description="Review and verify this shared candidate, or discard it. Ordinary Chat changes remain Instance-owned." />
            <Flex wrap gap={token.paddingXS}><Button disabled={busy} onClick={() => confirmAction(modal, { title: "Discard unfinished candidate?", content: "Cancel its authoring grant and pending approvals. The active version stays unchanged.", okText: "Discard candidate", onOk: () => run("cancel", { expectedRevision: review.instanceRevision }) })}>Discard unfinished candidate</Button></Flex></> : null}
        </Flex> }]} /> : <Typography.Text type="secondary">No harness changes yet. Start by teaching reusable knowledge or a procedure in Chat.</Typography.Text>}
      </> : null}
    </AgentConfigurationPanel>;
}

export function HarnessPolicyModeScopes({ mode, scopes, busy, onMode, onScopes }: {
  mode: HarnessMode; scopes: HarnessScope[]; busy: boolean; onMode: (mode: HarnessMode) => void; onScopes: (scopes: HarnessScope[]) => void
}) {
  const { token } = theme.useToken();
  const fieldId = useId();
  const helpId = `${fieldId}-help`;
  const scopesInvalid = mode !== "Disabled" && scopes.length === 0;
  const help = mode === "Disabled"
    ? "Harness changes stay manual."
    : mode === "Assisted"
      ? "Changes need your approval in Chat."
      : "Allowed knowledge may auto-save. Instructions and tools need approval.";
  return <Flex vertical gap={token.padding} className="harness-policy-fields">
    <Form.Item label="Authoring mode" htmlFor={fieldId} extra={<Typography.Text id={helpId} type="secondary" style={{ color: token.colorTextSecondary }}>{help}</Typography.Text>}>
      <Select id={fieldId} aria-label="Authoring mode" aria-describedby={helpId} value={mode} disabled={busy} onChange={onMode} options={[
        { value: "Disabled", label: "Manual (off)" }, { value: "Assisted", label: "Assisted" }, { value: "Managed", label: "Managed" }
      ]} />
    </Form.Item>
    {mode !== "Disabled" ? <Form.Item label="Areas the agent may manage" validateStatus={scopesInvalid ? "error" : undefined}
      help={scopesInvalid ? <span id={`${fieldId}-scopes-help`}>Select at least one area.</span> : undefined}>
      <Checkbox.Group className="harness-policy-scopes" style={{ gap: token.paddingXS }} aria-label="Areas the agent may manage"
        aria-invalid={scopesInvalid || undefined} aria-describedby={scopesInvalid ? `${fieldId}-scopes-help` : undefined} options={harnessScopes} value={scopes} disabled={busy}
      onChange={values => onScopes(values as HarnessScope[])} /></Form.Item> : null}
  </Flex>;
}
