import { Flex, Select, Typography } from "antd";

export const SPEECH_LOCALE_OPTIONS = [
  { value: "", label: "Agent default" },
  { value: "en", label: "English (en)" },
  { value: "fr-FR", label: "French (fr-FR)" },
  { value: "vi-VN", label: "Vietnamese (vi-VN)" },
  { value: "ja-JP", label: "Japanese (ja-JP)" }
] as const;

export function speechLocaleSelectValue(source: string | null, override: string | null, pending: string | null, inSession: boolean): string {
  if (!inSession) {
    return pending ?? "";
  }

  return source === "sessionOverride" ? override ?? "" : "";
}

export function SpeechLocalePicker({
  value,
  disabled,
  layout = "stack",
  onChange
}: {
  value: string;
  disabled?: boolean;
  layout?: "stack" | "row";
  onChange: (locale: string | null) => void;
}) {
  const options = SPEECH_LOCALE_OPTIONS.some((option) => option.value === value)
    ? [...SPEECH_LOCALE_OPTIONS]
    : [{ value, label: value }, ...SPEECH_LOCALE_OPTIONS];

  const select = (
    <Select
      aria-label="Speech locale"
      value={value}
      disabled={disabled}
      getPopupContainer={() => document.body}
      options={options.map((option) => ({ value: option.value, label: option.label }))}
      onChange={(next) => {
        const tag = typeof next === "string" ? next : "";
        onChange(tag.length === 0 ? null : tag);
      }}
      style={{ width: layout === "stack" ? "100%" : 220, minWidth: 0 }}
    />
  );

  if (layout === "row") {
    return (
      <Flex align="center" gap={8} wrap="wrap" className="speech-locale-picker">
        <Typography.Text type="secondary">Speech locale</Typography.Text>
        {select}
      </Flex>
    );
  }

  return (
    <Flex vertical gap={8} className="speech-locale-picker">
      <Typography.Text>Speech locale</Typography.Text>
      {select}
    </Flex>
  );
}
