import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { SPEECH_TEXT_SECTION_LABEL, shouldShowSpeechText, SpokenText } from "./SpokenText";

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
  it("exposes the Speech text label as text", () => {
    render(<SpokenText speechText="The spoken line." />);
    expect(screen.getByLabelText(SPEECH_TEXT_SECTION_LABEL)).toBeInTheDocument();
    expect(screen.getByText(SPEECH_TEXT_SECTION_LABEL)).toBeInTheDocument();
    expect(screen.getByText("The spoken line.")).toBeInTheDocument();
  });
});
