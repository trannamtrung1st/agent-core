import { Flex, Select, theme } from "antd";
import type { ModelDescriptor } from "../../services/api";

/** Shared catalog selection for unattended and registration execution. */
export function ExecutionModelFields({ models, modelKey, reasoningEffort, disabled, modelLabel, effortLabel,
  defaultLabel, onChange }: { models: ModelDescriptor[]; modelKey: string; reasoningEffort: string;
  disabled: boolean; modelLabel: string; effortLabel: string; defaultLabel: string;
  onChange: (modelKey: string, reasoningEffort: string) => void }) {
  const { token } = theme.useToken();
  const selected = models.find(model => model.key === modelKey);
  return <Flex vertical gap={token.paddingXS}>
    <Select aria-label={modelLabel} value={modelKey} disabled={disabled} showSearch optionFilterProp="label"
      options={[{ value: "", label: defaultLabel }, ...models.map(model => ({ value: model.key, label: model.displayName }))]}
      onChange={key => onChange(key, "")} />
    {selected && selected.supportedReasoningEfforts.length > 0 ? <Select aria-label={effortLabel} value={reasoningEffort} disabled={disabled}
      options={[{ value: "", label: "Model default" }, ...selected.supportedReasoningEfforts.map(value => ({ value, label: value }))]}
      onChange={effort => onChange(modelKey, effort)} /> : null}
  </Flex>;
}
