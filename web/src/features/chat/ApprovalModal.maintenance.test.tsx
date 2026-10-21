import { App, ConfigProvider } from 'antd';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { expect, it, vi } from 'vitest';
import { ApprovalModal } from './ApprovalModal';
const wrapper = ({ children }: { children: React.ReactNode }) => <ConfigProvider><App>{children}</App></ConfigProvider>;

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
