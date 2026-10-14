import { Alert, Descriptions, Flex, Modal, Tag, Typography, theme } from 'antd';
import type { PendingApproval } from '../../state/sessionStore';

type ApprovalModalProps = {
  approval: PendingApproval | null;
  onApprove: () => void;
  onReject: () => void;
};

// Style supplied paragraph headings and list lines without interpreting HTML or rewriting the proposal.
function ProposalText({ value }: { value: string }) {
  return (
    <div className="chat-approval-proposal">
      {value.split('\n').map((line, index, lines) => (
        <div
          key={index}
          className={
            line.startsWith('- ') ? 'chat-approval-proposal-item' : undefined
          }
        >
          {line.endsWith(':') ? (
            <Typography.Text strong>{line}</Typography.Text>
          ) : (
            line
          )}
          {index < lines.length - 1 ? '\n' : null}
        </div>
      ))}
    </div>
  );
}

export function ApprovalModal({
  approval,
  onApprove,
  onReject,
}: ApprovalModalProps) {
  const { token } = theme.useToken();
  const open = approval != null;
  const filesystem = ['workspace.delete', 'workspace.batch'].includes(
    approval?.toolName ?? '',
  );
  const management =
    /^(configuration|management|settings|resources|skills|credentials\.binding|automation\.preferences)\./.test(
      approval?.toolName ?? '',
    );
  const semantic =
    management ||
    filesystem ||
    approval?.toolName.startsWith('harness.') ||
    ['memory.consolidate', 'experience.consolidate', 'memory.forget'].includes(
      approval?.toolName ?? '',
    );
  const drive = [
    'drive.trash',
    'drive.trash_batch',
    'drive.rename',
    'drive.move',
  ].includes(approval?.toolName ?? '');
  let driveFiles: Array<{
    fileId: string;
    name: string;
    expectedVersion: string;
  }> = [];
  if (drive && approval?.details.Files) {
    try {
      const value: unknown = JSON.parse(approval.details.Files);
      if (
        Array.isArray(value) &&
        value.length >= 1 &&
        value.length <= 20 &&
        value.every(
          (f) =>
            f &&
            typeof f.fileId === 'string' &&
            f.fileId.length <= 160 &&
            typeof f.name === 'string' &&
            f.name.length <= 255 &&
            typeof f.expectedVersion === 'string' &&
            f.expectedVersion.length <= 64,
        )
      )
        driveFiles = value;
    } catch {
      /* Preserve raw inert details if the trusted projection cannot be interpreted. */
    }
  }
  const trash =
    approval?.toolName === 'drive.trash' ||
    approval?.toolName === 'drive.trash_batch';
  const driveAction = trash
    ? driveFiles.length > 1
      ? `Move ${driveFiles.length} files to Trash`
      : 'Move to Trash'
    : approval?.toolName === 'drive.rename'
      ? 'Rename file'
      : 'Move file';
  const driveTitle = trash
    ? driveFiles.length > 1
      ? `Move ${driveFiles.length} listed Drive files to Trash?`
      : 'Move this file to Google Drive Trash?'
    : approval?.toolName === 'drive.rename'
      ? 'Rename this Drive file?'
      : 'Move this Drive file?';
  const detailEntries = approval ? Object.entries(approval.details) : [];
  const revisionKeys = [
    'Active version',
    'Policy revision',
    'Instance revision',
  ];
  const revisions = detailEntries.filter(([key]) => revisionKeys.includes(key));
  const scope = approval?.details['Applies to'];
  const content = detailEntries.filter(
    ([key]) => !revisionKeys.includes(key) && key !== 'Applies to',
  );

  return (
    <Modal
      open={open}
      className="chat-approval"
      centered
      width={semantic ? 720 : 520}
      title={
        drive
          ? driveTitle
          : management
            ? approval?.toolName === 'management.freeze'
              ? 'Freeze self-management?'
              : 'Apply configuration to this Instance?'
            : filesystem
              ? 'Approve workspace changes?'
              : approval?.toolName.startsWith('harness.')
                ? 'Save this harness change?'
                : approval?.toolName === 'memory.forget'
                  ? 'Forget this learned-memory item?'
                  : semantic
                    ? 'Approve identity consolidation'
                    : 'Approve sensitive action'
      }
      okText={
        drive
          ? driveAction
          : management
            ? approval?.toolName === 'management.freeze'
              ? 'Freeze self-management'
              : 'Apply to this Instance'
            : 'Approve'
      }
      okButtonProps={{
        danger: trash,
        type: trash ? 'default' : undefined,
        style:
          trash && driveFiles.length > 0
            ? { color: token.colorErrorTextHover }
            : undefined,
        disabled: drive && driveFiles.length === 0,
      }}
      cancelText="Reject"
      onOk={onApprove}
      onCancel={onReject}
      destroyOnHidden
      mask={{ closable: false }}
      keyboard
    >
      {approval ? (
        <>
          {approval.decisionError ? (
            <Alert
              type="error"
              showIcon
              title="Decision was not confirmed"
              description={`${approval.decisionError} Retry a decision, or reconnect to recover its saved state.`}
            />
          ) : null}
          {drive && driveFiles.length === 0 ? (
            <Alert
              type="warning"
              showIcon
              title="Request fresh file details"
              description="This saved request has no complete target details. Reject it, inspect the files and request a fresh exact approval."
            />
          ) : null}
          <Typography.Paragraph className="chat-approval-summary">
            {approval.summary}
          </Typography.Paragraph>
          {scope !== undefined ? (
            <Alert
              className="chat-approval-scope"
              type="info"
              showIcon
              title="Applies to"
              description={scope}
            />
          ) : null}
          {revisions.length > 0 ? (
            <ul className="chat-approval-revisions">
              {revisions.map(([key, value]) => (
                <li key={key}>
                  <Typography.Text type="secondary">
                    {key}
                    {': '}
                  </Typography.Text>
                  <Tag>{value}</Tag>
                </li>
              ))}
            </ul>
          ) : null}
          {content.length > 0 ? (
            <div
              key={approval.approvalId}
              className="chat-approval-details"
              role="region"
              aria-label="Approval details"
              tabIndex={0}
            >
              {!semantic ? (
                <Descriptions
                  className={`chat-approval-fields${drive ? ' chat-approval-drive-fields' : ''}`}
                  size="small"
                  column={1}
                  layout={drive ? 'vertical' : 'horizontal'}
                  styles={{
                    label: { color: token.colorTextSecondary },
                    content: {
                      whiteSpace: 'pre-wrap',
                      overflowWrap: 'anywhere',
                    },
                  }}
                  items={content.map(([key, value]) => ({
                    key,
                    label: key,
                    children:
                      drive && key === 'Files' && driveFiles.length > 0 ? (
                        <ul
                          className="chat-approval-drive-files"
                          aria-label="Drive files in this request"
                        >
                          {driveFiles.map((file) => (
                            <li
                              key={file.fileId}
                              className="chat-approval-drive-file"
                            >
                              <Typography.Text strong>
                                {file.name}
                              </Typography.Text>
                              <Flex wrap gap={token.paddingXS} align="center">
                                <Tag>Version {file.expectedVersion}</Tag>
                                <Typography.Text
                                  type="secondary"
                                  className="chat-approval-drive-file-id"
                                >
                                  ID: {file.fileId}
                                </Typography.Text>
                              </Flex>
                            </li>
                          ))}
                        </ul>
                      ) : value.length > 0 ? (
                        value
                      ) : (
                        <Typography.Text
                          type="secondary"
                          aria-label="Not provided"
                        >
                          —
                        </Typography.Text>
                      ),
                  }))}
                />
              ) : (
                <ul className="chat-approval-list">
                  {content.map(([key, value]) => (
                    <li
                      className={`chat-approval-detail${key === 'Change' ? ' chat-approval-change' : ''}`}
                      style={
                        key === 'Change'
                          ? { borderRadius: token.borderRadiusLG }
                          : undefined
                      }
                      key={key}
                    >
                      <Typography.Text strong>
                        {key}
                        {': '}
                      </Typography.Text>
                      {key === 'Change' ? (
                        <ProposalText value={value} />
                      ) : (
                        <Typography.Text className="chat-approval-value">
                          {value}
                        </Typography.Text>
                      )}
                    </li>
                  ))}
                </ul>
              )}
            </div>
          ) : null}
        </>
      ) : null}
    </Modal>
  );
}
