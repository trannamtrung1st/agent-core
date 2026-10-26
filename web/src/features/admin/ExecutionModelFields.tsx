import { orderedReasoningEfforts, retainedReasoningEffort } from "../models/reasoningEfforts";
import { modelChoiceOptions } from "../models/modelLabels";
import { useId } from "react";
import { Flex, Select, theme } from "antd";
import type { ModelDescriptor } from "../../services/api";

/** Shared catalog selection for unattended and registration execution. */
export function ExecutionModelFields({ models, offeringId, reasoningEffort, disabled, modelLabel, effortLabel,
  defaultLabel, defaultOfferingId, showLabels = false, onChange }: { models: Pick<ModelDescriptor, "offeringId" | "displayName" | "supportedReasoningEfforts" | "defaultReasoningEffort" | "connectionLabel">[]; offeringId: string; reasoningEffort: string; defaultOfferingId?: string;
  disabled: boolean; modelLabel: string; effortLabel: string; defaultLabel: string;
  showLabels?: boolean; onChange: (offeringId: string, reasoningEffort: string) => void }) {
  const { token } = theme.useToken();
  const modelInputId = useId();
  const effortId = useId();
  const availableEfforts = orderedReasoningEfforts([...new Set(models.flatMap(model => model.supportedReasoningEfforts))]);
  const selected = models.find(model => model.offeringId === (offeringId || defaultOfferingId));
  const deferred = offeringId === "auto" || !offeringId && !selected;
  return <Flex vertical gap={token.paddingXS}>
    {showLabels ? <label htmlFor={modelInputId}>{modelLabel}</label> : null}
    <Select id={modelInputId} aria-label={modelLabel} value={offeringId} disabled={disabled} showSearch optionFilterProp="label"
      options={[{ value: "", label: defaultLabel }, { value: "auto", label: "Auto · eligible models per new Run" }, ...modelChoiceOptions(models, offeringId)]}
      onChange={key => key === "auto" ? onChange(key, "auto") : onChange(key, retainedReasoningEffort(models.find(model => model.offeringId === (key || defaultOfferingId)), reasoningEffort))} />
    {deferred || (selected?.supportedReasoningEfforts.length ?? 0) > 0 || !selected && !!reasoningEffort ? <Flex vertical gap={token.paddingXS}>
      <label htmlFor={effortId}>{effortLabel}</label>
      <Select id={effortId} aria-label={effortLabel} value={reasoningEffort} disabled={disabled || !selected && !!offeringId && offeringId !== "auto"}
      options={deferred ? [{ value: "", label: offeringId ? "Model default" : "Use inherited reasoning" }, ...(!offeringId ? [{ value: "modelDefault", label: "Model default" }] : []), { value: "auto", label: "Auto reasoning" }, ...availableEfforts.map(value => ({ value, label: value }))] : selected ? [{ value: "", label: offeringId ? "Model default" : "Use inherited reasoning" }, ...(!offeringId ? [{ value: "modelDefault", label: "Model default" }] : []), { value: "auto", label: "Auto reasoning" }, ...orderedReasoningEfforts(selected.supportedReasoningEfforts).map(value => ({ value, label: value }))]
        : [{ value: reasoningEffort, label: `${reasoningEffort} · Unavailable` }]}
      onChange={effort => onChange(offeringId, effort)} />
    </Flex> : null}
  </Flex>;
}
