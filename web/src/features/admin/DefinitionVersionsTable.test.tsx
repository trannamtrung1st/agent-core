import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { DefinitionVersionsTable } from "./DefinitionVersionsTable";
import { getAdminDefinitionVersion, listAdminAuthoringOptions } from "../../services/adminApi";

import { exposedRoles } from "../../test/exposedRoles";

vi.mock("../../services/adminApi", () => ({
  getExecutionBudgetLimits: vi.fn().mockResolvedValue({ maxSteps: 144, durationSeconds: 900, perToolSeconds: 30 }),
  getAdminDefinitionVersion: vi.fn(),
  listAdminAuthoringOptions: vi.fn()
}));

describe("Definition version inspection", () => {
  beforeEach(() => {
    vi.mocked(getAdminDefinitionVersion).mockReset();
    vi.mocked(listAdminAuthoringOptions).mockReset();
    vi.mocked(listAdminAuthoringOptions).mockResolvedValue({
      languageModelAliases: [], speechRecognizerAliases: [], speechSynthesizerAliases: [],
      defaultLanguageModelAlias: null, defaultSpeechRecognizerAlias: null, defaultSpeechSynthesizerAlias: null,
      defaultModelKey: "synthetic", models: [], interruptionClassifiers: []
    });
  });

  async function renderVersions() {
    await act(async () => {
      render(<DefinitionVersionsTable
        rows={[
          { definitionId: "examiner", version: 1, source: "builtIn", status: "published", displayName: "Examiner" },
          { definitionId: "examiner", version: 1, source: "durable", status: "published", displayName: "Examiner" }
        ]}
        publications={[]} busy={false} onChat={vi.fn()} onDeprecate={vi.fn()} renderResources={() => null}
      />);
    });
  }

  it("retries a failed inspection and renders the immutable form and JSON read-only", async () => {
    vi.mocked(getAdminDefinitionVersion)
      .mockRejectedValueOnce(new Error("Version load failed"))
      .mockResolvedValueOnce({ definitionId: "examiner", systemInstructions: "Built-in instructions", goals: ["A long immutable goal that remains readable on a narrow screen"], skills: [{ id: "sample.hello", name: "Sample hello", description: "A greeting procedure", procedure: "Say hello", projection: "OnDemand", defaultEnabled: true, requiredCapabilities: [] }] });
    await renderVersions();
    fireEvent.click(exposedRoles().getByRole("button", { name: "View v1 (builtIn)" }));
    expect(await screen.findByText("Version load failed")).toBeInTheDocument();
    fireEvent.click(exposedRoles().getByRole("button", { name: "Retry" }));
    const details = exposedRoles().getByRole("region", { name: "Version details" });
    fireEvent.click(await exposedRoles(details).findByRole("tab", { name: "Settings" }));
    fireEvent.click(exposedRoles(details).getByRole("button", { name: "Operating instructions" }));
    await waitFor(() => expect(within(details).getByLabelText("System instructions")).toHaveValue("Built-in instructions"));
    expect(within(details).getByLabelText("System instructions")).not.toBeDisabled();
    expect(within(details).getByLabelText("System instructions")).toHaveAttribute("readonly");
    act(() => { within(details).getByLabelText("System instructions").focus(); });
    expect(within(details).getByLabelText("System instructions")).toHaveFocus();
    fireEvent.click(exposedRoles(details).getByRole("tab", { name: "Profile" }));
    expect(within(details).getByLabelText("Goal 1").tagName).toBe("TEXTAREA");
    expect(within(details).getByLabelText("Goal 1")).toHaveValue("A long immutable goal that remains readable on a narrow screen");
    const profile = within(details.querySelector<HTMLElement>(".admin-definition-profile")!);
    expect(profile.queryByRole("button", { name: "Add goal" })).not.toBeInTheDocument();
    expect(profile.queryByRole("button", { name: "Remove goal 1" })).not.toBeInTheDocument();
    expect(within(details).queryByText("Sample hello")).not.toBeInTheDocument();
    fireEvent.click(exposedRoles(details).getByRole("tab", { name: "Skills & resources" }));
    const skills = exposedRoles(details).getByRole("region", { name: "Skills" });
    expect(within(skills).getByText("Sample hello")).toBeInTheDocument();
    expect(within(skills).queryByRole("button", { name: "Add skill" })).not.toBeInTheDocument();
    expect(within(skills).queryByRole("button", { name: "Edit" })).not.toBeInTheDocument();
    fireEvent.click(exposedRoles(details).getByRole("tab", { name: "Identity & version" }));
    fireEvent.click(within(details).getByText("Advanced JSON", { exact: true }));
    expect(within(details).getByLabelText("Advanced JSON", { selector: "textarea" })).toHaveAttribute("readonly");
    expect(within(details).queryByRole("button", { name: "Save draft" })).not.toBeInTheDocument();
    await act(async () => { fireEvent.click(exposedRoles().getByRole("button", { name: "Close" })); });
    await waitFor(() => expect(screen.queryByRole("region", { name: "Version details" })).not.toBeInTheDocument());
    expect(getAdminDefinitionVersion).toHaveBeenNthCalledWith(1, "examiner", 1, "builtIn");
    expect(getAdminDefinitionVersion).toHaveBeenNthCalledWith(2, "examiner", 1, "builtIn");
  });

  it("keeps colliding source versions distinct when filtering and reopening the drawer", async () => {
    vi.mocked(getAdminDefinitionVersion)
      .mockResolvedValueOnce({ definitionId: "examiner", systemInstructions: "Built-in instructions" })
      .mockResolvedValueOnce({ definitionId: "examiner", systemInstructions: "Durable instructions" });
    await renderVersions();
    const search = screen.getByLabelText("Search versions");
    fireEvent.change(search, { target: { value: "Built-in" } });
    expect(screen.queryByRole("button", { name: "View v1 (durable)" })).not.toBeInTheDocument();
    fireEvent.change(search, { target: { value: "v1" } });
    expect(exposedRoles().getByRole("button", { name: "View v1 (durable)" })).toBeInTheDocument();
    fireEvent.click(exposedRoles().getByRole("button", { name: "View v1 (builtIn)" }));
    fireEvent.click(await exposedRoles().findByRole("tab", { name: "Settings" }));
    fireEvent.click(exposedRoles().getByRole("button", { name: "Operating instructions" }));
    await waitFor(() => expect(screen.getByLabelText("System instructions")).toHaveValue("Built-in instructions"));
    await act(async () => { fireEvent.click(exposedRoles().getByRole("button", { name: "Close" })); });
    await waitFor(() => expect(screen.queryByRole("region", { name: "Version details" })).not.toBeInTheDocument());
    fireEvent.click(exposedRoles().getByRole("button", { name: "View v1 (durable)" }));
    fireEvent.click(await exposedRoles().findByRole("tab", { name: "Settings" }));
    fireEvent.click(exposedRoles().getByRole("button", { name: "Operating instructions" }));
    await waitFor(() => expect(screen.getByLabelText("System instructions")).toHaveValue("Durable instructions"));
    expect(getAdminDefinitionVersion).toHaveBeenNthCalledWith(1, "examiner", 1, "builtIn");
    expect(getAdminDefinitionVersion).toHaveBeenNthCalledWith(2, "examiner", 1, "durable");
  });
});
