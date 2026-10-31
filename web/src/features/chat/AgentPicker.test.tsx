import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { AgentPicker } from "./AgentPicker";
import { retryTenantGeneralAssistantSetup, tenantChatCanManageSetup, tenantChatEmptyReason, tenantChatSetupPending } from '../../services/tenantChat';

vi.mock('../../services/tenantChat', async importOriginal => ({
  ...await importOriginal<typeof import('../../services/tenantChat')>(),
  chatTenantId: '11111111-1111-1111-1111-111111111111', tenantChatCanManageSetup: vi.fn(() => false),
  tenantChatEmptyReason: vi.fn(() => null), tenantChatSetupPending: vi.fn(() => false), retryTenantGeneralAssistantSetup: vi.fn()
}));
beforeEach(() => {
  vi.mocked(tenantChatCanManageSetup).mockReturnValue(false); vi.mocked(tenantChatEmptyReason).mockReturnValue(null);
  vi.mocked(tenantChatSetupPending).mockReturnValue(false);
  vi.mocked(retryTenantGeneralAssistantSetup).mockReset();
});

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
  fireEvent.mouseDown(screen.getByRole("combobox", { name: "Agent" }));
}

describe("AgentPicker", () => {
  it("lists Support and Compliance identities for a new durable chat", async () => {
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

    expect(screen.getByRole("combobox", { name: "Agent" })).toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Speech locale" })).not.toBeInTheDocument();
    expect(screen.getByText("Agent")).toBeInTheDocument();
    expect(screen.getByText("Sam")).toBeInTheDocument();
    expect(screen.queryByText("Sam — Support")).not.toBeInTheDocument();
    expect(screen.queryByRole("combobox", { name: "Model" })).not.toBeInTheDocument();

    openIdentityOptions();
    expect(await screen.findByRole("option", { name: "Sam — Support" })).toBeInTheDocument();
    fireEvent.click(screen.getByTitle("Jordan — Compliance"));
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
    fireEvent.click(screen.getByTitle("Jordan — Compliance"));
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
    expect(screen.getByRole("combobox", { name: "Agent" })).toBeDisabled();
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

    expect(screen.getByRole("combobox", { name: "Agent" })).toBeDisabled();
    expect(screen.getByText("No matching agents. Try another search or reload the list.")).toBeInTheDocument();
  });
});

it('explains an unavailable model without inventing a provider or credential', () => {
  const item = { ...instances[0], name: 'General Assistant', systemManaged: true, setupState: 'no_model_configured' };
  render(<AgentPicker managedInstances={[item]} identityKey={`managed:${item.instanceId}`} error={null} managedInstancesError={null} managedInstancesLoading={false} onIdentityChange={vi.fn()} onRetryManagedInstances={vi.fn()} />);
  expect(screen.getByText('No model configured')).toBeVisible();
  expect(screen.getByText(/configure an available model before starting Chat/)).toBeVisible();
});

it('lets an owner explicitly retry pending setup once, refresh candidates and focus the recovered picker', async () => {
  vi.mocked(tenantChatCanManageSetup).mockReturnValue(true); vi.mocked(tenantChatEmptyReason).mockReturnValue('setup_pending');
  let resolve!: (value: { status: 'ready' }) => void;
  vi.mocked(retryTenantGeneralAssistantSetup).mockReturnValue(new Promise(done => { resolve = done; }));
  const props = { managedInstances: [], identityKey: '', error: null, managedInstancesError: null, managedInstancesLoading: false, onIdentityChange: vi.fn(), onRetryManagedInstances: vi.fn() };
  const view = render(<AgentPicker {...props} />);
  const button = screen.getByRole('button', { name: 'Retry assistant setup' }); fireEvent.click(button); fireEvent.click(button);
  expect(retryTenantGeneralAssistantSetup).toHaveBeenCalledTimes(1); expect(props.onRetryManagedInstances).not.toHaveBeenCalled();
  resolve({ status: 'ready' }); await waitFor(() => expect(props.onRetryManagedInstances).toHaveBeenCalledOnce());
  vi.mocked(tenantChatEmptyReason).mockReturnValue(null);
  view.rerender(<AgentPicker {...props} managedInstances={instances} identityKey={`managed:${instances[0].instanceId}`} />);
  await waitFor(() => expect(screen.getByRole('combobox', { name: 'Agent' })).toHaveFocus());
});

it('keeps a failed setup retry actionable without exposing the underlying exception', async () => {
  vi.mocked(tenantChatCanManageSetup).mockReturnValue(true); vi.mocked(tenantChatEmptyReason).mockReturnValue('setup_pending');
  vi.mocked(retryTenantGeneralAssistantSetup).mockRejectedValue(new Error('private-provider-body'));
  const reload = vi.fn();
  render(<AgentPicker managedInstances={[]} identityKey="" error={null} managedInstancesError={null} managedInstancesLoading={false} onIdentityChange={vi.fn()} onRetryManagedInstances={reload} />);
  fireEvent.click(screen.getByRole('button', { name: 'Retry assistant setup' }));
  await waitFor(() => expect(screen.getByText(/Setup retry could not be confirmed/)).toBeVisible());
  expect(screen.queryByText('private-provider-body')).not.toBeInTheDocument(); expect(reload).not.toHaveBeenCalled();
});

it('does not offer provisioning recovery to a non-owner', () => {
  vi.mocked(tenantChatEmptyReason).mockReturnValue('setup_pending');
  render(<AgentPicker managedInstances={[]} identityKey="" error={null} managedInstancesError={null} managedInstancesLoading={false} onIdentityChange={vi.fn()} onRetryManagedInstances={vi.fn()} />);
  expect(screen.queryByRole('button', { name: 'Retry assistant setup' })).not.toBeInTheDocument();
  expect(retryTenantGeneralAssistantSetup).not.toHaveBeenCalled();
});

it('keeps pending default setup visible when another ordinary agent is available', () => {
  vi.mocked(tenantChatCanManageSetup).mockReturnValue(true); vi.mocked(tenantChatSetupPending).mockReturnValue(true);
  render(<AgentPicker managedInstances={instances} identityKey={`managed:${instances[0].instanceId}`} error={null} managedInstancesError={null} managedInstancesLoading={false} onIdentityChange={vi.fn()} onRetryManagedInstances={vi.fn()} />);
  expect(screen.getByText('Default assistant setup pending')).toBeVisible();
  expect(screen.getByRole('button', { name: 'Retry assistant setup' })).toBeVisible();
  expect(screen.getByRole('combobox', { name: 'Agent' })).toBeEnabled();
});
