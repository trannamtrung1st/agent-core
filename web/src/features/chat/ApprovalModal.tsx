import { Modal, Typography } from "antd";
import type { PendingApproval } from "../../state/sessionStore";

type ApprovalModalProps = {
  approval: PendingApproval | null;
  onApprove: () => void;
  onReject: () => void;
};

export function ApprovalModal({ approval, onApprove, onReject }: ApprovalModalProps) {
  const open = approval != null;
  const detailEntries = approval ? Object.entries(approval.details) : [];

  return (
    <Modal
      open={open}
      className={approval?.toolName.startsWith("harness.") ? "harness-chat-approval" : undefined}
      title={approval?.toolName.startsWith("harness.") ? "Save this harness change?" : "Approve sensitive action"}
      okText="Approve"
      cancelText="Reject"
      onOk={onApprove}
      onCancel={onReject}
      destroyOnHidden
      mask={{ closable: false }}
      keyboard
    >
      {approval ? (
        <>
          <Typography.Paragraph>{approval.summary}</Typography.Paragraph>
          {detailEntries.length > 0 ? (
            <ul>
              {detailEntries.map(([key, value]) => (
                <li key={key}>
                  <Typography.Text strong>{key}</Typography.Text>
                  {": "}
                  {approval.toolName.startsWith("harness.") ? <Typography.Paragraph className="harness-procedure" style={{ maxHeight: "40vh", overflow: "auto", overflowWrap: "anywhere" }}>{value}</Typography.Paragraph> : <Typography.Text>{value}</Typography.Text>}
                </li>
              ))}
            </ul>
          ) : null}
        </>
      ) : null}
    </Modal>
  );
}
