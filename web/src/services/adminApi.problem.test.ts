import { describe, expect, it } from "vitest";
import { adminRequestErrorFromProblem, friendlyAdminDetail } from "./adminApi";

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

  it("reads diagnosticId as its own field and leaves 4xx copy unchanged", () => {
    const diagnosed = adminRequestErrorFromProblem(
      {
        detail: "forced session save failure",
        diagnosticId: "019944af-0008-7000-8000-0000000000d5",
        extensions: { stack: "secret-stack" }
      },
      "The request failed."
    );
    expect(diagnosed.message).toBe("forced session save failure");
    expect(diagnosed.diagnosticId).toBe("019944af-0008-7000-8000-0000000000d5");
    expect(diagnosed.message).not.toContain("secret-stack");

    const validation = adminRequestErrorFromProblem(
      {
        detail: "Voice.Enabled requires speechRecognizer and speechSynthesizer aliases.",
        extensions: { diagnosticId: "should-not-be-read" }
      },
      "The draft could not be saved."
    );
    expect(validation.message).toBe("Add a speech recognizer and a speech synthesizer before saving a voice-enabled draft.");
    expect(validation.diagnosticId).toBeUndefined();
  });

  it("uses the fallback when the server sends no detail", () => {
    expect(friendlyAdminDetail("  ", "The draft could not be saved. Check the form and try again."))
      .toBe("The draft could not be saved. Check the form and try again.");
  });
});
