import { reasoningEffortChoices } from "../models/reasoningEfforts";
import { modelChoiceLabel } from "../models/modelLabels";
import type { ComponentType } from "react";
import { useEffect, useState } from "react";
import { Button, Dropdown, Flex, Select, Slider, Tag, Tooltip, Typography, theme } from "antd";
import {
  BulbOutlined,
  CheckOutlined,
  CodeOutlined,
  DownOutlined,
  EyeOutlined,
  ToolOutlined
} from "@ant-design/icons";

export const INHERIT_MODEL = "default";

export type ModelCatalogItem = {
  offeringId: string;
  displayName: string;
  tools: boolean;
  vision: boolean;
  structuredOutput: boolean;
  reasoning: boolean;
  supportedReasoningEfforts: readonly string[];
  defaultReasoningEffort?: string | null;
  connectionLabel?: string | null;
};

type CapabilityIcon = ComponentType<{ "aria-hidden"?: boolean }>;

const MODEL_CAPABILITIES: readonly {
  label: string;
  isEnabled: (model: ModelCatalogItem) => boolean;
  Icon: CapabilityIcon;
}[] = [
  { label: "Vision", isEnabled: (model) => model.vision, Icon: EyeOutlined },
  { label: "Reasoning capable", isEnabled: (model) => model.reasoning, Icon: BulbOutlined },
  { label: "Tools", isEnabled: (model) => model.tools, Icon: ToolOutlined },
  {
    label: "Structured output",
    isEnabled: (model) => model.structuredOutput,
    Icon: CodeOutlined
  }
];

function ModelOptionCapabilityIcon({ label, Icon }: { label: string; Icon: CapabilityIcon }) {
  return (
    <Tooltip title={label}>
      <span className="model-picker-option-capability" role="img" aria-label={label}>
        <Icon aria-hidden />
      </span>
    </Tooltip>
  );
}

function ModelOptionCapabilities({ model }: { model: ModelCatalogItem }) {
  const { token } = theme.useToken();
  const enabled = MODEL_CAPABILITIES.filter((capability) => capability.isEnabled(model));
  if (enabled.length === 0) {
    return null;
  }

  return (
    <Flex align="center" gap={token.marginXXS} className="model-picker-option-caps">
      {enabled.map((capability) => (
        <ModelOptionCapabilityIcon key={capability.label} label={capability.label} Icon={capability.Icon} />
      ))}
    </Flex>
  );
}

export function modelSelectValue(
  sessionKey: string | null,
  pendingKey: string | null,
  inSession: boolean,
  defaultOfferingId: string | null = null
): string {
  const pending = !pendingKey || pendingKey === INHERIT_MODEL ? null : pendingKey;
  if (!inSession) {
    return pending ?? INHERIT_MODEL;
  }

  return sessionKey && sessionKey.length > 0 ? sessionKey : pending ?? defaultOfferingId ?? INHERIT_MODEL;
}

export function selectedCatalogModel(
  models: readonly ModelCatalogItem[],
  value: string,
  defaultOfferingId: string | null
): ModelCatalogItem | null {
  const key = value === INHERIT_MODEL ? defaultOfferingId : value;
  return models.find((model) => model.offeringId === key) ?? null;
}

export function nextEffortForModel(
  models: readonly ModelCatalogItem[],
  key: string,
  defaultOfferingId: string | null,
  currentEffort: string | null
): string | null {
  const model = selectedCatalogModel(models, key, defaultOfferingId);
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
  inSession: boolean,
  inheritedEffort: string | null = null
): string | null {
  if (!model?.reasoning) {
    return null;
  }

  const current = inSession ? sessionEffort ?? pendingEffort : pendingEffort;
  if (current && model.supportedReasoningEfforts.includes(current)) {
    return current;
  }

  return inheritedEffort && model.supportedReasoningEfforts.includes(inheritedEffort) ? inheritedEffort : model.defaultReasoningEffort ?? null;
}

export function formatEffortLabel(effort: string | null | undefined): string {
  if (!effort) {
    return "";
  }

  return effort.charAt(0).toUpperCase() + effort.slice(1);
}

