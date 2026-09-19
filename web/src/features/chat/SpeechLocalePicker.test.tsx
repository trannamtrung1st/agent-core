import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { SPEECH_LOCALE_FIELD_LABEL, SpeechLocalePicker } from "./SpeechLocalePicker";

describe("SpeechLocalePicker", () => {
  it("shows a visible field label in the header row layout", () => {
    render(<SpeechLocalePicker layout="row" value="" onChange={vi.fn()} />);

    expect(screen.getByText(SPEECH_LOCALE_FIELD_LABEL)).toBeVisible();
    expect(screen.getByRole("combobox", { name: SPEECH_LOCALE_FIELD_LABEL })).toBeInTheDocument();
  });

  it("keeps the stacked label on the new-chat intro", () => {
    render(<SpeechLocalePicker value="" onChange={vi.fn()} />);

    expect(screen.getByText(SPEECH_LOCALE_FIELD_LABEL)).toBeVisible();
    expect(screen.getByRole("combobox", { name: SPEECH_LOCALE_FIELD_LABEL })).toBeInTheDocument();
  });
});
