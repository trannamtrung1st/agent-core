import { Flex, Typography } from "antd";
import { SoundOutlined } from "@ant-design/icons";
import type { HistoryEntry } from "../../state/sessionStore";

export const SPOKEN_SECTION_LABEL = "Spoken";
export const SPEECH_TEXT_SECTION_LABEL = "Speech text";

export function speechSectionLabel(deliveryMode: HistoryEntry["deliveryMode"] | undefined): string {
  return deliveryMode === "voice" ? SPOKEN_SECTION_LABEL : SPEECH_TEXT_SECTION_LABEL;
}

export function shouldShowSpeechText(
  displayText: string | null | undefined,
  speechText: string | null | undefined
): boolean {
  if (speechText == null) {
    return false;
  }

  const speech = normalizeComparableText(speechText);
  if (speech.length === 0) {
    return false;
  }

  return speech !== normalizeComparableText(displayText ?? "");
}

export function normalizeComparableText(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

export function SpokenText({
  speechText,
  deliveryMode
}: {
  speechText: string;
  deliveryMode?: HistoryEntry["deliveryMode"];
}) {
  const sectionLabel = speechSectionLabel(deliveryMode);
  return (
    <section className="spoken-text" aria-label={sectionLabel}>
      <Flex align="center" gap={8} className="spoken-text-label">
        <SoundOutlined aria-hidden className="spoken-text-icon" />
        <Typography.Text type="secondary" className="spoken-text-caption">
          {sectionLabel}
        </Typography.Text>
      </Flex>
      <Typography.Paragraph type="secondary" className="spoken-text-body">
        {speechText}
      </Typography.Paragraph>
    </section>
  );
}
