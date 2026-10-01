import { CopyOutlined } from "@ant-design/icons";
import { useState, type ReactNode } from "react";
import { Button, Flex, theme } from "antd";

export function detailPopoverBodyStyle(token: ReturnType<typeof theme.useToken>["token"]) {
  return {
    width: "min(360px, calc(100vw - 32px))",
    maxHeight: "calc(40vh - 8px)",
    paddingBlockStart: token.paddingXXS,
    paddingInlineEnd: token.paddingXXS
  };
}

export function CopyDetailsButton({ copyText }: { copyText: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(copyText);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <Button
      icon={<CopyOutlined />}
      aria-label="Copy details"
      onClick={() => void copy()}
      style={{ alignSelf: "flex-start", flex: "0 0 auto" }}
    >
      <span role="status">{copied ? "Copied" : "Copy details"}</span>
    </Button>
  );
}

export function DetailPopoverBody({
  children,
  copyText,
  dataTestId,
  copyResetKey = 0
}: {
  children: ReactNode;
  copyText: string;
  dataTestId?: string;
  copyResetKey?: number;
}) {
  const { token } = theme.useToken();

  return (
    <Flex
      vertical
      gap={token.paddingXS}
      data-testid={dataTestId}
      className="ac-scroll-pane diagnostic-details-pane"
      style={detailPopoverBodyStyle(token)}
    >
      {children}
      <CopyDetailsButton key={copyResetKey} copyText={copyText} />
    </Flex>
  );
}
