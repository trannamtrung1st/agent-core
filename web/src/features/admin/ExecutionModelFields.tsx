import { orderedReasoningEfforts, retainedReasoningEffort } from "../models/reasoningEfforts";
import { modelChoiceOptions } from "../models/modelLabels";
import { useId } from "react";
import { Flex, Select, theme } from "antd";
import type { ModelDescriptor } from "../../services/api";

/** Shared catalog selection for unattended and registration execution. */
export function ExecutionModelFields({ models, offeringId, reasoningEffort, disabled, modelLabel, effortLabel,
  defaultLabel, defaultOfferingId, showLabels = false, onChange }: { models: ModelDescriptor[]; offeringId: string; reasoningEffort: string; defaultOfferingId?: string;
  disabled: boolean; modelLabel: string; effortLabel: string; defaultLabel: string;
  showLabels?: boolean; onChange: (offeringId: string, reasoningEffort: string) => void }) {
  const { token } = theme.useToken();
  const modelInputId = useId();
  const effortId = useId();
  const selected = models.find(model => model.offeringId === (offeringId || defaultOfferingId));
  return <Flex vertical gap={token.paddingXS}>
    {showLabels ? <label htmlFor={modelInputId}>{modelLabel}</label> : null}
    <Select id={modelInputId} aria-label={modelLabel} value={offeringId} disabled={disabled} showSearch optionFilterProp="label"
      options={[{ value: "", label: defaultLabel }, ...modelChoiceOptions(models, offeringId)]}
      onChange={key => onChange(key, retainedReasoningEffort(models.find(model => model.offeringId === (key || defaultOfferingId)), reasoningEffort))} />
    {(selected?.supportedReasoningEfforts.length ?? 0) > 0 || !selected && !!reasoningEffort ? <Flex vertical gap={token.paddingXS}>
      <label htmlFor={effortId}>{effortLabel}</label>
      <Select id={effortId} aria-label={effortLabel} value={reasoningEffort} disabled={disabled || !selected}
      options={selected ? [{ value: "", label: "Model default" }, ...orderedReasoningEfforts(selected.supportedReasoningEfforts).map(value => ({ value, label: value }))]
        : [{ value: reasoningEffort, label: `${reasoningEffort} · Unavailable` }]}
      onChange={effort => onChange(offeringId, effort)} />
    </Flex> : null}
  </Flex>;
}
