import { showAdminFormValidation } from './adminFormFeedback';
import { useAdminUnsavedChanges } from './useAdminUnsavedChanges';
import { useEffect, useRef, useState } from "react";
import { Alert, App, Button, Descriptions, Flex, Form, Select, Spin, Typography, theme } from "antd";
import { confirmAction } from "../../app/confirmAction";
import { getBrowserPrivacy, saveBrowserPrivacy, type BrowserPrivacy, type BrowserPrivacyMode } from "../../services/systemBrowserPrivacy";
import { IdentityError } from "../../services/identity";
import { AccessError } from "../access/AccessFeedback";
import { useAdminDetailLayout } from "./useAdminDetailLayout";

type Fields = { mode: BrowserPrivacyMode; unmaskedOrigins: string[]; trustedGraphicsOrigins: string[] };
const exposure = "Unmasked screenshots can expose credentials, OTPs, personal information and confidential application data to model providers and immutable Session artifacts. All child frames remain masked. Semantic redaction and browser origin restrictions still apply.";

/** Global Host policy has one edit owner under Platform → Browser privacy. */
export function BrowserPrivacySection() {
  const { modal, message } = App.useApp();
  const { token } = theme.useToken();
  const detailLayout = useAdminDetailLayout();
  const [form] = Form.useForm<Fields>();
  const mode = Form.useWatch("mode", form);
  const fields = Form.useWatch([], form);
  const [policy, setPolicy] = useState<BrowserPrivacy | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);
  const active = useRef<AbortController | null>(null);
  const sending = useRef(false); const blocked = useRef(false); const [requiresReload, setRequiresReload] = useState(false);
  const confirmation = useRef<ReturnType<typeof confirmAction> | null>(null);

  const dirty = !!policy && !!fields && JSON.stringify(fields) !== JSON.stringify({ mode: policy.saved.mode, unmaskedOrigins: policy.saved.unmaskedOrigins, trustedGraphicsOrigins: policy.saved.trustedGraphicsOrigins });
  useAdminUnsavedChanges('system:browser-privacy', dirty, () => {
    if (!policy) return;
    form.resetFields();
    accept(policy);
    setError(null); setNotice(null);
  });
  function accept(value: BrowserPrivacy) {
    setPolicy(value);
    form.setFieldsValue({ mode: value.saved.mode, unmaskedOrigins: value.saved.unmaskedOrigins,
      trustedGraphicsOrigins: value.saved.trustedGraphicsOrigins });
  }
  async function load(preserveDraft = false) {
    if (sending.current) return;
    const draft = preserveDraft && dirty ? form.getFieldsValue(true) as Fields : undefined;
    active.current?.abort(); const controller = new AbortController(); active.current = controller;
    setLoading(true); setError(null); setNotice(null);
    try {
      const value = await getBrowserPrivacy(controller.signal);
      if (!controller.signal.aborted) { accept(value); if (draft) form.setFieldsValue(draft); blocked.current = false; setRequiresReload(false); }
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure); blocked.current = true; setRequiresReload(true);
        if (failure instanceof IdentityError && [401,403,404].includes(failure.status)) { setPolicy(null); form.resetFields(); }
      }
    } finally { if (!controller.signal.aborted) setLoading(false); }
  }
  useEffect(() => { void load(); return () => { active.current?.abort(); confirmation.current?.destroy(); }; }, []); // eslint-disable-line react-hooks/exhaustive-deps

  async function save(values: Fields, acknowledgeExposure: boolean) {
    if (!policy || sending.current || blocked.current) return;
    const controller = new AbortController(); active.current = controller; sending.current = true;
    setSaving(true); setError(null); setNotice(null);
    try {
      const value = await saveBrowserPrivacy({ ...values, unmaskedOrigins: values.mode === "Unmasked" ? values.unmaskedOrigins : [],
        expectedRevision: policy.saved.revision, acknowledgeExposure }, controller.signal);
      if (!controller.signal.aborted) { accept(value); setNotice(value.durable
        ? "Screenshot privacy saved. Restart the host to activate this revision." : value.activation); }
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure);
        if (!(failure instanceof IdentityError) || failure.status !== 400) { blocked.current = true; setRequiresReload(true); }
        if (failure instanceof IdentityError && [401,403,404].includes(failure.status)) { setPolicy(null); form.resetFields(); }
      }
    } finally { sending.current = false; if (!controller.signal.aborted) setSaving(false); }
  }
  function submit(values: Fields) {
    if (values.mode === "Unmasked" && !values.unmaskedOrigins?.length) {
      const errorFields = [{ name: ["unmaskedOrigins"] as ["unmaskedOrigins"], errors: ["Select at least one deployment-approved exact origin."] }];
      form.setFields(errorFields);
      showAdminFormValidation(message, form, {errorFields});
      return;
    }
    if (confirmation.current || blocked.current || sending.current) return;
    const selected = { ...values, unmaskedOrigins: [...values.unmaskedOrigins], trustedGraphicsOrigins: [...values.trustedGraphicsOrigins] };
    if (values.mode === "Unmasked") confirmation.current = confirmAction(modal, { title: "Authorize unmasked screenshots for all organizations?", danger: true,
      content: <Flex vertical gap={token.paddingSM}><Typography.Paragraph>{exposure}</Typography.Paragraph>
        <Typography.Text>Exact origins: {values.unmaskedOrigins.join(", ")}</Typography.Text></Flex>,
      okText: "Acknowledge & save", onOk: () => save(selected, true), afterClose: () => { confirmation.current = null; } });
    else void save(selected, false);
  }
  return <Flex vertical gap={token.paddingSM} role="region" aria-label="Screenshot privacy">
    <Flex wrap justify="space-between" align="center" gap={token.paddingXS}><Typography.Title level={5}>Screenshot privacy</Typography.Title><Button disabled={saving || loading} onClick={() => void load(true)}>Reload saved policy</Button></Flex>
    <Typography.Text type="secondary">Global Browser policy for every agent in every organization. Definitions, agents and page content cannot expand these permissions.</Typography.Text>
    {loading ? <Spin aria-label="Loading screenshot privacy" /> : null}
    {error ? <AccessError error={error} retry={() => void load(true)} /> : null}
    {requiresReload ? <Alert type="warning" showIcon title="Read current policy before saving again" description="The saved outcome or current authority must be checked. Reload retains your edits and does not repeat the save." /> : null}
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
      <Form form={form} layout="vertical" className="admin-config-form" disabled={saving || loading || requiresReload} onFinish={submit} onFinishFailed={failure=>showAdminFormValidation(message,form,failure)}>
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
          <Button type="primary" htmlType="submit" loading={saving} disabled={!dirty || requiresReload || loading}>Save screenshot privacy</Button>
        </Flex>
      </Form>
    </> : null}
  </Flex>;
}
