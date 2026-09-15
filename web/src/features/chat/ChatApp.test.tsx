import { act, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { emptySession } from "../../state/sessionStore";
import { useChatStore } from "../../state/chatStore";
import { ChatApp } from "./ChatApp";

describe("ChatApp accessibility", () => {
  afterEach(() => {
    useChatStore.setState({
      ...emptySession(),
      agents: [],
      selectedAgentId: "examiner"
    });
  });

  it("labels identity and surfaces connection and microphone errors", () => {
    useChatStore.setState({
      ...emptySession(),
      agents: [{ id: "examiner", version: 1, name: "Alex", role: "Examiner", description: "", voiceAvailable: true }],
      selectedAgentId: "examiner"
    });
    const { rerender } = render(<ChatApp />);
    expect(screen.getByLabelText("Identity")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Start conversation" })).toBeInTheDocument();

    act(() => {
      useChatStore.setState({
        ...emptySession(),
        sessionId: "s1",
        connection: "failed",
        agentName: "Alex",
        agentRole: "Examiner",
        voiceAvailable: true,
        error: "Microphone permission was denied. Enable the microphone or continue in text.",
        agents: [],
        selectedAgentId: "examiner"
      });
    });
    rerender(<ChatApp />);
    expect(screen.getByTestId("connection")).toHaveTextContent("Connection failed");
    expect(screen.getByRole("alert")).toHaveTextContent("Microphone permission was denied");
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Voice" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "End" })).toBeInTheDocument();
  });

  it("shows Voice after reconnect when durable mode is voice until capture is live", () => {
    useChatStore.setState({
      ...emptySession(),
      sessionId: "s1",
      connection: "ready",
      mode: "voice",
      captureLive: false,
      voiceAvailable: true,
      agentName: "Alex",
      agentRole: "Examiner",
      agents: [],
      selectedAgentId: "examiner"
    });
    const { rerender } = render(<ChatApp />);
    expect(screen.getByTestId("connection")).toHaveTextContent("Ready");
    expect(screen.getByRole("button", { name: "Voice" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Mute" })).not.toBeInTheDocument();

    act(() => {
      useChatStore.setState({ captureLive: true, muted: false });
    });
    rerender(<ChatApp />);
    expect(screen.getByTestId("connection")).toHaveTextContent("Listening");
    expect(screen.getByRole("button", { name: "Mute" })).toBeInTheDocument();
  });
});