export function ModelPicker({
  models,
  defaultOfferingId,
  defaultSource = "tenantDefault",
  modelValue,
  inheritSelected = modelValue === INHERIT_MODEL,
  unavailableLabel,
  effortValue,
  disabled,
  layout = "stack",
  onModelChange,
  onEffortChange
}: {
  models: readonly ModelCatalogItem[];
  defaultOfferingId: string | null;
  defaultSource?: "tenantDefault" | "agentDefault";
  modelValue: string;
  inheritSelected?: boolean;
  unavailableLabel?: string | null;
  effortValue: string | null;
  disabled?: boolean;
  layout?: "stack" | "row";
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
      if (target?.closest(".model-picker-popover, .model-picker-effort-popup") || target?.closest(".model-picker-chip")) {
        return;
      }
      setOpen(false);
    }

    function onKeyDown(event: KeyboardEvent): void {
      if (event.key === "Escape") {
        setOpen(false);
      }
    }

    function onFocus(event: FocusEvent): void {
      const target = event.target as HTMLElement | null;
      if (!target?.closest(".model-picker-popover, .model-picker-chip, .model-picker-effort-popup")) setOpen(false);
    }
    function onBlur(): void { setOpen(false); }
    document.addEventListener("focusin", onFocus);
    window.addEventListener("blur", onBlur);
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("focusin", onFocus);
      window.removeEventListener("blur", onBlur);
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  const selectValue =
    models.length === 0
      ? modelValue
      : modelValue === INHERIT_MODEL
        ? defaultOfferingId ?? INHERIT_MODEL
        : modelValue;
  const selected = models.length === 0 ? null : selectedCatalogModel(models, selectValue, defaultOfferingId);
  const { levels: efforts, modes } = reasoningEffortChoices(selected?.supportedReasoningEfforts ?? []);
  const effortIndex = efforts.indexOf(effortValue ?? selected?.defaultReasoningEffort ?? "");
  const [effortPreviewIndex, setEffortPreviewIndex] = useState(effortIndex);

  useEffect(() => {
    setEffortPreviewIndex(effortIndex);
  }, [effortIndex, open, efforts.length]);

  if (models.length === 0) {
    return null;
  }

  const showEffort = Boolean(selected?.reasoning && (efforts.length > 0 || modes.length > 0));
  const compact = layout === "row";

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
        <Button type="text" role="option" title={defaultSource === "agentDefault" ? "Use Agent default" : "Use organization default"}
          aria-selected={inheritSelected} className="model-picker-option" disabled={disabled || !defaultOfferingId || !models.some(model => model.offeringId === defaultOfferingId)}
          style={{ width: "100%", height: "auto", paddingInline: controlPadding, paddingBlock: controlPadding }}
          onClick={() => { onModelChange(INHERIT_MODEL); closePicker(); }}>
          <Flex align="center" justify="space-between" gap={token.paddingXS} style={{ width: "100%", minWidth: 0 }}>
            <span className="model-picker-option-name">{defaultSource === "agentDefault" ? "Use Agent default" : "Use organization default"}</span>
            <span className="model-picker-option-mark" aria-hidden>{inheritSelected ? <CheckOutlined /> : null}</span>
          </Flex>
        </Button>
        {models.map((model) => {
          const isSelected = !inheritSelected && model.offeringId === selectValue;
          return (
            <Button
              key={model.offeringId}
              type="text"
              role="option"
              title={modelChoiceLabel(model)}
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
                onModelChange(model.offeringId);
                closePicker();
              }}
            >
              <Flex align="center" justify="space-between" gap={token.paddingXS} style={{ width: "100%", minWidth: 0 }}>
                <span className="model-picker-option-name">{modelChoiceLabel(model)}</span>
                <Flex align="center" gap={token.paddingXS} className="model-picker-option-end">
                  <ModelOptionCapabilities model={model} />
                  {model.offeringId === defaultOfferingId ? (
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
          {efforts.length > 0 && !modes.includes(effortValue ?? "") ? <Slider
            min={0}
            max={Math.max(0, efforts.length - 1)}
            step={1}
            dots
            tooltip={{ open: false }}
            value={Math.max(0, effortPreviewIndex)}
            disabled={disabled || efforts.length === 1}
            ariaValueTextFormatterForHandle={(value) => formatEffortLabel(efforts[value ?? 0])}
            onChange={(value) => setEffortPreviewIndex(value)}
            onChangeComplete={(value) => onEffortChange(efforts[value] ?? null)}
            aria-label="Reasoning effort"
          /> : null}
          {modes.length > 0 ? <Select
            classNames={{ popup: { root: "model-picker-effort-popup" } }}
            aria-label="Reasoning mode"
            value={effortValue}
            disabled={disabled}
            options={[...efforts, ...modes].map(value => ({ value, label: formatEffortLabel(value) }))}
            onChange={onEffortChange}
          /> : null}
        </Flex>
      ) : null}
    </Flex>
  );

  const trigger = (
    <Flex
      align="center"
      gap={token.paddingXS}
      className="model-picker-chip"
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
        <Flex align="center" gap={token.paddingXS} style={{ minWidth: 0, flex: "1 1 auto" }}>
          <span className="model-picker-name">{selected ? modelChoiceLabel(selected) : `${unavailableLabel ?? (selectValue === INHERIT_MODEL ? defaultSource === "agentDefault" ? "Agent default" : "Organization default" : selectValue)} · Unavailable`}</span>
          {inheritSelected ? (
            <Tag variant="filled" style={{ marginInlineEnd: 0 }}>
              Default
            </Tag>
          ) : null}
          {showEffort ? (
            <Typography.Text type="secondary" className="model-picker-effort" aria-label="Reasoning effort">
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
      className={`model-picker model-picker-composer${compact ? " model-picker-row" : ""}`}
    >
      <Dropdown
        trigger={["click"]}
        arrow={false}
        placement="topLeft"
        align={{ overflow: { adjustX: true, adjustY: true, shiftX: true } }}
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
