import { useEffect, useState } from "react";
import { Button, Dropdown, Flex, Slider, Tag, Typography, theme } from "antd";
import { CheckOutlined, DownOutlined } from "@ant-design/icons";

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

export function formatEffortLabel(effort: string | null | undefined): string {
  if (!effort) {
    return "";
  }

  return effort.charAt(0).toUpperCase() + effort.slice(1);
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
  const { token } = theme.useToken();
  const [open, setOpen] = useState(false);
  const controlPadding = token.paddingXS;

  useEffect(() => {
    if (!open) {
      return;
    }

    function onPointerDown(event: PointerEvent): void {
      const target = event.target as HTMLElement | null;
      if (target?.closest(".model-picker-popover") || target?.closest(".model-picker-chip")) {
        return;
      }
      setOpen(false);
    }

    function onKeyDown(event: KeyboardEvent): void {
      if (event.key === "Escape") {
        setOpen(false);
      }
    }

    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  const selectValue =
    models.length === 0
      ? modelValue
      : modelValue === DEFAULT_MODEL_KEY
        ? defaultKey ?? DEFAULT_MODEL_KEY
        : modelValue;
  const selected = models.length === 0 ? null : selectedCatalogModel(models, selectValue, defaultKey);
  const efforts = selected?.supportedReasoningEfforts ?? [];
  const effortIndex = effortValue && efforts.length > 0 ? Math.max(0, efforts.indexOf(effortValue)) : 0;
  const [effortPreviewIndex, setEffortPreviewIndex] = useState(effortIndex);

  useEffect(() => {
    setEffortPreviewIndex(effortIndex);
  }, [effortIndex, open, efforts.length]);

  if (models.length === 0) {
    return null;
  }

  const showEffort = Boolean(selected?.reasoning && efforts.length > 0);
  const compact = layout === "row";
  const composer = variant === "borderless";

  function closePicker(): void {
    setOpen(false);
  }

  function openModels(): void {
    if (disabled) {
      return;
    }
    if (open) {
      closePicker();
      return;
    }
    setOpen(true);
  }

  const overlay = (
    <Flex vertical gap={token.paddingXS} className="model-picker-overlay">
      <Typography.Text type="secondary" className="model-picker-overlay-title" style={{ paddingInline: controlPadding }}>
        Select model
      </Typography.Text>
      <Flex vertical role="listbox" aria-label="Model catalog">
        {models.map((model) => {
          const isSelected = model.key === selectValue;
          return (
            <Button
              key={model.key}
              type="text"
              role="option"
              title={model.displayName}
              aria-selected={isSelected}
              className="model-picker-option"
              disabled={disabled}
              style={{
                width: "100%",
                height: "auto",
                paddingInline: controlPadding,
                paddingBlock: controlPadding
              }}
              onClick={() => {
                onModelChange(model.key);
                closePicker();
              }}
            >
              <Flex align="center" justify="space-between" gap={token.paddingXS} style={{ width: "100%", minWidth: 0 }}>
                <span className="model-picker-option-name">{model.displayName}</span>
                <Flex align="center" gap={token.paddingXS} className="model-picker-option-end">
                  {model.key === defaultKey ? (
                    <Tag variant="filled" style={{ marginInlineEnd: 0 }}>
                      Default
                    </Tag>
                  ) : null}
                  <span className="model-picker-option-mark" aria-hidden>
                    {isSelected ? <CheckOutlined /> : null}
                  </span>
                </Flex>
              </Flex>
            </Button>
          );
        })}
      </Flex>
      {showEffort ? (
        <Flex vertical gap={token.paddingXS} className="model-picker-reasoning" style={{ paddingInline: controlPadding }}>
          <Flex align="center" justify="space-between" gap={token.paddingXS}>
            <Typography.Text type="secondary">Reasoning</Typography.Text>
            <Typography.Text>{formatEffortLabel(efforts[effortPreviewIndex] ?? effortValue)}</Typography.Text>
          </Flex>
          <Slider
            min={0}
            max={Math.max(0, efforts.length - 1)}
            step={1}
            dots
            tooltip={{ open: false }}
            value={effortPreviewIndex}
            disabled={disabled}
            onChange={(value) => setEffortPreviewIndex(value)}
            onChangeComplete={(value) => onEffortChange(efforts[value] ?? null)}
            aria-label="Reasoning effort"
          />
        </Flex>
      ) : null}
    </Flex>
  );

  const trigger = (
    <Flex
      align="center"
      gap={token.paddingXS}
      className={`model-picker-chip${composer ? "" : " model-picker-chip-outlined"}`}
    >
      <Button
        type="text"
        size="small"
        aria-label="Model"
        aria-haspopup="listbox"
        aria-expanded={open}
        disabled={disabled}
        className="model-picker-model"
        style={{
          height: "auto",
          minHeight: token.controlHeight,
          paddingInline: controlPadding,
          paddingBlock: controlPadding
        }}
        onClick={(event) => {
          event.stopPropagation();
          openModels();
        }}
      >
        <Flex align="center" gap={token.paddingXS}>
          <span className="model-picker-name">{selected?.displayName ?? "Model"}</span>
          {selectValue === defaultKey ? (
            <Tag variant="filled" style={{ marginInlineEnd: 0 }}>
              Default
            </Tag>
          ) : null}
          {showEffort ? (
            <Typography.Text type="secondary" className="model-picker-effort" aria-label="Reasoning">
              {formatEffortLabel(effortValue)}
            </Typography.Text>
          ) : null}
          <DownOutlined aria-hidden />
        </Flex>
      </Button>
    </Flex>
  );

  return (
    <Flex
      align="center"
      className={`model-picker${compact ? " model-picker-row" : ""}${composer ? " model-picker-composer" : ""}`}
    >
      <Dropdown
        trigger={["click"]}
        arrow={false}
        placement={composer ? "topLeft" : "bottomLeft"}
        autoAdjustOverflow={false}
        open={open}
        onOpenChange={setOpen}
        getPopupContainer={() => document.body}
        classNames={{ root: "model-picker-popover" }}
        popupRender={() => overlay}
      >
        {trigger}
      </Dropdown>
    </Flex>
  );
}
