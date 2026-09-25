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
        managedInstances={[]}
        identityKey="legacy:customer-support"
        error={null}
        speechLocale=""
        managedInstancesError={null}
        managedInstancesLoading={false}
        onIdentityChange={onSelect}
        onSpeechLocaleChange={vi.fn()}
        onRetryManagedInstances={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Speech locale" })).toBeInTheDocument();
    expect(screen.getByText("Identity")).toBeInTheDocument();
    expect(screen.getByText("Sam — Support (legacy)")).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Model" })).not.toBeInTheDocument();

    openIdentityOptions();
    fireEvent.click(screen.getByTitle("Jordan — Compliance (legacy)"));
    expect(onSelect).toHaveBeenCalledWith("legacy:compliance");
  });

  it("surfaces picker errors without a separate start control", () => {
    render(
      <AgentPicker
        agents={agents}
        managedInstances={[]}
        identityKey="legacy:customer-support"
        error="Unable to list agents."
        speechLocale=""
        managedInstancesError={null}
        managedInstancesLoading={false}
        onIdentityChange={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
        onRetryManagedInstances={vi.fn()}
      />
    );

    expect(screen.getByRole("alert")).toHaveTextContent("Unable to list agents.");
    expect(screen.queryByRole("button", { name: "Start conversation" })).not.toBeInTheDocument();
  });

  it("lists managed instances ahead of legacy agents", () => {
    const onIdentityChange = vi.fn();
    render(
      <AgentPicker
        agents={agents}
        managedInstances={[
          {
            instanceId: "019944af-00d1-7000-8000-000000000099",
            definitionId: "examiner",
            activeVersion: 2,
            name: "Pinned",
            role: "Coach",
            voiceAvailable: true,
            language: "en"
          }
        ]}
        identityKey="managed:019944af-00d1-7000-8000-000000000099"
        error={null}
        managedInstancesError={null}
        managedInstancesLoading={false}
        speechLocale=""
        onIdentityChange={onIdentityChange}
        onSpeechLocaleChange={vi.fn()}
        onRetryManagedInstances={vi.fn()}
      />
    );

    openIdentityOptions();
    fireEvent.click(screen.getByTitle("Jordan — Compliance (legacy)"));
    expect(onIdentityChange).toHaveBeenCalledWith("legacy:compliance");
  });

  it("shows loading state while managed inventory is unresolved", () => {
    render(
      <AgentPicker
        agents={agents}
        managedInstances={[]}
        identityKey=""
        error={null}
        managedInstancesError={null}
        managedInstancesLoading={true}
        speechLocale=""
        onIdentityChange={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
        onRetryManagedInstances={vi.fn()}
      />
    );

    expect(screen.getByLabelText("Loading managed instances")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Identity" })).toBeDisabled();
  });

  it("surfaces managed inventory failures with retry", () => {
    const onRetry = vi.fn();
    render(
      <AgentPicker
        agents={agents}
        managedInstances={[]}
        identityKey="legacy:customer-support"
        error={null}
        managedInstancesError="Managed instances unavailable."
        managedInstancesLoading={false}
        speechLocale=""
        onIdentityChange={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
        onRetryManagedInstances={onRetry}
      />
    );

    expect(screen.getByText("Managed instances could not be loaded.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry managed instances" }));
    expect(onRetry).toHaveBeenCalled();
  });

  it("disables identity selection when no agents are available", () => {
    render(
      <AgentPicker
        agents={[]}
        managedInstances={[]}
        identityKey=""
        error={null}
        speechLocale=""
        managedInstancesError={null}
        managedInstancesLoading={false}
        onIdentityChange={vi.fn()}
        onSpeechLocaleChange={vi.fn()}
        onRetryManagedInstances={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeDisabled();
  });
});
