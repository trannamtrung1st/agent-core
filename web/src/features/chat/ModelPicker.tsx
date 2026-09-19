import { Flex, Select, Tag, Typography } from "antd";
import type { ReactNode } from "react";

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
  inSession: boolean,
  defaultKey: string | null = null
): string {
  const pending = !pendingKey || pendingKey === DEFAULT_MODEL_KEY ? null : pendingKey;
  if (!inSession) {
    return pending ?? defaultKey ?? DEFAULT_MODEL_KEY;
  }

  return sessionKey && sessionKey.length > 0 ? sessionKey : pending ?? defaultKey ?? DEFAULT_MODEL_KEY;
}

export function selectedCatalogModel(
  models: readonly ModelCatalogItem[],
  value: string,
  defaultKey: string | null
): ModelCatalogItem | null {
  const key = value === DEFAULT_MODEL_KEY ? defaultKey : value;
  return models.find((model) => model.key === key) ?? null;
}

export function wireModelSelectionKey(
  catalogKey: string,
  defaultKey: string | null,
  inSession: boolean
): string {
  if (!inSession && defaultKey && catalogKey === defaultKey) {
    return DEFAULT_MODEL_KEY;
  }

  return catalogKey;
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

function modelLabel(displayName: string, isDefault: boolean): ReactNode {
  if (!isDefault) {
    return displayName;
  }

  return (
    <Flex align="center" gap={8} justify="space-between">
      <span>{displayName}</span>
      <Tag variant="filled" style={{ marginInlineEnd: 0 }}>
        Default
      </Tag>
    </Flex>
  );
}

export function ModelPicker({
  models,
  defaultKey,
  modelValue,
  effortValue,
  disabled,
  layout = "stack",
  variant = "outlined",
  onModelChange,
  onEffortChange
}: {
  models: readonly ModelCatalogItem[];
  defaultKey: string | null;
  modelValue: string;
  effortValue: string | null;
  disabled?: boolean;
  layout?: "stack" | "row";
  variant?: "outlined" | "borderless";
  onModelChange: (key: string) => void;
  onEffortChange: (effort: string | null) => void;
}) {
  if (models.length === 0) {
    return null;
  }

  const selectValue = modelValue === DEFAULT_MODEL_KEY ? defaultKey ?? DEFAULT_MODEL_KEY : modelValue;
  const selected = selectedCatalogModel(models, selectValue, defaultKey);
  const modelOptions = models.map((model) => ({
    value: model.key,
    title: model.displayName,
    label: modelLabel(model.displayName, model.key === defaultKey)
  }));
  const effortOptions = (selected?.supportedReasoningEfforts ?? []).map((effort) => ({
    value: effort,
    label: effort.charAt(0).toUpperCase() + effort.slice(1)
  }));

  const compact = layout === "row";
  const modelSelect = (
    <Select
      aria-label="Model"
      size={compact ? "small" : "middle"}
      variant={variant}
      value={selectValue}
      disabled={disabled}
      getPopupContainer={() => document.body}
      options={modelOptions}
      onChange={(next) => onModelChange(typeof next === "string" ? next : defaultKey ?? DEFAULT_MODEL_KEY)}
      popupMatchSelectWidth={false}
      style={{ width: "100%", minWidth: 0 }}
    />
  );

  const effortSelect =
    selected?.reasoning && effortOptions.length > 0 ? (
      <Select
        aria-label="Reasoning"
        size={compact ? "small" : "middle"}
        variant={variant}
        value={effortValue ?? undefined}
        disabled={disabled}
        getPopupContainer={() => document.body}
        options={effortOptions}
        onChange={(next) => onEffortChange(typeof next === "string" ? next : null)}
        popupMatchSelectWidth={false}
        style={{ width: "100%", minWidth: 0 }}
      />
    ) : null;

  if (compact) {
    return (
      <Flex
        align="center"
        gap={8}
        wrap="wrap"
        className={`model-picker model-picker-row${variant === "borderless" ? " model-picker-composer" : ""}`}
      >
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
