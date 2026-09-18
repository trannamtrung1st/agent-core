import { speechErrorFromRecognitionError } from "./errors";

describe("speechErrorFromRecognitionError", () => {
  it("maps permission and device errors without recognition detail", () => {
    expect(speechErrorFromRecognitionError("not-allowed").code).toBe("SpeechPermissionDenied");
    expect(speechErrorFromRecognitionError("audio-capture").code).toBe("SpeechDeviceUnavailable");
    expect(speechErrorFromRecognitionError("not-allowed").extensions).toBeUndefined();
  });

  it("preserves native recognition errors in extensions", () => {
    const network = speechErrorFromRecognitionError("network");
    expect(network.code).toBe("SpeechRecognitionUnavailable");
    expect(network.extensions).toEqual({ recognitionError: "network" });

    const denied = speechErrorFromRecognitionError("service-not-allowed");
    expect(denied.extensions).toEqual({ recognitionError: "service-not-allowed" });
  });

  it("sanitizes unexpected native error tokens", () => {
    expect(speechErrorFromRecognitionError("bad token").extensions).toEqual({ recognitionError: "unrecognized" });
    expect(speechErrorFromRecognitionError(undefined).extensions).toEqual({ recognitionError: "unknown" });
  });
});
