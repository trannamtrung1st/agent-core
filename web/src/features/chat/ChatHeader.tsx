import { useEffect, useRef, useState, type ReactNode, type Ref } from "react";
import { App as AntApp, Badge, Button, Flex, Popover, Tooltip, Typography } from "antd";
import { CalendarOutlined, CodeOutlined, FolderOpenOutlined, InboxOutlined, PoweroffOutlined, MoreOutlined } from "@ant-design/icons";
import { confirmAction } from "../../app/confirmAction";
import { formatChatTime } from "./chatTime";

export function ChatHeader({
  title,
  subtitle,
  timestamp,
  status,
  additionalActions, workActions, workButtonRefs = [],
  sessionsToggle,
  speechLocale,
  onSchedules,
  onBackgroundWork, backgroundButtonRef,
  onSandbox, onFiles, filesButtonRef,
  sandboxButtonRef,
  attentionCount = 0,
  onEnd,
  inSession, endDisabled = false
}: {
  title: string;
  subtitle?: string | null;
  timestamp?: string | null;
  status?: ReactNode;
  additionalActions?: ReactNode;
  workActions?: ReactNode;
  workButtonRefs?: Ref<HTMLButtonElement>[];
  sessionsToggle?: ReactNode;
  speechLocale?: ReactNode;
  onSchedules?: () => void;
  onBackgroundWork?: () => void; backgroundButtonRef?: Ref<HTMLButtonElement>;
  onSandbox?: () => void;
  sandboxButtonRef?: Ref<HTMLButtonElement>;
  onFiles?: () => void; filesButtonRef?: Ref<HTMLButtonElement>;
  attentionCount?: number;
  onEnd: () => void;
  inSession: boolean;
  endDisabled?: boolean;
}) {
  const { modal } = AntApp.useApp();
  const [toolsOpen, setToolsOpen] = useState(false);
  const toolsButton = useRef<HTMLButtonElement | null>(null);
  const toolsPanel = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    if (!toolsOpen) return;
    const frame = requestAnimationFrame(() => toolsPanel.current?.querySelector<HTMLButtonElement>('button')?.focus());
    return () => cancelAnimationFrame(frame);
  }, [toolsOpen]);
  const timeLabel = timestamp ? formatChatTime(timestamp) : null;

  return (
    <Flex align="center" justify="space-between" gap={12} className="chat-header-inner">
      <Flex align="center" gap={12} className="chat-header-identity">
        {sessionsToggle}
        <div className="chat-header-copy">
          <Flex align="baseline" gap={8} className="chat-header-title-row">
            <Typography.Title level={1} className="chat-header-title">
              {title}
            </Typography.Title>
            {timeLabel && timestamp ? (
              <Typography.Text type="secondary" className="chat-header-time">
                <time dateTime={timestamp}>{timeLabel}</time>
              </Typography.Text>
            ) : null}
          </Flex>
          {subtitle || status ? (
            <Flex align="center" gap={8} className="chat-header-details">
              {subtitle ? (
                <Tooltip title={subtitle}><Typography.Text type="secondary" className="chat-header-subtitle single-line-ellipsis">
                  {subtitle}
                </Typography.Text></Tooltip>
              ) : null}
              {status}
            </Flex>
          ) : null}
        </div>
      </Flex>
      {speechLocale || inSession || onBackgroundWork || additionalActions ? (
        <Flex align="center" wrap gap={8} className="chat-header-actions">
          {speechLocale}
          <Flex align="center" gap={8} className="chat-header-session-actions">
            {(onFiles || onSandbox || onBackgroundWork || inSession || workActions) && <Popover trigger="click" placement="bottomRight" open={toolsOpen} onOpenChange={setToolsOpen} content={<Flex ref={toolsPanel} vertical gap="var(--ac-space-compact)" className="chat-tools" onKeyDown={event => { if (event.key === 'Escape') { event.stopPropagation(); setToolsOpen(false); toolsButton.current?.focus(); } }} onClick={event => { if ((event.target as HTMLElement).closest('button:not(:disabled)')) setToolsOpen(false); }}>
              <Typography.Text strong>Conversation tools</Typography.Text>
            {onFiles && <Tooltip title="Session files"><Button type="text" size="small" className="chat-tool-action" aria-label="Session files" icon={<FolderOpenOutlined aria-hidden />} onClick={onFiles}>Files</Button></Tooltip>}
            {onSandbox && <Tooltip title="Session sandbox"><Button type="text" size="small" className="chat-tool-action" aria-label="Session sandbox"  icon={<CodeOutlined />} onClick={onSandbox}>Sandbox</Button></Tooltip>}
            {onBackgroundWork ? (
              <Badge count={attentionCount} size="small" offset={[-4, 4]}>
                <Tooltip title="Background work">
                  <Button
                    type="text"
                    size="small"
                    className="chat-tool-action chat-header-background-work"
                    aria-label={attentionCount > 0 ? `Background work, ${attentionCount} need attention` : "Background work"}
                    icon={<InboxOutlined />}
                    onClick={onBackgroundWork}
                  >Background work</Button>
                </Tooltip>
              </Badge>
            ) : null}
            {inSession && onSchedules ? (
              <Tooltip title="Automations">
                <Button
                  type="text"
                  size="small"
                  className="chat-tool-action"
                  aria-label="Automations"
                  icon={<CalendarOutlined />}
                  onClick={onSchedules}
                >Automations</Button>
              </Tooltip>
            ) : null}
            {inSession ? (
              <Tooltip title={endDisabled ? "Wait for the response to finish, or stop it first." : "End conversation"}>
                <Button
                  type="text"
                  size="small"
                  className="chat-tool-action"
                  aria-label="End"
                  disabled={endDisabled}
                  icon={<PoweroffOutlined />}
                  onClick={() => {
                    confirmAction(modal, {
                      title: "End this conversation?",
                      content: "This conversation will become read-only and cannot be resumed.",
                      okText: "End",
                      danger: true,
                      onOk: onEnd
                    });
                  }}
                >End conversation</Button>
              </Tooltip>
            ) : null}
              {workActions}
            </Flex>}>
              <Badge dot={attentionCount > 0}><Button icon={<MoreOutlined aria-hidden />} aria-label={attentionCount > 0 ? `Conversation tools, ${attentionCount} need attention` : 'Conversation tools'} ref={node => {
                const button = node instanceof HTMLButtonElement ? node : null;
                toolsButton.current = button;
                for (const ref of [filesButtonRef, sandboxButtonRef, backgroundButtonRef, ...workButtonRefs]) {
                  if (typeof ref === 'function') ref(button);
                  else if (ref) ref.current = button;
                }
              }}>Tools</Button></Badge>
            </Popover>}

            {additionalActions}
          </Flex>
        </Flex>
      ) : null}
    </Flex>
  );
}
