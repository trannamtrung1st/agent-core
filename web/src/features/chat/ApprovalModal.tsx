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
      title="Approve sensitive action"
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
                  <Typography.Text>{value}</Typography.Text>
                </li>
              ))}
            </ul>
          ) : null}
        </>
      ) : null}
    </Modal>
  );
}
