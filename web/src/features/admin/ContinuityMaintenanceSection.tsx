import { AdminErrorNotice } from "./adminFailure";
import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Button, Flex, Form, InputNumber, Spin, Switch, Typography, theme } from "antd";
import { instanceContinuityRequest as request, type ContinuityMaintenanceSettings } from "../../services/adminApi";
import { describeAdminError, type AdminFailureNotice } from "./adminErrors";

export function ContinuityMaintenanceSection({ instanceId }: { instanceId: string }) {
  const { token } = theme.useToken();
  const [saved, setSaved] = useState<ContinuityMaintenanceSettings | null>(null);
  const [usesDefault, setUsesDefault] = useState(true);
  const [minutes, setMinutes] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<AdminFailureNotice | null>(null);
  const order = useRef(0);
  const mutation = useRef(false);
  const apply = (value: ContinuityMaintenanceSettings) => {
    setSaved(value); setUsesDefault(value.usesDefault);
    setMinutes((value.configuredIntervalSeconds ?? value.defaultIntervalSeconds) / 60);
  };
  const reload = useCallback(async () => {
    const generation = ++order.current; setBusy(true);
    try {
      const value = await request<ContinuityMaintenanceSettings>(instanceId, "continuity-maintenance");
      if (order.current === generation) { apply(value); setError(null); }
    } catch (reason) {
      if (order.current === generation) setError(describeAdminError(reason, "Unable to load automatic continuity review. Reload and try again."));
    } finally { if (order.current === generation) setBusy(false); }
  }, [instanceId]);
  useEffect(() => { setSaved(null); void reload(); return () => { order.current++; }; }, [reload]);
  const seconds = minutes === null ? null : Math.round(minutes * 60);
  const invalid = !usesDefault && (!saved || seconds === null || !Number.isFinite(seconds) || (minutes ?? 0) * 60 + 1e-7 < saved.minimumIntervalSeconds || (minutes ?? 0) * 60 - 1e-7 > saved.maximumIntervalSeconds);
  const changed = saved && (usesDefault ? saved.configuredIntervalSeconds !== null : seconds !== saved.configuredIntervalSeconds);
  async function save() {
    if (!saved || invalid || mutation.current) return;
    mutation.current = true; const generation = ++order.current; setBusy(true); setError(null);
    try {
      const value = await request<ContinuityMaintenanceSettings>(instanceId, "continuity-maintenance", "PUT", {
        expectedRevision: saved.revision, intervalSeconds: usesDefault ? null : seconds
      });
      if (order.current === generation) apply(value);
    } catch (reason) {
      if (order.current === generation) setError(describeAdminError(reason, "Automatic continuity review could not be saved. Your edit is retained; reload to review the current setting."));
    } finally { mutation.current = false; if (order.current === generation) setBusy(false); }
  }
  return <section aria-label="Automatic continuity review">
    <Flex vertical gap={token.paddingXS}>
      <Typography.Title level={5} style={{ margin: 0 }}>Automatic continuity review</Typography.Title>
      <Typography.Paragraph type="secondary" style={{ marginBottom: 0, maxWidth: "72ch" }}>Controls how often the host reviews active Sessions for stable Experience checkpoints. This is deterministic maintenance; autonomous Thought scheduling is configured separately.</Typography.Paragraph>
      {!saved && busy ? <Spin aria-label="Loading automatic continuity review" /> : null}
      {saved ? <>
        <Typography.Text>Effective interval: {saved.effectiveIntervalSeconds / 60} minutes · {saved.usesDefault || !saved.configuredIntervalAllowed ? "System default" : "Custom interval"}</Typography.Text>
        {!saved.configuredIntervalAllowed ? <Alert type="warning" showIcon title="Saved interval is outside the current system range"
          description={`The system default of ${saved.defaultIntervalSeconds / 60} minutes is effective. Choose an allowed interval or use the system default.`} /> : null}
        <Form layout="vertical" onFinish={() => void save()} style={{ maxWidth: "48rem" }}>
          <Flex wrap align="center" gap={token.paddingXS} style={{ marginBottom: token.paddingXS }}>
            <Switch aria-label="Use system default for continuity review" checked={usesDefault} disabled={busy}
              onChange={value => { setUsesDefault(value); if (value) setMinutes(saved.defaultIntervalSeconds / 60); }} />
            <Typography.Text>Use system default ({saved.defaultIntervalSeconds / 60} minutes)</Typography.Text>
          </Flex>
          <Form.Item label="Review interval (minutes)" validateStatus={invalid ? "error" : undefined}
            help={invalid ? `Enter an interval between ${saved.minimumIntervalSeconds / 60} and ${saved.maximumIntervalSeconds / 60} minutes.` : undefined}
            extra={`Allowed range: ${saved.minimumIntervalSeconds / 60}–${saved.maximumIntervalSeconds / 60} minutes. Experience must be enabled for automatic review.`}>
            <InputNumber aria-label="Continuity review interval" value={minutes} disabled={busy || usesDefault} changeOnBlur={false}
              onChange={setMinutes} style={{ width: "12rem", maxWidth: "100%" }} />
          </Form.Item>
          <Flex wrap gap={token.paddingXS}>
            <Button type="primary" htmlType="submit" loading={busy} disabled={!changed || invalid}>Save review interval</Button>
            <Button disabled={busy} onClick={() => void reload()}>Reload review interval</Button>
          </Flex>
        </Form>
      </> : null}
      {error ? <Alert type="error" showIcon title={<AdminErrorNotice message={error.message} diagnosticId={error.diagnosticId} showDetailsLabel />} action={<Button disabled={busy} onClick={() => void reload()}>Reload</Button>} /> : null}
    </Flex>
  </section>;
}
