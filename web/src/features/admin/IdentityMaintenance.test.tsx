import { App, ConfigProvider } from "antd";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ApprovalModal } from "../chat/ApprovalModal";
import { IdentityMaintenanceSection, ExperienceSection } from "./InstanceContinuitySection";
import { MemoryLineageDetails } from "./instanceMemoryAutomation";
import { instanceContinuityRequest, getAdminLearnedMemory, type ExperienceItem, type AdminLearnedMemoryItem } from "../../services/adminApi";

vi.mock("../../services/adminApi", async importOriginal => ({ ...await importOriginal<typeof import("../../services/adminApi")>(), instanceContinuityRequest: vi.fn(), getAdminLearnedMemory: vi.fn() }));
vi.mock("../../services/api", async importOriginal => ({ ...await importOriginal<typeof import("../../services/api")>(), listModels: vi.fn().mockResolvedValue({ models: [] }) }));
const request = vi.mocked(instanceContinuityRequest);
const getMemory = vi.mocked(getAdminLearnedMemory);
const wrapper = ({ children }: { children: React.ReactNode }) => <ConfigProvider><App>{children}</App></ConfigProvider>;
afterEach(() => { cleanup(); vi.resetAllMocks(); });

describe("Identity maintenance", () => {
  it("lets the owner review complete consolidation text and every parent before deciding", async () => {
    const approve = vi.fn(); const reject = vi.fn();
    const parents = Array.from({ length: 8 }, (_, i) => `019944af-00d${i}-7000-8000-000000000001`);
    const replacement = "x".repeat(1900) + " retained project qualifier";
    render(<ApprovalModal approval={{ approvalId: "approval", responseId: "response", operationId: "operation", effect: "Write", toolName: "memory.consolidate", summary: "Consolidate selected learned memories", details: { Sources: parents.join(", "), Replacement: replacement }, expiresAt: "2026-10-06T03:00:00Z" }} onApprove={approve} onReject={reject} />, { wrapper });
    await waitFor(() => expect(screen.getByRole("dialog", { name: "Approve identity consolidation" })).toBeVisible());
    expect(screen.getByText(replacement)).toBeVisible();
    expect(screen.getByText(parents.join(", "))).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));
    expect(approve).toHaveBeenCalledOnce(); expect(reject).not.toHaveBeenCalled();
  });

  it("persists a default-off revisioned setting and keeps the switch disabled while saving", async () => {
    let release!: (value: unknown) => void;
    request.mockResolvedValueOnce({ agentInstanceId: "instance", allowAgentConsolidation: false, revision: 4 });
    request.mockImplementationOnce(() => new Promise(resolve => { release = resolve; }));
    render(<IdentityMaintenanceSection instanceId="instance" />, { wrapper });
    const toggle = screen.getByRole("switch", { name: "Allow agent consolidation" });
    await waitFor(() => expect(toggle).toBeEnabled());
    expect(toggle).toHaveAttribute("aria-checked", "false");
    fireEvent.click(toggle);
    expect(toggle).toBeDisabled();
    expect(request).toHaveBeenLastCalledWith("instance", "maintenance", "PUT", { expectedRevision: 4, allowAgentConsolidation: true });
    release({ agentInstanceId: "instance", allowAgentConsolidation: true, revision: 5 });
    await waitFor(() => expect(toggle).toHaveAttribute("aria-checked", "true"));
    expect(screen.getByText(/It does not schedule runs, change memory scope, or permit silent forgetting/)).toBeVisible();
  });

  it("offers reload after a stale revision and then uses the refreshed revision", async () => {
    request.mockResolvedValueOnce({ agentInstanceId: "instance", allowAgentConsolidation: false, revision: 1 })
      .mockRejectedValueOnce(new Error("Maintenance settings revision is stale."))
      .mockResolvedValueOnce({ agentInstanceId: "instance", allowAgentConsolidation: true, revision: 3 })
      .mockResolvedValueOnce({ agentInstanceId: "instance", allowAgentConsolidation: false, revision: 4 });
    render(<IdentityMaintenanceSection instanceId="instance" />, { wrapper });
    const toggle = screen.getByRole("switch", { name: "Allow agent consolidation" });
    await waitFor(() => expect(toggle).toBeEnabled()); fireEvent.click(toggle);
    await screen.findByRole("button", { name: "Reload" });
    expect(toggle).toBeDisabled(); fireEvent.click(screen.getByRole("button", { name: "Reload" }));
    await waitFor(() => expect(toggle).toBeEnabled()); fireEvent.click(toggle);
    await waitFor(() => expect(request).toHaveBeenLastCalledWith("instance", "maintenance", "PUT", { expectedRevision: 3, allowAgentConsolidation: false }));
  });

  it("shows bounded memory lineage, historical states and recoverable source reads", async () => {
    const row: AdminLearnedMemoryItem = { memoryId: "canonical", kind: "Preference", status: "Active", subject: "Frontend examples", content: "Prefer TypeScript for frontend examples.",
      provenance: { source: "agentInferred", originMemoryId: null, originSessionId: null, recordedAt: "2026-10-06T00:00:00Z", derivedFromMemoryIds: ["source-a", "source-b"], maintenanceOrigin: "Thought" }, updatedAt: "2026-10-06T00:00:00Z" };
    getMemory.mockRejectedValueOnce(new Error("Read unavailable"));
    getMemory.mockImplementation(async (_instance, id) => ({ ...row, memoryId: id, status: "Superseded", provenance: { ...row.provenance, derivedFromMemoryIds: [] } }));
    render(<MemoryLineageDetails instanceId="instance" row={row} scope="IdentityUser" />, { wrapper });
    expect(screen.getByText("Consolidated from 2 memories")).toBeVisible();
    fireEvent.click(screen.getByRole("button", { name: "View sources" }));
    await screen.findByRole("button", { name: "Retry sources" });
    fireEvent.click(screen.getByRole("button", { name: "Retry sources" }));
    await waitFor(() => expect(screen.getAllByText("Superseded")).toHaveLength(2));
    expect(getMemory).toHaveBeenCalledWith("instance", "source-b", "IdentityUser", undefined);
  });

  it("distinguishes consolidated and superseded Experience without offering restoration", async () => {
    const row: ExperienceItem = { experienceId: "consolidated", sourceKind: "Consolidation", sourceId: "operation", sourceAt: "2026-10-06T00:00:00Z", throughCursor: 0, definitionId: "assistant", definitionVersion: 9,
      generationAgentRunId: "00000000-0000-0000-0000-000000000000", modelKey: "synthetic", status: "Completed", visibility: "Superseded", revision: 3, eligibleForContext: false,
      content: { goal: "Repeated browser lessons", attempts: [], decisions: [], outcomes: [], corrections: [], unresolved: [], difficulties: [], lessons: ["Preserve meaningful exceptions"] },
      diagnosticId: null, failureSummary: null, derivedFromExperienceIds: ["a", "b"], maintenanceOrigin: "Thought" };
    request.mockResolvedValue({ enabled: true, settingsRevision: 1, contextBudgetCharacters: 6000, items: [row] });
    render(<ExperienceSection instanceId="instance" onWork={vi.fn()} />, { wrapper });
    fireEvent.click(await screen.findByRole("button", { name: "View experience: Repeated browser lessons" }));
    expect(screen.getByText("Consolidated from 2 experiences")).toBeVisible();
    expect(screen.getAllByText("Superseded").length).toBeGreaterThan(0);
    expect(screen.getByRole("button", { name: "Suppress experience" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "View generation run" })).not.toBeInTheDocument();
  });
});
