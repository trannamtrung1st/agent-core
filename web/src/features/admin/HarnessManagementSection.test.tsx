import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { App as AntApp } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { HarnessManagementSection } from "./HarnessManagementSection";
import { getHarnessReview, updateHarness, type HarnessReview } from "../../services/adminApi";
vi.mock("../../services/adminApi", () => ({ getHarnessReview: vi.fn(), updateHarness: vi.fn() }));

const base: HarnessReview = { instanceId: "instance", instanceRevision: 1, activeVersion: 7, policyRevision: 1,
  policy: { mode: "Disabled", scopes: [], sources: [], eligibleTools: [], frozen: false }, preparation: null,
  draftRevision: null, instructions: null, skills: [], knowledge: [], selectedTools: [], diff: null, resources: [] };
const candidate: HarnessReview = { ...base, policy: { ...base.policy, mode: "Managed", scopes: ["Skills"] }, draftRevision: 3,
  preparation: { preparationId: "preparation", draftId: "draft", baseVersion: 7, purpose: "Review operations", status: "Ready",
    publishedVersion: null, publishedDraftRevision: null, diagnosticId: null, approvals: [], evidence: [
      { actor: "Agent", draftRevision: 2, check: "Old report", status: "Verified", expected: "Old", observed: "Old", limitation: null },
      { actor: "Core", draftRevision: 3, check: "Validation", status: "Verified", expected: "Valid", observed: "Valid", limitation: null },
      { actor: "Agent", draftRevision: 3, check: "Safe sample", status: "PartiallyVerified", expected: "Safe boundary", observed: "Stopped safely", limitation: "Refund execution requires owner evidence." },
      { actor: "Agent", draftRevision: 3, check: "Unavailable resource", status: "CannotVerify", expected: "Read private resource", observed: "Access unavailable", limitation: "Missing permission." },
      { actor: "Core", draftRevision: 3, check: "External quality", status: "RequiresExternalEvidence", expected: "Review tone", observed: "Owner review required", limitation: null }
    ] } };
function mount() { render(<AntApp><HarnessManagementSection instanceId="instance" eligibleTools={["http.request"]} onUpdated={vi.fn()} /></AntApp>); }

describe("Harness management", () => {
  beforeEach(() => { vi.mocked(getHarnessReview).mockReset(); vi.mocked(updateHarness).mockReset(); });
  it("keeps manual defaults and requires a purpose before preparation", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue(base); mount();
    expect(await screen.findByText("Manual")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Prepare harness" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByText("Configure authoring policy"));
    expect(await screen.findByLabelText("Authoring mode")).toBeVisible();
    expect(screen.getByRole("button", { name: "Save authoring policy" })).toBeDisabled();
  });
  it("separates agent assessments, Core checks, stale evidence, and external limits", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue(candidate); mount();
    fireEvent.click(await screen.findByText(/Verification & limitations/));
    expect(await screen.findByText("Partially verified")).toBeVisible();
    expect(screen.getByText("Cannot verify")).toBeVisible();
    expect(screen.getByText("Requires external evidence")).toBeVisible();
    expect(screen.getByText("Stale · revision 2")).toBeVisible();
    expect(screen.getByText("Refund execution requires owner evidence.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Publish & adopt" })).toBeEnabled();
  });
  it("requires an exact visible approval and submits the action hash after confirmation", async () => {
    const pending: HarnessReview = { ...candidate, preparation: { ...candidate.preparation!, status: "AwaitingApproval", approvals: [
      { approvalId: "approval", actionHash: "exact-hash", status: "Pending", operation: { kind: "tool.select", draftRevision: 3, id: "http.request", enabled: false,
        content: null, source: null, skill: null, allowUnreadUnsupportedTypes: null } }
    ] } };
    vi.mocked(getHarnessReview).mockResolvedValue(pending); vi.mocked(updateHarness).mockResolvedValue(candidate); mount();
    expect(await screen.findByText("Approval required: tool.select")).toBeVisible();
    expect(screen.getByRole("button", { name: "Publish & adopt" })).toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Review & approve change" }));
    expect(updateHarness).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole("button", { name: "Approve change" }));
    await waitFor(() => expect(updateHarness).toHaveBeenCalledWith("instance", "approvals/approval", { expectedRevision: 1, actionHash: "exact-hash", approve: true }));
  });
  it("refreshes a conflict without replaying and preserves an actionable error", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue(candidate); vi.mocked(updateHarness).mockRejectedValue(new Error("Candidate revision is stale; reload.")); mount();
    fireEvent.click(await screen.findByRole("button", { name: "Verify candidate" }));
    expect(await screen.findByText("Candidate revision is stale; reload.")).toBeVisible();
    expect(screen.getByRole("button", { name: "Reload" })).toBeEnabled();
    expect(updateHarness).toHaveBeenCalledTimes(1);
    expect(getHarnessReview).toHaveBeenCalledTimes(2);
  });
  it("shows frozen publication and evidence for the tested revision", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue({ ...candidate, activeVersion: 13, draftRevision: 4,
      policy: { ...candidate.policy, mode: "Disabled", frozen: true }, preparation: { ...candidate.preparation!, status: "Published", publishedVersion: 13, publishedDraftRevision: 3 } });
    mount(); expect(await screen.findByText("Frozen")).toBeVisible(); expect(screen.getByText("Published & adopted")).toBeVisible();
    expect(screen.queryByRole("button", { name: "Continue preparation" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByText(/Verification & limitations/));
    expect(await screen.findByText("Partially verified")).toBeVisible();
    expect(screen.queryByText("Stale · revision 3")).not.toBeInTheDocument();
  });
  it("shows loading, failed checks and a retry after load failure", async () => {
    vi.mocked(getHarnessReview).mockRejectedValueOnce(new Error("Load failed")).mockResolvedValueOnce({ ...candidate,
      preparation: { ...candidate.preparation!, status: "Failed" } }); mount();
    expect(await screen.findByText("Load failed")).toBeVisible(); fireEvent.click(screen.getByRole("button", { name: "Reload" }));
    expect(await screen.findByText("Failed", {exact:true})).toBeVisible();
    expect(screen.getByRole("button", { name: "Prepare harness" })).toBeDisabled();
  });
});
