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
        models={[]}
        defaultModelKey={null}
        modelValue="default"
        effortValue={null}
        onSelect={onSelect}
        onSpeechLocaleChange={vi.fn()}
        onModelChange={vi.fn()}
        onEffortChange={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Speech locale" })).toBeInTheDocument();
    expect(screen.getByText("Identity")).toBeInTheDocument();
    expect(screen.getByText("Sam — Support")).toBeInTheDocument();

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
        models={[]}
        defaultModelKey={null}
        modelValue="default"
        effortValue={null}
        onSelect={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
        onModelChange={vi.fn()}
        onEffortChange={vi.fn()}
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
        models={[]}
        defaultModelKey={null}
        modelValue="default"
        effortValue={null}
        onSelect={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
        onModelChange={vi.fn()}
        onEffortChange={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeDisabled();
  });

  it("shows reasoning only for models that support it", () => {
    const onModelChange = vi.fn();
    render(
      <AgentPicker
        agents={agents}
        selectedAgentId="customer-support"
        error={null}
        speechLocale=""
        models={[
          {
            key: "scripted-alpha",
            displayName: "Scripted Alpha",
            reasoning: true,
            supportedReasoningEfforts: ["low", "medium", "high"],
            defaultReasoningEffort: "medium"
          },
          {
            key: "scripted-beta",
            displayName: "Scripted Beta",
            reasoning: false,
            supportedReasoningEfforts: [],
            defaultReasoningEffort: null
          }
        ]}
        defaultModelKey="scripted-alpha"
        modelValue="default"
        effortValue="medium"
        onSelect={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
        onModelChange={onModelChange}
        onEffortChange={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Model" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Reasoning" })).toBeInTheDocument();
    fireEvent.mouseDown(screen.getByRole("combobox", { name: "Model" }));
    fireEvent.click(screen.getByTitle("Scripted Beta"));
    expect(onModelChange).toHaveBeenCalledWith("scripted-beta");
  });
});
