import { Flex, Select, Typography } from "antd";

export const DEFAULT_MODEL_KEY = "default";

export type ModelCatalogItem = {
  key: string;
  displayName: string;
  reasoning: boolean;
  supportedReasoningEfforts: readonly string[];
  defaultReasoningEffort?: string | null;
};

export function modelSelectValue(
  sessionKey: string | null,
  pendingKey: string | null,
  inSession: boolean
): string {
  if (!inSession) {
    return pendingKey && pendingKey.length > 0 ? pendingKey : DEFAULT_MODEL_KEY;
  }

  return sessionKey && sessionKey.length > 0 ? sessionKey : pendingKey || DEFAULT_MODEL_KEY;
}

export function selectedCatalogModel(
  models: readonly ModelCatalogItem[],
  value: string,
  defaultKey: string | null
): ModelCatalogItem | null {
  const key = value === DEFAULT_MODEL_KEY ? defaultKey : value;
  return models.find((model) => model.key === key) ?? null;
}

export function nextEffortForModel(
  models: readonly ModelCatalogItem[],
  key: string,
  defaultKey: string | null,
  currentEffort: string | null
): string | null {
  const model = selectedCatalogModel(models, key, defaultKey);
  if (!model?.reasoning) {
    return null;
  }

  if (currentEffort && model.supportedReasoningEfforts.includes(currentEffort)) {
    return currentEffort;
  }

  return model.defaultReasoningEffort ?? null;
}

export function effortSelectValue(
  model: ModelCatalogItem | null,
  sessionEffort: string | null,
  pendingEffort: string | null,
  inSession: boolean
): string | null {
  if (!model?.reasoning) {
    return null;
  }

  const current = inSession ? sessionEffort ?? pendingEffort : pendingEffort;
  if (current && model.supportedReasoningEfforts.includes(current)) {
    return current;
  }

  return model.defaultReasoningEffort ?? null;
}

export function ModelPicker({
  models,
  defaultKey,
  modelValue,
  effortValue,
  disabled,
  layout = "stack",
  onModelChange,
  onEffortChange
}: {
  models: readonly ModelCatalogItem[];
  defaultKey: string | null;
  modelValue: string;
  effortValue: string | null;
  disabled?: boolean;
  layout?: "stack" | "row";
  onModelChange: (key: string) => void;
  onEffortChange: (effort: string | null) => void;
}) {
  if (models.length === 0) {
    return null;
  }

  const defaultModel = models.find((model) => model.key === defaultKey);
  const selected = selectedCatalogModel(models, modelValue, defaultKey);
  const modelOptions = [
    {
      value: DEFAULT_MODEL_KEY,
      label: defaultModel ? `Default · ${defaultModel.displayName}` : "Default"
    },
    ...models.map((model) => ({ value: model.key, label: model.displayName }))
  ];
  const effortOptions = (selected?.supportedReasoningEfforts ?? []).map((effort) => ({
    value: effort,
    label: effort.charAt(0).toUpperCase() + effort.slice(1)
  }));

  const modelSelect = (
    <Select
      aria-label="Model"
      size={layout === "row" ? "small" : "middle"}
      value={modelValue}
      disabled={disabled}
      getPopupContainer={() => document.body}
      options={modelOptions}
      onChange={(next) => onModelChange(typeof next === "string" ? next : DEFAULT_MODEL_KEY)}
      popupMatchSelectWidth={false}
      style={{ width: "100%", minWidth: 0 }}
    />
  );

  const effortSelect =
    selected?.reasoning && effortOptions.length > 0 ? (
      <Select
        aria-label="Reasoning"
        size={layout === "row" ? "small" : "middle"}
        value={effortValue ?? undefined}
        disabled={disabled}
        getPopupContainer={() => document.body}
        options={effortOptions}
        onChange={(next) => onEffortChange(typeof next === "string" ? next : null)}
        popupMatchSelectWidth={false}
        style={{ width: "100%", minWidth: 0 }}
      />
    ) : null;

  if (layout === "row") {
    return (
      <Flex align="center" gap={8} wrap="wrap" className="model-picker model-picker-row">
        <div className="model-picker-control">{modelSelect}</div>
        {effortSelect ? <div className="model-picker-effort">{effortSelect}</div> : null}
      </Flex>
    );
  }

  return (
    <Flex vertical gap={8} className="model-picker">
      <Typography.Text>Model</Typography.Text>
      {modelSelect}
      {effortSelect ? (
        <>
          <Typography.Text>Reasoning</Typography.Text>
          {effortSelect}
        </>
      ) : null}
    </Flex>
  );
}
