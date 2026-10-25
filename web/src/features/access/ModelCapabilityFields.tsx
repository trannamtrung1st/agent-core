import { Checkbox, Flex, Select, Typography } from 'antd';
import { blankCapabilities, type ModelCapabilities } from '../../services/models';

/** The same explicit capability declaration is used for tenant offerings and shared presets. */
export function ModelCapabilityFields({ value = blankCapabilities(), onChange, disabled = false }: {
  value?: ModelCapabilities; onChange?: (value: ModelCapabilities) => void; disabled?: boolean;
}) {
  const patch = (next: Partial<ModelCapabilities>) => onChange?.({ ...value, ...next });
  return <Flex vertical gap="var(--ac-space-default)">
    <Typography.Text type="secondary">Declare support for this exact model and transport. Discovery and metadata tests do not prove these capabilities.</Typography.Text>
    <Flex wrap gap="var(--ac-space-default)">
      <Checkbox disabled={disabled} checked={value.tools} onChange={event => patch({ tools: event.target.checked })}>Tool calls</Checkbox>
      <Checkbox disabled={disabled} checked={value.vision} onChange={event => patch({ vision: event.target.checked })}>Image input</Checkbox>
      <Checkbox disabled={disabled} checked={value.structuredOutput} onChange={event => patch({ structuredOutput: event.target.checked })}>Native structured output</Checkbox>
      <Checkbox disabled={disabled} checked={value.forcedToolChoice} onChange={event => patch({ forcedToolChoice: event.target.checked })}>Required / named tool choice</Checkbox>
    </Flex>
    <Flex vertical gap="var(--ac-space-tight)"><label htmlFor="model-hierarchy">Instruction hierarchy</label><Select id="model-hierarchy" aria-label="Instruction hierarchy" disabled={disabled} value={value.hierarchy} onChange={hierarchy => patch({ hierarchy })} options={[
      { value: 'Unsupported', label: 'Unknown or unsupported' }, { value: 'NativeDeveloper', label: 'Native system and developer roles' }, { value: 'OrderedTrustedSections', label: 'Ordered trusted Core / Runtime sections' }
    ]} /></Flex>
    <Flex vertical gap="var(--ac-space-tight)"><label htmlFor="model-semantic-mode">Semantic final output</label><Select id="model-semantic-mode" aria-label="Semantic final output" disabled={disabled} value={value.semanticMode} onChange={semanticMode => patch({ semanticMode })} options={[
      { value: 'NativeStructuredOutput', label: 'Native structured output' }, { value: 'ResponseFunction', label: 'Response function — requires forced tool choice' }
    ]} /></Flex>
    <Flex vertical gap="var(--ac-space-tight)"><label htmlFor="model-efforts">Supported reasoning efforts</label><Select id="model-efforts" aria-label="Supported reasoning efforts" mode="tags" disabled={disabled} value={value.reasoningEfforts} placeholder="No reasoning control" onChange={values => {
      const efforts = [...new Set(values.map(item => item.trim()).filter(Boolean))]; patch({ reasoningEfforts: efforts, defaultReasoningEffort: efforts.includes(value.defaultReasoningEffort ?? '') ? value.defaultReasoningEffort : null });
    }} /></Flex>
    {value.reasoningEfforts.length > 0 && <Flex vertical gap="var(--ac-space-tight)"><label htmlFor="model-default-effort">Default reasoning effort</label><Select id="model-default-effort" aria-label="Default reasoning effort" disabled={disabled} value={value.defaultReasoningEffort} placeholder="Choose a supported effort" options={value.reasoningEfforts.map(effort => ({ value: effort, label: effort }))} onChange={defaultReasoningEffort => patch({ defaultReasoningEffort })} /></Flex>}
  </Flex>;
}

export function validateCapabilities(_: unknown, c?: ModelCapabilities) {
  if (!c || c.reasoningEfforts.length > 12 || c.reasoningEfforts.some(value => !value.trim() || value.length > 32)
    || c.reasoningEfforts.length > 0 && !c.reasoningEfforts.includes(c.defaultReasoningEffort ?? '')
    || c.semanticMode === 'ResponseFunction' && (!c.tools || !c.forcedToolChoice))
    return Promise.reject(new Error('Declare a valid semantic output mode and a default from the supported reasoning efforts.'));
  return Promise.resolve();
}
