import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { AgentPicker } from "./AgentPicker";

const instances = [
  {
    instanceId: "019944af-00d1-7000-8000-000000000091",
    definitionId: "customer-support",
    activeVersion: 3,
    name: "Sam",
    role: "Support",
    description: "Help.",
    language: "en",
    voiceAvailable: false
  },
  {
    instanceId: "019944af-00d1-7000-8000-000000000092",
    definitionId: "compliance",
    activeVersion: 2,
    name: "Jordan",
    role: "Compliance",
    description: "Cite.",
    language: "en",
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
        managedInstances={instances}
        identityKey="managed:019944af-00d1-7000-8000-000000000091"
        error={null}

        managedInstancesError={null}
        managedInstancesLoading={false}
        onIdentityChange={onSelect}

        onRetryManagedInstances={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Speech locale" })).not.toBeInTheDocument();
    expect(screen.getByText("Identity")).toBeInTheDocument();
    expect(screen.getByText("Sam — Support · v3 · 00000091")).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Model" })).not.toBeInTheDocument();

    openIdentityOptions();
    fireEvent.click(screen.getByTitle("Jordan — Compliance · v2 · 00000092"));
    expect(onSelect).toHaveBeenCalledWith("managed:019944af-00d1-7000-8000-000000000092");
  });

  it("surfaces picker errors without a separate start control", () => {
    render(
      <AgentPicker
        managedInstances={instances}
        identityKey="managed:019944af-00d1-7000-8000-000000000091"
        error="Unable to list agents."

        managedInstancesError={null}
        managedInstancesLoading={false}
        onIdentityChange={vi.fn()}

        onRetryManagedInstances={vi.fn()}
      />
    );

    expect(screen.getByRole("alert")).toHaveTextContent("Unable to list agents.");
    expect(screen.queryByRole("button", { name: "Start conversation" })).not.toBeInTheDocument();
  });

  it("lists only managed instance identities", () => {
    const onIdentityChange = vi.fn();
    render(
      <AgentPicker
        managedInstances={instances}
        identityKey="managed:019944af-00d1-7000-8000-000000000099"
        error={null}
        managedInstancesError={null}
        managedInstancesLoading={false}

        onIdentityChange={onIdentityChange}

        onRetryManagedInstances={vi.fn()}
      />
    );

    openIdentityOptions();
    fireEvent.click(screen.getByTitle("Jordan — Compliance · v2 · 00000092"));
    expect(onIdentityChange).toHaveBeenCalledWith("managed:019944af-00d1-7000-8000-000000000092");
  });

  it("shows loading state while managed inventory is unresolved", () => {
    render(
      <AgentPicker
        managedInstances={[]}
        identityKey=""
        error={null}
        managedInstancesError={null}
        managedInstancesLoading={true}

        onIdentityChange={vi.fn()}

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
        managedInstances={[]}
        identityKey="managed:019944af-00d1-7000-8000-000000000091"
        error={null}
        managedInstancesError="Managed instances unavailable."
        managedInstancesLoading={false}

        onIdentityChange={vi.fn()}

        onRetryManagedInstances={onRetry}
      />
    );

    expect(screen.getByText("Agents could not be loaded.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry managed instances" }));
    expect(onRetry).toHaveBeenCalled();
  });

  it("explains empty tenant inventory and disables selection", () => {
    render(
      <AgentPicker
        managedInstances={[]}
        identityKey=""
        error={null}

        managedInstancesError={null}
        managedInstancesLoading={false}
        onIdentityChange={vi.fn()}

        onRetryManagedInstances={vi.fn()}
      />
    );

    expect(screen.getByRole("combobox", { name: "Identity" })).toBeDisabled();
    expect(screen.getByText("No matching agents. Try another search or reload the list.")).toBeInTheDocument();
  });
});

it('explains an unavailable model without inventing a provider or credential', () => {
  const item = { ...instances[0], name: 'General Assistant', systemManaged: true, setupState: 'no_model_configured' };
  render(<AgentPicker managedInstances={[item]} identityKey={`managed:${item.instanceId}`} error={null} managedInstancesError={null} managedInstancesLoading={false} onIdentityChange={vi.fn()} onRetryManagedInstances={vi.fn()} />);
  expect(screen.getByText('No model configured')).toBeVisible();
  expect(screen.getByText(/configure an available model before starting Chat/)).toBeVisible();
});
