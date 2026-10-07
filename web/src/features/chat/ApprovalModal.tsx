import { Alert, Modal, Tag, Typography, theme } from "antd";
import type { PendingApproval } from "../../state/sessionStore";

type ApprovalModalProps = {
  approval: PendingApproval | null;
  onApprove: () => void;
  onReject: () => void;
};

// Style supplied paragraph headings and list lines without interpreting HTML or rewriting the proposal.
function ProposalText({ value }: { value: string }) {
  return (
    <div className="chat-approval-proposal">
      {value.split("\n").map((line, index, lines) => (
        <div key={index} className={line.startsWith("- ") ? "chat-approval-proposal-item" : undefined}>
          {line.endsWith(":") ? <Typography.Text strong>{line}</Typography.Text> : line}
          {index < lines.length - 1 ? "\n" : null}
        </div>
      ))}
    </div>
  );
}

export function ApprovalModal({ approval, onApprove, onReject }: ApprovalModalProps) {
  const { token } = theme.useToken();
  const open = approval != null;
  const filesystem = ["workspace.delete", "workspace.batch"].includes(approval?.toolName ?? "");
  const semantic = filesystem || approval?.toolName.startsWith("harness.") || ["memory.consolidate", "experience.consolidate", "memory.forget"].includes(approval?.toolName ?? "");
  const detailEntries = approval ? Object.entries(approval.details) : [];
  const revisionKeys = ["Active version", "Policy revision"];
  const revisions = detailEntries.filter(([key]) => revisionKeys.includes(key));
  const scope = approval?.details["Applies to"];
  const content = detailEntries.filter(([key]) => !revisionKeys.includes(key) && key !== "Applies to");

  return (
    <Modal
      open={open}
      className="chat-approval"
      centered
      width={semantic ? 720 : 520}
      title={filesystem ? "Approve workspace changes?" : approval?.toolName.startsWith("harness.") ? "Save this harness change?" : approval?.toolName === "memory.forget" ? "Forget this learned-memory item?" : semantic ? "Approve identity consolidation" : "Approve sensitive action"}
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
          <Typography.Paragraph className="chat-approval-summary">{approval.summary}</Typography.Paragraph>
          {scope !== undefined ? <Alert className="chat-approval-scope" type="info" showIcon title="Applies to" description={scope} /> : null}
          {revisions.length > 0 ? (
            <ul className="chat-approval-revisions">
              {revisions.map(([key, value]) => (
                <li key={key}>
                  <Typography.Text type="secondary">{key}{": "}</Typography.Text>
                  <Tag>{value}</Tag>
                </li>
              ))}
            </ul>
          ) : null}
          {content.length > 0 ? (
            <div key={approval.approvalId} className="chat-approval-details" role="region" aria-label="Approval details" tabIndex={0}>
              <ul className="chat-approval-list">
                {content.map(([key, value]) => (
                  <li className={`chat-approval-detail${key === "Change" ? " chat-approval-change" : ""}`} style={key === "Change" ? { borderRadius: token.borderRadiusLG } : undefined} key={key}>
                    <Typography.Text strong>{key}{": "}</Typography.Text>
                    {key === "Change" ? <ProposalText value={value} /> : <Typography.Text className="chat-approval-value">{value}</Typography.Text>}
                  </li>
                ))}
              </ul>
            </div>
          ) : null}
        </>
      ) : null}
    </Modal>
  );
}
