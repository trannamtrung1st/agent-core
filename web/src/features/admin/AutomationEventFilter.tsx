import { useEffect, useRef, useState } from "react";
import { Alert, Button, Flex, Form, Input, InputNumber, Select, Typography, theme } from "antd";
import { instanceContinuityRequest, type AutomationTrigger, type FilterTestResult } from "../../services/adminApi";

const failureText: Record<string, string> = {
  "filter-syntax-not-allowed": "Use a Boolean expression with static event paths, comparisons and logical operators.",
  "filter-source-budget": "Keep the expression within 1,024 UTF-8 bytes.",
  "filter-ast-budget": "Simplify the expression: at most 128 nodes and 12 nested levels.",
  "filter-result-not-boolean": "The expression must return true or false.",
  "filter-unsafe-number": "Use safe integer units or strings for values with high precision.",
  "filter-envelope-ambiguous": "Remove duplicate keys from the sample JSON.",
  "filter-envelope-budget": "Keep the sample within 8 KiB.",
  "filter-envelope-schema": "Use an event envelope with schemaVersion 1.",
  "filter-timeout": "The test took too long. Simplify the expression and retry.",
  "filter-worker-budget": "The evaluator is busy. Retry this test.",
  "filter-evaluation-error": "Check missing fields and simplify the expression before retrying."
};
type Trigger = Exclude<AutomationTrigger, { kind: "schedule" }>;
export function AutomationEventFilter({ instanceId, trigger, example, disabled, onChange }: {
  instanceId: string; trigger: Trigger; example: Record<string, unknown>; disabled: boolean; onChange: (trigger: Trigger) => void;
}) {
  const { token } = theme.useToken();
  const [sample, setSample] = useState(() => JSON.stringify(example, null, 2));
  const [result, setResult] = useState<FilterTestResult | null>(null);
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  useEffect(() => { generation.current++; setResult(null); setBusy(false); }, [trigger.filterExpression, sample, instanceId]);
  useEffect(() => { setSample(JSON.stringify(example, null, 2)); }, [example]);
  useEffect(() => () => { generation.current++; }, []);
  async function test() {
    const version = ++generation.current;
    setBusy(true); setResult(null);
    try {
      const event: unknown = JSON.parse(sample);
      const next = await instanceContinuityRequest<FilterTestResult>(instanceId, "automations/filter-test", "POST", { expression: trigger.filterExpression || null, event });
      if (version === generation.current) setResult(next);
    } catch { if (version === generation.current) setResult({ matched: null, status: "error", code: "Check sample JSON and connection, then retry." }); }
    finally { if (version === generation.current) setBusy(false); }
  }
  return <Flex vertical gap={token.paddingSM}>
    <Form.Item label="Event filter" extra="Optional Boolean expression. Blank matches every authorized signal. Filters run before the agent; Run now bypasses the filter.">
      <Input.TextArea aria-label="Event filter expression" rows={3} maxLength={1024} value={trigger.filterExpression ?? ""} disabled={disabled}
        onChange={e => onChange({ ...trigger, filterExpression: e.target.value || null })} />
    </Form.Item>
    <Form.Item label="Dispatch"><Select aria-label="Event dispatch" value={trigger.dispatch?.mode ?? "everyMatch"} disabled={disabled}
      options={[{ value: "everyMatch", label: "Every matching event" }, { value: "coalesceLatest", label: "Group matching events in a window" }]}
      onChange={mode => onChange({ ...trigger, dispatch: mode === "everyMatch" ? { mode } : { mode, windowSeconds: 900 } })} /></Form.Item>
    {trigger.dispatch?.mode === "coalesceLatest" ? <Form.Item label="Window (seconds)" extra="Grouped Runs retain all source event references."><InputNumber aria-label="Event window seconds" min={60} max={3600} value={trigger.dispatch.windowSeconds} disabled={disabled}
      onChange={windowSeconds => onChange({ ...trigger, dispatch: { mode: "coalesceLatest", windowSeconds } })} /></Form.Item> : null}
    <Form.Item label="Sample event JSON" extra="Illustrative fixture only. Avoid pasting credentials or private content."><Input.TextArea aria-label="Sample event JSON" rows={6} maxLength={8192} value={sample} disabled={disabled} onChange={e => setSample(e.target.value)} /></Form.Item>
    <Button aria-label="Test filter" disabled={disabled || busy} loading={busy} style={{ alignSelf: "flex-start" }} onClick={() => void test()}>Test filter</Button>
    <div aria-live="polite" role="status">{result ? <Alert showIcon type={result.status === "error" ? "error" : result.matched ? "success" : "info"}
      title={result.status === "error" ? "Filter error" : result.matched ? "Matched" : "Not matched"}
      description={result.code ? <Typography.Text>{failureText[result.code] ?? result.code}</Typography.Text> : "Test only; no Run was created."} /> : null}</div>
  </Flex>;
}
