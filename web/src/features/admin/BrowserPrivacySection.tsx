import { useEffect, useRef, useState } from "react";
import { Alert, App, Button, Descriptions, Flex, Form, Select, Spin, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { getBrowserPrivacy, saveBrowserPrivacy, type BrowserPrivacy, type BrowserPrivacyMode } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";
import { AdminErrorNotice } from "./adminFailure";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

type Fields = { mode: BrowserPrivacyMode; unmaskedOrigins: string[]; trustedGraphicsOrigins: string[] };
const exposure = "Unmasked screenshots can expose credentials, OTPs, personal information and confidential application data to model providers and immutable Session artifacts. All child frames remain masked. Semantic redaction and browser origin restrictions still apply.";

/** Global host policy editor inside the existing Browser provider configuration. */
export function BrowserPrivacySection() {
  const { modal } = App.useApp();
  const { token } = theme.useToken();
  const detailLayout = useAdminDetailLayout();
  const [form] = Form.useForm<Fields>();
  const mode = Form.useWatch("mode", form);
  const [policy, setPolicy] = useState<BrowserPrivacy | null>(null);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const active = useRef<AbortController | null>(null);

  function accept(value: BrowserPrivacy) {
    setPolicy(value);
    form.setFieldsValue({ mode: value.saved.mode, unmaskedOrigins: value.saved.unmaskedOrigins,
      trustedGraphicsOrigins: value.saved.trustedGraphicsOrigins });
  }
  async function load() {
    active.current?.abort();
    const controller = new AbortController(); active.current = controller;
    setLoading(true); setError(null); setNotice(null);
    try { const value = await getBrowserPrivacy(controller.signal); if (!controller.signal.aborted) accept(value); }
    catch (failure) { if (!controller.signal.aborted) setError(describeAdminError(failure, "Unable to load screenshot privacy.")); }
    finally { if (!controller.signal.aborted) setLoading(false); }
  }
  useEffect(() => { void load(); return () => active.current?.abort(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function save(values: Fields, acknowledgeExposure: boolean) {
    if (!policy || saving) return;
    const controller = new AbortController(); active.current = controller;
    setSaving(true); setError(null); setNotice(null);
    try {
      const value = await saveBrowserPrivacy({ ...values, unmaskedOrigins: values.mode === "Unmasked" ? values.unmaskedOrigins : [],
        expectedRevision: policy.saved.revision, acknowledgeExposure }, controller.signal);
      if (!controller.signal.aborted) { accept(value); setNotice(value.durable
        ? "Screenshot privacy saved. Restart the host to activate this revision." : value.activation); }
    } catch (failure) {
      if (!controller.signal.aborted) setError(describeAdminError(failure, "Unable to save screenshot privacy. Your edits are retained."));
    } finally { if (!controller.signal.aborted) setSaving(false); }
  }
  function submit(values: Fields) {
    if (values.mode === "Unmasked" && !values.unmaskedOrigins?.length) {
      form.setFields([{ name: "unmaskedOrigins", errors: ["Select at least one deployment-approved exact origin."] }]);
      return;
    }
    if (values.mode === "Unmasked") confirmAction(modal, { title: "Authorize unmasked screenshots?", danger: true,
      content: <Flex vertical gap={token.paddingSM}><Typography.Paragraph>{exposure}</Typography.Paragraph>
        <Typography.Text>Exact origins: {values.unmaskedOrigins.join(", ")}</Typography.Text></Flex>,
      okText: "Acknowledge exposure and save", onOk: () => save(values, true) });
    else void save(values, false);
  }
  return <Flex vertical gap={token.paddingSM} role="region" aria-label="Screenshot privacy">
    <Typography.Title level={5}>Screenshot privacy</Typography.Title>
    <Typography.Text type="secondary">Global Browser policy for every Instance. Definitions, agents and page content cannot expand these permissions.</Typography.Text>
    {loading ? <Spin aria-label="Loading screenshot privacy" /> : null}
    {error ? <Alert type="error" showIcon title={<AdminErrorNotice {...error} />} action={
      <Button disabled={saving} onClick={() => void load()}>Reload saved privacy policy</Button>} /> : null}
    {notice ? <div role="status"><Alert type="success" showIcon title={notice} /></div> : null}
    {policy ? <>
      <Descriptions {...detailLayout} bordered size="small" column={1}>
        <Descriptions.Item label="Effective privacy">{policy.effective.mode} · revision {policy.effective.revision}</Descriptions.Item>
        <Descriptions.Item label="Effective unmasked origins">{policy.effective.unmaskedOrigins.join(", ") || "None"}</Descriptions.Item>
        <Descriptions.Item label="Effective graphics origins">{policy.effective.trustedGraphicsOrigins.join(", ") || "None"}</Descriptions.Item>
        <Descriptions.Item label="Saved privacy">{policy.saved.mode} · revision {policy.saved.revision}{policy.restartRequired
          ? policy.durable ? " · restart required" : " · ephemeral, not active" : policy.constrainedByDeployment ? "" : " · active as saved"}
          {policy.constrainedByDeployment ? " · constrained by deployment" : ""}</Descriptions.Item>
        <Descriptions.Item label="Persistence">{policy.durable ? "Durable SQLite policy" : "InMemory — edits do not survive host restart"}</Descriptions.Item>
        <Descriptions.Item label="Deployment restrictions">{!policy.deployment.captureAllowed ? "Screenshots prohibited" : policy.deployment.unmaskedAllowed ? "Unmasked capture limited to deployment-approved exact origins" : "Unmasked capture prohibited"}</Descriptions.Item>
        <Descriptions.Item label="Activation">{policy.activation}</Descriptions.Item>
      </Descriptions>
      <Form form={form} layout="vertical" className="admin-config-form" disabled={saving || loading} onFinish={submit}>
        <Form.Item name="mode" label="Saved screenshot privacy mode" rules={[{ required: true }]}>
          <Select options={[
            { value: "Protected", label: "Protected — mask sensitive pixels", disabled: !policy.deployment.captureAllowed },
            { value: "Unmasked", label: "Unmasked — explicit confidentiality exception", disabled: !policy.deployment.captureAllowed || !policy.deployment.unmaskedAllowed },
            { value: "Disabled", label: "Disabled — no screenshots" }
          ]} />
        </Form.Item>
        <Typography.Paragraph type="secondary">{mode === "Disabled" ? "Semantic browser tools remain available. Screenshot and coordinate guidance is suppressed after activation."
          : mode === "Unmasked" ? "Capture visible content without automatic sensitive-pixel masking only on selected exact origins. Other origins use Protected."
          : "Mask sensitive DOM content, passwords, reflected secrets and child frames. DOM masking cannot guarantee all visual secrets; use Disabled for strict confidentiality."}</Typography.Paragraph>
        {mode === "Unmasked" ? <Alert type="warning" showIcon title="Confidentiality exception" description={exposure} /> : null}
          <Form.Item hidden={mode !== "Unmasked"} name="unmaskedOrigins" label="Trusted exact origins for Unmasked capture"
            rules={mode === "Unmasked" ? [{ required: true, type: "array", min: 1, message: "Select at least one deployment-approved exact origin." }] : []}>
            <Select mode="multiple" options={policy.deployment.unmaskedOriginCeiling.map(value => ({ value, label: value }))} />
          </Form.Item>
        <Form.Item hidden={mode === "Disabled"} name="trustedGraphicsOrigins" label="Trusted graphics origins in Protected mode"
          extra="Canvas/SVG exception only; sensitive DOM and child frames remain masked.">
          <Select mode="multiple" options={policy.deployment.graphicsOriginCeiling.map(value => ({ value, label: value }))} />
        </Form.Item>
        <Flex gap={token.paddingXS} wrap>
          <Button type="primary" htmlType="submit" loading={saving}>Save screenshot privacy</Button>
          <Button disabled={saving || loading} onClick={() => void load()}>Reload saved policy</Button>
        </Flex>
      </Form>
    </> : null}
  </Flex>;
}
