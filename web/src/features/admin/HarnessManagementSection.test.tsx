import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { App as AntApp } from "antd";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { HarnessManagementSection } from "./HarnessManagementSection";
import { getHarnessReview, updateHarness, type HarnessReview } from "../../services/adminApi";
vi.mock("../../services/adminApi", () => ({ getHarnessReview: vi.fn(), updateHarness: vi.fn() }));

const base: HarnessReview = { instanceId: "instance", instanceRevision: 1, activeVersion: 7, policyRevision: 1,
  policy: { mode: "Disabled", scopes: [], sources: [], eligibleTools: [], frozen: false }, preparation: null,
  draftRevision: null, instructions: null, skills: [], knowledge: [], selectedTools: [], diff: null, resources: [] };
const candidate: HarnessReview = { ...base, policy: { ...base.policy, mode: "Managed", scopes: ["KnowledgeResources"] }, draftRevision: 3,
  preparation: { preparationId: "preparation", draftId: "draft", baseVersion: 7, purpose: "Review operations", status: "Ready",
    publishedVersion: null, publishedDraftRevision: null, diagnosticId: null, approvals: [], evidence: [
      { actor: "Agent", draftRevision: 2, check: "Old report", status: "Verified", expected: "Old", observed: "Old", limitation: null },
      { actor: "Core", draftRevision: 3, check: "Validation", status: "Verified", expected: "Valid", observed: "Valid", limitation: null },
      { actor: "Agent", draftRevision: 3, check: "Safe sample", status: "PartiallyVerified", expected: "Safe boundary", observed: "Stopped safely", limitation: "Refund execution requires owner evidence." },
      { actor: "Agent", draftRevision: 3, check: "Unavailable resource", status: "CannotVerify", expected: "Read private resource", observed: "Access unavailable", limitation: "Missing permission." },
      { actor: "Core", draftRevision: 3, check: "External quality", status: "RequiresExternalEvidence", expected: "Review tone", observed: "Owner review required", limitation: null }
    ] } };
function mount() { render(<AntApp><HarnessManagementSection instanceId="instance" eligibleTools={["http.request"]} onUpdated={vi.fn()} /></AntApp>); }

describe("Harness governance", () => {
  beforeEach(() => { vi.mocked(getHarnessReview).mockReset(); vi.mocked(updateHarness).mockReset(); });
  it("keeps Manual defaults and explains conversational teaching without preparation controls", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue(base); mount();
    expect(await screen.findByText("Manual")).toBeVisible();
    expect(screen.getByLabelText("Authoring mode")).toBeVisible();
    expect(screen.getByRole("button", {name:"Save authoring policy"})).toBeDisabled();
    for (const name of ["Prepare harness", "Continue preparation", "Verify candidate", "Publish & adopt"])
      expect(screen.queryByRole("button", {name})).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Permitted sources")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Eligible tools")).not.toBeInTheDocument();
  });
  it("keeps evidence ownership, staleness and external limitations in operator inspection", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue(candidate); mount();
    fireEvent.click(await screen.findByText("Recent harness change & verification"));
    expect(await screen.findByText("Partially verified")).toBeVisible();
    expect(screen.getByText("Cannot verify")).toBeVisible();
    expect(screen.getByText("Requires external evidence")).toBeVisible();
    expect(screen.getByText("Stale · revision 2")).toBeVisible();
    expect(screen.getByText("Refund execution requires owner evidence.")).toBeVisible();
  });
  it("discards an unfinished historical candidate only after confirmation", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue(candidate);vi.mocked(updateHarness).mockResolvedValue(base); mount();
    fireEvent.click(await screen.findByText("Recent harness change & verification"));
    fireEvent.click(await screen.findByRole("button", {name:"Discard unfinished candidate"}));
    expect(updateHarness).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole("button", {name:"Discard candidate"}));
    await waitFor(() => expect(updateHarness).toHaveBeenCalledWith("instance", "cancel", {expectedRevision:1}));
  });
  it("refreshes a policy conflict without replay and keeps the error actionable", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue(candidate);vi.mocked(updateHarness).mockRejectedValue(new Error("Policy revision is stale; reload."));mount();
    fireEvent.click(await screen.findByRole("button", {name:"Freeze self-management"}));
    fireEvent.click(within(await screen.findByRole("dialog")).getByRole("button", {name:/^Freeze self-management$/}));
    expect(await screen.findByText("Policy revision is stale; reload.")).toBeVisible();
    expect(screen.getByRole("button", {name:"Reload"})).toBeEnabled();
    expect(updateHarness).toHaveBeenCalledTimes(1);expect(getHarnessReview).toHaveBeenCalledTimes(2);
  });
  it("retains published evidence and the tested revision after freeze", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue({...candidate, activeVersion:13,draftRevision:4,policy:{...candidate.policy,mode:"Disabled",frozen:true},
      preparation:{...candidate.preparation!,status:"Published",publishedVersion:13,publishedDraftRevision:3}}); mount();
    expect(await screen.findByText("Frozen")).toBeVisible();
    fireEvent.click(screen.getByText("Recent harness change & verification"));
    expect(await screen.findByText("Saved for future conversations · version 13")).toBeVisible();
    expect(screen.queryByText("Stale · revision 3")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", {name:"Discard unfinished candidate"})).not.toBeInTheDocument();
  });
  it("offers a retry for a failed governance load", async () => {
    vi.mocked(getHarnessReview).mockRejectedValueOnce(new Error("Load failed")).mockResolvedValueOnce(base);mount();
    expect(await screen.findByText("Load failed")).toBeVisible();fireEvent.click(screen.getByRole("button", {name:"Reload"}));
    expect(await screen.findByText("Manual")).toBeVisible();
  });
  it("requires an area for an enabled policy and recovers when one is selected", async () => {
    vi.mocked(getHarnessReview).mockResolvedValue({ ...base, policy: { ...base.policy, mode: "Managed" } });
    vi.mocked(updateHarness).mockResolvedValue({ ...base, policy: { ...base.policy, mode: "Managed", scopes: ["KnowledgeResources"] } });
    mount();
    expect(await screen.findByRole("group", { name: "Areas the agent may manage" })).toHaveAccessibleDescription("Select at least one area.");
    const save = screen.getByRole("button", { name: "Save authoring policy" });
    expect(save).toBeDisabled();
    expect(screen.getByRole("combobox", { name: "Authoring mode" })).toHaveAccessibleDescription("Allowed knowledge may auto-save. Instructions and tools need approval.");
    fireEvent.click(screen.getByRole("checkbox", { name: "Knowledge & resources" }));
    expect(save).toBeEnabled();
    fireEvent.click(save);
    await waitFor(() => expect(updateHarness).toHaveBeenCalledWith("instance", "policy", {
      expectedRevision: 1, mode: "Managed", scopes: ["KnowledgeResources"], sources: [], eligibleTools: [], frozen: false
    }));
  });
});
