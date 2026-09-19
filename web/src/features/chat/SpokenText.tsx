import { Flex, Typography } from "antd";
import { SoundOutlined } from "@ant-design/icons";

export const SPOKEN_SECTION_LABEL = "Spoken";

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

export function SpokenText({ speechText }: { speechText: string }) {
  return (
    <section className="spoken-text" aria-label={SPOKEN_SECTION_LABEL}>
      <Flex align="center" gap={8} className="spoken-text-label">
        <SoundOutlined aria-hidden className="spoken-text-icon" />
        <Typography.Text type="secondary" className="spoken-text-caption">
          {SPOKEN_SECTION_LABEL}
        </Typography.Text>
      </Flex>
      <Typography.Paragraph type="secondary" className="spoken-text-body">
        {speechText}
      </Typography.Paragraph>
    </section>
  );
}
