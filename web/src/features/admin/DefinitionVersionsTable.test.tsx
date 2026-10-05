import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import { DefinitionVersionsTable } from "./DefinitionVersionsTable";
import { getAdminDefinitionVersion, listAdminAuthoringOptions } from "../../services/adminApi";

vi.mock("../../services/adminApi", () => ({
  getAdminDefinitionVersion: vi.fn(),
  listAdminAuthoringOptions: vi.fn()
}));

describe("Definition version inspection", () => {
  it("retries a failed inspection and keeps colliding source versions distinct and read-only", async () => {
    vi.mocked(listAdminAuthoringOptions).mockResolvedValue({
      languageModelAliases: [], speechRecognizerAliases: [], speechSynthesizerAliases: [],
      defaultLanguageModelAlias: null, defaultSpeechRecognizerAlias: null, defaultSpeechSynthesizerAlias: null,
      defaultModelKey: "synthetic", models: [], interruptionClassifiers: []
    });
    vi.mocked(getAdminDefinitionVersion)
      .mockRejectedValueOnce(new Error("Version load failed"))
      .mockResolvedValueOnce({ definitionId: "examiner", systemInstructions: "Built-in instructions", goals: ["A long immutable goal that remains readable on a narrow screen"] })
      .mockResolvedValueOnce({ definitionId: "examiner", systemInstructions: "Durable instructions" });
    await act(async () => {
      render(<DefinitionVersionsTable
        rows={[
          { definitionId: "examiner", version: 1, source: "builtIn", status: "published", displayName: "Examiner" },
          { definitionId: "examiner", version: 1, source: "durable", status: "published", displayName: "Examiner" }
        ]}
        publications={[]} busy={false} onChat={vi.fn()} onDeprecate={vi.fn()} renderResources={() => null}
      />);
    });
    fireEvent.click(screen.getByRole("button", { name: "View v1 (builtIn)" }));
    expect(await screen.findByText("Version load failed")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));
    const details = screen.getByRole("region", { name: "Version details" });
    await waitFor(() => expect(within(details).getByLabelText("System instructions")).toHaveValue("Built-in instructions"));
    expect(within(details).getByLabelText("System instructions")).toBeDisabled();
    expect(within(details).getByLabelText("Goal 1").tagName).toBe("TEXTAREA");
    expect(within(details).getByLabelText("Goal 1")).toHaveValue("A long immutable goal that remains readable on a narrow screen");
    expect(within(details).queryByRole("button", { name: "Add goal" })).not.toBeInTheDocument();
    expect(within(details).queryByRole("button", { name: "Remove goal 1" })).not.toBeInTheDocument();
    fireEvent.click(within(details).getByText("Advanced JSON", { exact: true }));
    expect(within(details).getByRole("textbox", { name: "Advanced JSON" })).toHaveAttribute("readonly");
    expect(within(details).queryByRole("button", { name: "Save draft" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Close" }));
    await waitFor(() => expect(screen.queryByRole("region", { name: "Version details" })).not.toBeInTheDocument());
    fireEvent.click(screen.getByRole("button", { name: "View v1 (durable)" }));
    await waitFor(() => expect(screen.getByLabelText("System instructions")).toHaveValue("Durable instructions"));
    expect(getAdminDefinitionVersion).toHaveBeenNthCalledWith(1, "examiner", 1, "builtIn");
    expect(getAdminDefinitionVersion).toHaveBeenNthCalledWith(2, "examiner", 1, "builtIn");
    expect(getAdminDefinitionVersion).toHaveBeenNthCalledWith(3, "examiner", 1, "durable");
  });
});
