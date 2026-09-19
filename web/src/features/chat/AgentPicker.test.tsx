import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AgentPicker } from "./AgentPicker";

const agents = [
  {
    id: "customer-support",
    version: 1,
    name: "Sam",
    role: "Support",
    description: "Help.",
    voiceAvailable: false
  },
  {
    id: "compliance",
    version: 1,
    name: "Jordan",
    role: "Compliance",
    description: "Cite.",
    voiceAvailable: false
  }
];

function openIdentityOptions() {
  fireEvent.mouseDown(screen.getByRole("combobox", { name: "Identity" }));
}

describe("AgentPicker", () => {
  it("lists Support and Compliance identities for a new durable chat", () => {
    const onSelect = vi.fn();
    render(
      <AgentPicker
        agents={agents}
        selectedAgentId="customer-support"
        error={null}
        speechLocale=""
        onSelect={onSelect}
        onSpeechLocaleChange={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Speech locale" })).toBeInTheDocument();
    expect(screen.getByText("Identity")).toBeInTheDocument();
    expect(screen.getByText("Sam — Support")).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Model" })).not.toBeInTheDocument();

    openIdentityOptions();
    fireEvent.click(screen.getByTitle("Jordan — Compliance"));
    expect(onSelect).toHaveBeenCalledWith("compliance");
  });

  it("surfaces picker errors without a separate start control", () => {
    render(
      <AgentPicker
        agents={agents}
        selectedAgentId="customer-support"
        error="Unable to list agents."
        speechLocale=""
        onSelect={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
      />
    );

    expect(screen.getByRole("alert")).toHaveTextContent("Unable to list agents.");
    expect(screen.queryByRole("button", { name: "Start conversation" })).not.toBeInTheDocument();
  });

  it("disables identity selection when no agents are available", () => {
    render(
      <AgentPicker
        agents={[]}
        selectedAgentId=""
        error={null}
        speechLocale=""
        onSelect={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeDisabled();
  });
});
