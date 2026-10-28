import { useEffect, useState } from 'react';
import { Alert, Button, Checkbox, Flex, Form, Input, InputNumber, Select, Typography } from 'antd';
import type { AutomationWorkflowDestination } from '../../services/tenantAutomations';
import { readWorkflowGraph, workflowApi, workflowCatalog, type WorkflowDefinition, type WorkflowVersion } from '../../services/tenantWorkflows';
import { useIdentityQuery } from '../access/useIdentityQuery';
import { AccessError, AccessLoading } from '../access/AccessFeedback';

/** Saved constant inputs: no event expressions, templating or source-model execution. */
export function WorkflowAutomationDestination({ tenant, instance, value, disabled, onChange, onReady }: {
  tenant: string; instance: string; value?: AutomationWorkflowDestination | null; disabled: boolean;
  onChange: (value: AutomationWorkflowDestination) => void; onReady: (ready: boolean) => void;
}) {
  const [page, setPage] = useState(0); const [items, setItems] = useState<WorkflowDefinition[]>([]); const [error, setError] = useState<unknown>(); const [total, setTotal] = useState(0);
  const [selected, setSelected] = useState(value?.workflowId); const [version, setVersion] = useState(value?.version ?? 1);
  useEffect(() => { const controller = new AbortController(); setError(undefined);
    void workflowCatalog(tenant, page, controller.signal).then(result => { if (!controller.signal.aborted) { setItems(result.items); setTotal(result.total); } }).catch(failure => { if (!controller.signal.aborted) { setItems([]); setError(failure); } });
    return () => controller.abort();
  }, [tenant, page]);
  const definition = useIdentityQuery<WorkflowDefinition>(selected ? workflowApi(tenant, `/${selected}`) : null);
  const publication = useIdentityQuery<WorkflowVersion>(selected ? workflowApi(tenant, `/${selected}/versions/${version}`) : null);
  const graph = publication.data ? readWorkflowGraph(publication.data.graph.canonicalJson) : undefined;
  const eligible = !!graph && !!definition.data && !definition.data.retired && graph.allowedOrigins.includes(2)
    && (!graph.nodes.some(node => node.kind === 1) || graph.coordinatorInstanceIds.includes(instance));
  const exact = !!value && value.workflowId === selected && value.version === version && value.graphHash === publication.data?.graph.hash;
  const valid = eligible && exact && !!graph?.inputs.every(field => {
    const input = value?.inputs[field.key]; if (input == null || input === '') return !field.required;
    return field.kind === 0 ? typeof input === 'string' && input.trim().length > 0 && input.length <= field.maxLength
      : field.kind === 1 ? typeof input === 'boolean' : field.kind === 2 ? typeof input === 'number' && Number.isFinite(input)
      : field.kind === 3 ? typeof input === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(input) : field.choices?.includes(String(input));
  });
  useEffect(() => { onReady(valid); return () => onReady(false); }, [valid, onReady]);
  const pin = () => { if (publication.data && selected && graph) onChange({ workflowId: selected, version, graphHash: publication.data.graph.hash,
    inputs: Object.fromEntries(graph.inputs.filter(field => field.kind === 1).map(field => [field.key, false])) }); };
  const input = (key: string, entry: unknown) => { if (value) { const inputs = { ...value.inputs }; if (entry == null || entry === '') delete inputs[key]; else inputs[key] = entry; onChange({ ...value, inputs }); } };
  return <Flex vertical gap="var(--ac-space-default)">
    <Typography.Paragraph type="secondary">Pins a published process and constant typed inputs. Worker steps own their Space, Environment and model. Current executor permissions are checked again on every occurrence.</Typography.Paragraph>
    {error !== undefined && <AccessError error={error} />}
    <Form.Item label="Published Workflow"><Select aria-label="Automation Workflow" disabled={disabled} value={selected} placeholder="Choose a readable published Workflow" labelRender={option => items.find(item => item.workflowId === option.value)?.name ?? definition.data?.name ?? 'Saved Workflow'}
      options={items.filter(item => item.publishedVersion > 0 && !item.retired).map(item => ({ value: item.workflowId, label: item.name }))} onChange={id => { setSelected(id); setVersion(items.find(item => item.workflowId === id)?.publishedVersion ?? 1); }} /></Form.Item>
    <Flex wrap gap="var(--ac-space-compact)">{page > 0 && <Button disabled={disabled} onClick={() => setPage(p => p - 1)}>Previous Workflows</Button>}{(page + 1) * 20 < total && <Button disabled={disabled} onClick={() => setPage(p => p + 1)}>More Workflows</Button>}</Flex>
    {selected && <Form.Item label="Published version"><InputNumber aria-label="Automation Workflow version" disabled={disabled} min={1} max={definition.data?.publishedVersion ?? version} value={version} onChange={v => setVersion(v ?? 1)} /></Form.Item>}
    {publication.loading || definition.loading ? <AccessLoading /> : publication.error || definition.error ? <AccessError error={publication.error ?? definition.error} retry={() => { publication.reload(); definition.reload(); }} /> : selected && !eligible ? <Alert type="warning" showIcon title="This version is unavailable to this Automation" description="Allow Automation origins and include this executor among the coordinators when the graph has Agent tasks. Check retirement and current access." /> : eligible && <>
      {!exact ? <Button disabled={disabled} onClick={pin}>Pin version {version} and prepare inputs</Button> : <>
        <Typography.Text>Published version {version} is pinned.</Typography.Text>
        {graph?.inputs.map(field => <Form.Item key={field.key} label={field.label} required={field.required}>
          {field.kind === 1 ? <Checkbox aria-label={field.label} disabled={disabled} checked={value.inputs[field.key] === true} onChange={e => input(field.key, e.target.checked)}>Yes</Checkbox>
            : field.kind === 2 ? <InputNumber aria-label={field.label} disabled={disabled} value={value.inputs[field.key] as number | undefined} min={-1e12} max={1e12} onChange={entry => input(field.key, entry)} />
            : field.kind === 4 ? <Select aria-label={field.label} disabled={disabled} value={value.inputs[field.key] as string | undefined} options={field.choices?.map(choice => ({ value: choice, label: choice }))} onChange={entry => input(field.key, entry)} />
            : <Input aria-label={field.label} disabled={disabled} type={field.kind === 3 ? 'date' : 'text'} value={value.inputs[field.key] as string ?? ''} maxLength={field.maxLength} onChange={e => input(field.key, e.target.value)} />}
        </Form.Item>)}
        {!graph?.inputs.length && <Typography.Text type="secondary">No inputs required.</Typography.Text>}
      </>}
    </>}
  </Flex>;
}
