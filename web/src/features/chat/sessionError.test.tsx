import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { SessionFailureAlert } from "./SessionFailureAlert";
import {
  classLabel,
  sanitizeExtensions,
  sessionErrorClass,
  sessionErrorFromWire,
  SESSION_ERROR_CLASSES
} from "./sessionError";

describe("sessionError", () => {
  it("maps backend categories onto distinguishable classes", () => {
    expect(sessionErrorClass("Validation", "ValidationError")).toBe("validation/protocol");
    expect(sessionErrorClass("Protocol", "ProtocolError")).toBe("validation/protocol");
    expect(sessionErrorClass("Provider", "ModelError")).toBe("provider/model");
    expect(sessionErrorClass("Speech", "RecognitionFailed")).toBe("speech/capture/playback");
    expect(sessionErrorClass("Session", "SessionPersistenceUnavailable")).toBe("persistence");
    expect(sessionErrorClass("Transport", "Backpressure")).toBe("transport/reconnect");
    expect(sessionErrorClass("Policy", "ToolDenied")).toBe("tool/policy denial");
    expect(sessionErrorClass("Sandbox", "SandboxFailed")).toBe("sandbox");
    expect(sessionErrorClass("Resource", "OutputLimit")).toBe("resource limit");
    expect(SESSION_ERROR_CLASSES.slice(0, 8)).toEqual([
      "validation/protocol",
      "provider/model",
      "speech/capture/playback",
      "persistence",
      "transport/reconnect",
      "tool/policy denial",
      "sandbox",
      "resource limit"
    ]);
  });

  it("drops secrets, stacks, and vendor bodies from extensions", () => {
    expect(
      sanitizeExtensions({
        retryable: true,
        apiKey: "sk-secret",
        stack: "at foo",
        vendorBody: "{\"choices\":[]}",
        hint: "safe"
      })
    ).toEqual({ retryable: true, hint: "safe" });
  });
});

describe("SessionFailureAlert", () => {
  it("shows recoverable structured details without leaking vendor payloads", async () => {
    render(
      <SessionFailureAlert
        error={sessionErrorFromWire(
          {
            category: "Validation",
            code: "ValidationError",
            message: "Text exceeds 8000 UTF-16 code units.",
            fatal: false,
            retryAfterMs: 0,
            extensions: { apiKey: "sk-live", vendorBody: "OPENAI_ERROR", hint: "shorten" }
          },
          "failed"
        )}
      />
    );
    const alert = screen.getByTestId("session-failure");
    expect(alert).toHaveAttribute("data-error-class", "validation/protocol");
    expect(alert).toHaveAttribute("data-error-code", "ValidationError");
    expect(alert).toHaveAttribute("data-error-fatal", "false");
    expect(screen.getByText("Text exceeds 8000 UTF-16 code units.")).toBeInTheDocument();
    expect(screen.getByText(/Validation or protocol/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Failure details" }));
    const details = await screen.findByTestId("session-failure-details");
    expect(details).toHaveTextContent("Category");
    expect(details).toHaveTextContent("ValidationError");
    expect(details).toHaveTextContent("Recoverable");
    expect(screen.getByText("shorten")).toBeInTheDocument();
    expect(screen.queryByText("sk-live")).not.toBeInTheDocument();
    expect(screen.queryByText("OPENAI_ERROR")).not.toBeInTheDocument();
  });

  it("marks fatal protocol failures as not retryable", async () => {
    render(
      <SessionFailureAlert
        error={sessionErrorFromWire(
          {
            category: "Protocol",
            code: "ProtocolError",
            message: "Unsupported protocol version.",
            fatal: true
          },
          "failed"
        )}
      />
    );
    expect(screen.getByTestId("session-failure")).toHaveAttribute("data-error-fatal", "true");
    fireEvent.click(screen.getByRole("button", { name: "Failure details" }));
    const details = await screen.findByTestId("session-failure-details");
    expect(details).toHaveTextContent("Fatal");
    expect(details).toHaveTextContent("Not retryable from this alert.");
    expect(classLabel("validation/protocol")).toBe("Validation or protocol");
  });
});
