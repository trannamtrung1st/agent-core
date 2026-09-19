import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import {
  SPEECH_TEXT_SECTION_LABEL,
  SPOKEN_SECTION_LABEL,
  shouldShowSpeechText,
  SpokenText
} from "./SpokenText";

describe("shouldShowSpeechText", () => {
  it("hides missing or blank speech text", () => {
    expect(shouldShowSpeechText("Hello", null)).toBe(false);
    expect(shouldShowSpeechText("Hello", undefined)).toBe(false);
    expect(shouldShowSpeechText("Hello", "")).toBe(false);
    expect(shouldShowSpeechText("Hello", "   \n")).toBe(false);
  });

  it("hides exact and whitespace-only differences", () => {
    expect(shouldShowSpeechText("Hello there.", "Hello there.")).toBe(false);
    expect(shouldShowSpeechText("Hello  there.\n", " Hello there. ")).toBe(false);
  });

  it("shows genuinely different wording", () => {
    expect(shouldShowSpeechText("Shown display.", "Hidden speech")).toBe(true);
  });
});

describe("SpokenText", () => {
  it("uses Spoken for voice delivery and Speech text for text delivery", () => {
    const { rerender } = render(<SpokenText speechText="The spoken line." deliveryMode="voice" />);
    expect(screen.getByLabelText(SPOKEN_SECTION_LABEL)).toBeInTheDocument();
    rerender(<SpokenText speechText="The spoken line." deliveryMode="text" />);
    expect(screen.getByLabelText(SPEECH_TEXT_SECTION_LABEL)).toBeInTheDocument();
    expect(screen.getByText("The spoken line.")).toBeInTheDocument();
  });
});
