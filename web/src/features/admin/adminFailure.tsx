import { Button, Flex, theme, Typography } from "antd";
import { describeAdminError } from "./adminErrors";
import { DiagnosticDetails } from "../chat/DiagnosticDetails";

export function showAdminFailure(
  messageApi: { error: (config: { content: React.ReactNode; duration?: number }) => void },
  error: unknown,
  fallback: string
) {
  const notice = describeAdminError(error, fallback);
  messageApi.error({
    content: <AdminErrorNotice message={notice.message} diagnosticId={notice.diagnosticId} />,
    duration: notice.diagnosticId ? 8 : 3
  });
  return notice;
}

export function AdminErrorNotice({
  message,
  diagnosticId,
  tone = "plain"
}: {
  message: string;
  diagnosticId?: string | null;
  tone?: "plain" | "danger";
}) {
  const { token } = theme.useToken();
  const text = tone === "danger"
    ? <Typography.Text type="danger">{message}</Typography.Text>
    : <span>{message}</span>;
  if (!diagnosticId) {
    return text;
  }

  return (
    <Flex gap={token.paddingXS} align="center" wrap="wrap">
      {text}
      <DiagnosticDetails fields={{ diagnosticId }} />
    </Flex>
  );
}

export function AdminRetryAction({
  onRetry,
  diagnosticId,
  retryLabel = "Retry"
}: {
  onRetry: () => void;
  diagnosticId?: string | null;
  retryLabel?: string;
}) {
  const { token } = theme.useToken();
  return (
    <Flex gap={token.paddingXS} align="center" wrap="wrap">
      {diagnosticId ? <DiagnosticDetails fields={{ diagnosticId }} /> : null}
      <Button size="small" aria-label={retryLabel === "Retry" ? undefined : retryLabel} onClick={onRetry}>
        Retry
      </Button>
    </Flex>
  );
}
