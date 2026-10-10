import { useExecutionBudgetLimits } from "./executionBudgetLimits";
import { memo, useCallback, useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Alert, Button, Collapse, Flex, Form, InputNumber, Select, Spin, Typography, theme } from 'antd';
import { getExecutionBudgets, setExecutionBudgets, type ExecutionBudgetPolicy, type ExecutionBudgetProfile, type ExecutionBudgetLimits } from '../../services/adminApi';
import type { DefinitionCandidate } from './definitionCandidate';

const classes = ['standard', 'interactiveBrowser', 'unattendedBoundBrowser'] as const;
const labels = { standard: 'Standard', interactiveBrowser: 'Interactive Browser', unattendedBoundBrowser: 'Unattended Bound Browser' };
const fields = ['maxSteps', 'durationSeconds', 'perToolSeconds'] as const;
const fieldLabels = { maxSteps: 'Steps', durationSeconds: 'Duration (seconds)', perToolSeconds: 'Per-tool timeout (seconds)' };
const minimums = { maxSteps: 8, durationSeconds: 60, perToolSeconds: 1 };
const emptyPolicy: ExecutionBudgetPolicy = {};
function ExecutionBudgetPanel({ children, needsAttention }: { children: ReactNode; needsAttention?: boolean }) {
  return <section aria-label="Execution budgets"><Collapse defaultActiveKey={['budgets']} items={[{
    key: 'budgets', label: 'Execution budgets',
    extra: needsAttention ? <Typography.Text type="danger" aria-hidden>Needs attention</Typography.Text> : undefined,
    children,
  }]} /></section>;
}
function preset(kind: typeof classes[number], value: number): ExecutionBudgetProfile {
  const [steps, seconds] = kind === 'interactiveBrowser' ? [48, 300] : kind === 'unattendedBoundBrowser' ? [32, 240] : [24, 180];
  return { maxSteps: steps * (value + 1), durationSeconds: seconds * (value + 1), perToolSeconds: 30, preset: value };
}
export function validExecutionBudgets(policy: ExecutionBudgetPolicy, limits: ExecutionBudgetLimits | null = null) {
  if (!policy || typeof policy !== 'object' || Array.isArray(policy)) return false;
  return classes.every(kind => { const p = policy[kind]; return p == null || limits !== null && typeof p === 'object' && Number.isInteger(p.preset) && p.preset >= 0 && p.preset <= 3
    && (p.preset === 3 || JSON.stringify(preset(kind, p.preset)) === JSON.stringify({ maxSteps: p.maxSteps, durationSeconds: p.durationSeconds, perToolSeconds: p.perToolSeconds, preset: p.preset }))
    && Number.isInteger(p.maxSteps) && p.maxSteps >= 8 && p.maxSteps <= limits.maxSteps
    && Number.isInteger(p.durationSeconds) && p.durationSeconds >= 60 && p.durationSeconds <= limits.durationSeconds
    && Number.isInteger(p.perToolSeconds) && p.perToolSeconds >= 1 && p.perToolSeconds <= limits.perToolSeconds; });
}
const BudgetFields = memo(function BudgetFields({ value, inherited, instance, disabled, limits, onChange }: {
  limits: ExecutionBudgetLimits; value: ExecutionBudgetPolicy; inherited?: ExecutionBudgetPolicy; instance?: boolean; disabled?: boolean; onChange: (v: ExecutionBudgetPolicy) => void;
}) {
  const { token } = theme.useToken();
  const id = useId();
  return <Flex vertical gap={token.padding} className="admin-settings-form">
    <Typography.Text type="secondary">Host limits: {limits.maxSteps} steps · {limits.durationSeconds / 60} minutes · {limits.perToolSeconds} seconds per tool. Cleanup and final reply share the total budget. Changes apply to the next run.</Typography.Text>
    {classes.map(kind => { const own = value[kind]; const effective = own ?? inherited?.[kind] ?? preset(kind, 0);
      const update = (p: ExecutionBudgetProfile | null) => onChange({ ...value, [kind]: p });
      return <Flex vertical gap={token.paddingXS} key={kind} role="group" aria-label={`${labels[kind]} budget`}>
        <Typography.Text strong>{labels[kind]}</Typography.Text>
        <Flex wrap align="end" gap={token.paddingSM}>
        {instance && <Form.Item label="Budget source" layout="vertical" htmlFor={`${id}-${kind}-source`} style={{ flex: '1 1 12rem', margin: 0 }}><Select id={`${id}-${kind}-source`} aria-label={`${labels[kind]} inheritance`} value={own ? 'override' : 'inherit'} disabled={disabled}
          options={[{ value: 'inherit', label: 'Inherit Definition' }, { value: 'override', label: 'Override' }]}
          onChange={v => update(v === 'inherit' ? null : { ...effective })} /></Form.Item>}
        {(!instance || own) && <Form.Item label="Execution profile" layout="vertical" htmlFor={`${id}-${kind}-profile`} style={{ flex: '1 1 12rem', margin: 0 }}>
          <Select id={`${id}-${kind}-profile`} aria-label={`${labels[kind]} profile`} disabled={disabled} value={own?.preset ?? 0}
            options={[{ value: 0, label: 'Standard' }, { value: 1, label: 'Extended' }, { value: 2, label: 'Deep Workflow' }, { value: 3, label: 'Custom' }]}
            onChange={v => update(v === 3 ? { ...effective, preset: 3 } : preset(kind, v))} />
        </Form.Item>}
        {!instance && own && <Button disabled={disabled} onClick={() => update(null)} aria-label={`Reset ${labels[kind]} to system default`}>Reset to system default</Button>}
        </Flex>
        <Typography.Text type="secondary">{effective.maxSteps} steps · {effective.durationSeconds / 60} minutes · {effective.perToolSeconds}s per tool · {own ? instance ? 'Instance override' : 'Definition default' : inherited?.[kind] ? 'Inherited Definition default' : 'System default'}</Typography.Text>
        {(!instance || own) && <>
          <Collapse items={[{ key: 'advanced', label: 'Advanced limits', children: <Flex wrap gap={token.paddingSM}>
            {fields.map(field => { const invalid = !Number.isInteger(effective[field]) || effective[field] < minimums[field] || effective[field] > limits[field];
              return <Form.Item key={field} label={fieldLabels[field]} layout="vertical" htmlFor={`${id}-${kind}-${field}`}
                style={{ flex: '1 1 12rem', margin: 0 }} validateStatus={invalid ? 'error' : undefined}
                help={invalid ? `Enter a whole number from ${minimums[field]} to ${limits[field]}.` : undefined}>
              <InputNumber id={`${id}-${kind}-${field}`} aria-label={`${labels[kind]} ${field}`} aria-invalid={invalid} disabled={disabled} step={1} min={minimums[field]}
                style={{ width: '100%' }} max={limits[field]} value={effective[field]} onInput={text => update({ ...effective, [field]: Number(text), preset: 3 })} onChange={v => update({ ...effective, [field]: v ?? 0, preset: 3 })} />
            </Form.Item>; })}
          </Flex> }]} />
        </>}
      </Flex>;
    })}
    {!validExecutionBudgets(value, limits) && <Alert showIcon type="error" title="Use whole values within the host limits before saving." />}
  </Flex>;
});
export function DefinitionExecutionBudgets({ candidate, busy, readOnly, onChange }: {
  candidate: DefinitionCandidate; busy: boolean; readOnly?: boolean; onChange: (v: DefinitionCandidate) => void;
}) {
  // Other draft fields change frequently. Keep this control group stable while
  // merging budget edits into the latest candidate, rather than an older draft.
  const { limits, error, reload } = useExecutionBudgetLimits();
  const latest = useRef({ candidate, onChange });
  latest.current = { candidate, onChange };
  const updateBudget = useCallback((executionBudgets: ExecutionBudgetPolicy) => {
    const current = latest.current;
    current.onChange({ ...current.candidate, executionBudgets });
  }, []);
  return <ExecutionBudgetPanel needsAttention={!!error || !!limits && !validExecutionBudgets((candidate.executionBudgets as ExecutionBudgetPolicy | null) ?? emptyPolicy, limits)}>
    {error && <Alert showIcon type="error" title={error} action={<Button onClick={reload}>Retry limits</Button>} />}
    {!limits && !error && <Spin aria-label="Loading execution limits" />}
    {limits && <BudgetFields limits={limits} value={(candidate.executionBudgets as ExecutionBudgetPolicy | null) ?? emptyPolicy} disabled={busy || readOnly}
      onChange={updateBudget} /> }
  </ExecutionBudgetPanel>;
}
export function InstanceExecutionBudgets({ instanceId, archived, onUpdated }: { instanceId: string; archived: boolean; onUpdated: () => void }) {
  const { token } = theme.useToken();
  const { limits, error: limitsError, reload: reloadLimits } = useExecutionBudgetLimits();
  const [loaded, setLoaded] = useState<Awaited<ReturnType<typeof getExecutionBudgets>> | null>(null);
  const [value, setValue] = useState<ExecutionBudgetPolicy>({}); const [busy, setBusy] = useState(false);
  const preserveDraft = useRef(false);
  const scope = useRef(instanceId);
  const epoch = useRef(0);
  const [error, setError] = useState<string | null>(null); const [attempt, setAttempt] = useState(0);
  useEffect(() => { let current = true; epoch.current++;
    if (scope.current !== instanceId) { scope.current = instanceId; preserveDraft.current = false; setValue({}); }
    setLoaded(null); setError(null); setBusy(false);
    getExecutionBudgets(instanceId).then(v => { if (current) { setLoaded(v); if (!preserveDraft.current) setValue(v.executionBudgets ?? {}); preserveDraft.current = false; } })
      .catch(e => { if (current) setError(e instanceof Error ? e.message : 'Budgets could not be loaded.'); });
    return () => { current = false; epoch.current++; };
  }, [instanceId, attempt]);
  const dirty = loaded && JSON.stringify(value) !== JSON.stringify(loaded.executionBudgets ?? {});
  async function save() {
    if (!limits || !loaded || !validExecutionBudgets(value, limits)) return;
    const generation = epoch.current;
    setBusy(true); setError(null);
    try { const updated = await setExecutionBudgets(instanceId, loaded.revision, value);
      if (generation === epoch.current) { setLoaded({ ...loaded, revision: updated.revision, executionBudgets: value }); onUpdated(); }
    }
    catch (e) { if (generation === epoch.current) setError(e instanceof Error ? e.message : 'The Instance changed. Reload and review before saving.'); }
    finally { if (generation === epoch.current) setBusy(false); }
  }
  return <ExecutionBudgetPanel needsAttention={!!error || !!limitsError || !!limits && !validExecutionBudgets(value, limits)}><Flex vertical gap={token.padding}>
    {error && <Alert showIcon type="error" title={error} action={<Button onClick={() => { preserveDraft.current = !!dirty; setAttempt(v => v + 1); }}>Reload</Button>} />}
    {limitsError && <Alert showIcon type="error" title={limitsError} action={<Button onClick={reloadLimits}>Retry limits</Button>} />}
    {!limits && !limitsError && <Spin aria-label="Loading execution limits" />}
    {!loaded ? !error && <Spin aria-label="Loading execution budgets" /> : <>
      {limits && <BudgetFields limits={limits} value={value} inherited={loaded.definitionDefaults ?? emptyPolicy} instance disabled={busy || archived} onChange={setValue} /> }
      {dirty && <Typography.Text type="secondary">Unsaved budget changes</Typography.Text>}
      <Flex gap={token.paddingSM} wrap><Button type="primary" loading={busy} disabled={!limits || !dirty || archived || !validExecutionBudgets(value, limits)} aria-label="Save execution budgets" onClick={() => void save()}>Save</Button>
        <Button disabled={!dirty || busy} aria-label="Discard execution budget changes" onClick={() => setValue(loaded.executionBudgets ?? {})}>Discard</Button></Flex>
    </>}
  </Flex></ExecutionBudgetPanel>;
}
