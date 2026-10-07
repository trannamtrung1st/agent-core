import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { PendingApproval } from "../../state/sessionStore";
import { ApprovalModal } from "./ApprovalModal";

const approval: PendingApproval = {
  approvalId: "approval", responseId: "response", operationId: "operation",
  toolName: "harness.instructions.update", effect: "write",
  summary: "Save instructions.update for future conversations",
  details: {
    Change: "First line\n\n<script>alert('untrusted')</script>\nLast line",
    "Applies to": "Future conversations; this Session stays pinned.",
    "Policy revision": "2"
  },
  expiresAt: "2099-01-01T00:00:00Z"
};

describe("ApprovalModal", () => {
  it("keeps the complete plain-text proposal in a keyboard-accessible review region", () => {
    const approve = vi.fn();
    const reject = vi.fn();
    render(<ApprovalModal approval={approval} onApprove={approve} onReject={reject} />);
    const dialog = screen.getByRole("dialog", { name: "Save this harness change?" });
    const details = within(dialog).getByRole("region", { name: "Approval details" });
    expect(details).toHaveAttribute("tabindex", "0");
    expect(within(dialog).getByText(approval.details["Applies to"])).toBeInTheDocument();
    expect(within(dialog).getByText("Policy revision:")).toBeInTheDocument();
    expect(details.querySelector("script")).toBeNull();
    expect(details.querySelector(".chat-approval-proposal")?.textContent).toBe(approval.details.Change);
    fireEvent.click(within(dialog).getByRole("button", { name: "Approve" }));
    expect(approve).toHaveBeenCalledOnce();
    fireEvent.click(within(dialog).getByRole("button", { name: "Reject" }));
    expect(reject).toHaveBeenCalledOnce();
  });

  it("supports an empty-details sensitive approval and removes a cleared request", async () => {
    const view = render(<ApprovalModal approval={{ ...approval, toolName: "email.send", details: {} }} onApprove={vi.fn()} onReject={vi.fn()} />);
    expect(await screen.findByRole("dialog", { name: "Approve sensitive action" })).toBeInTheDocument();
    expect(screen.queryByRole("region", { name: "Approval details" })).toBeNull();
    view.rerender(<ApprovalModal approval={null} onApprove={vi.fn()} onReject={vi.fn()} />);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("starts a replacement approval at its opening line after a previous proposal was scrolled", () => {
    const view = render(<ApprovalModal approval={approval} onApprove={vi.fn()} onReject={vi.fn()} />);
    const oldRegion = screen.getByRole("region", { name: "Approval details" });
    oldRegion.scrollTop = 400;
    view.rerender(<ApprovalModal approval={{ ...approval, approvalId: "replacement", details: { Change: "New proposal opening line" } }} onApprove={vi.fn()} onReject={vi.fn()} />);
    const region = screen.getByRole("region", { name: "Approval details" });
    expect(region.scrollTop).toBe(0);
    expect(within(region).getByText("New proposal opening line")).toBeInTheDocument();
  });
});
