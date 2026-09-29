import { describe, expect, it } from "vitest";
import { friendlyAdminDetail } from "./adminApi";

describe("friendlyAdminDetail", () => {
  it("turns draft validation failures into operator language", () => {
    expect(friendlyAdminDetail(
      "Voice.Enabled requires speechRecognizer and speechSynthesizer aliases.",
      "The draft could not be saved."
    )).toBe("Add a speech recognizer and a speech synthesizer before saving a voice-enabled draft.");
    expect(friendlyAdminDetail(
      "Text-only definitions must omit speech provider aliases.",
      "The draft could not be saved."
    )).toBe("Remove the speech recognizer and speech synthesizer when voice is off.");
    expect(friendlyAdminDetail(
      "speechRecognizer alias 'primary-stt' is not configured.",
      "The draft could not be saved."
    )).toBe("The speech recognizer “primary-stt” is not available on this server. Choose one that is configured.");
    expect(friendlyAdminDetail(
      "Definition candidate body is invalid: The JSON value could not be converted to System.Int32.",
      "The draft could not be saved."
    )).toBe("A field in this draft could not be read. Check numbers and Advanced JSON, then try again.");
  });

  it("uses the fallback when the server sends no detail", () => {
    expect(friendlyAdminDetail("  ", "The draft could not be saved. Check the form and try again."))
      .toBe("The draft could not be saved. Check the form and try again.");
  });
});
