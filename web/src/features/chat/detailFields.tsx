import { Typography, theme } from "antd";

export type DetailLine = {
  label: string;
  value: string;
  testId?: string;
};

export function DetailField({ label, value, testId }: DetailLine) {
  const { token } = theme.useToken();
  const valueStyle = {
    display: "block",
    userSelect: "text" as const,
    fontFamily: token.fontFamilyCode,
    fontSize: token.fontSizeSM,
    wordBreak: "break-all" as const
  };

  return (
    <div style={{ flex: "0 0 auto" }}>
      <Typography.Text type="secondary" style={{ display: "block", fontSize: token.fontSizeSM }}>
        {label}
      </Typography.Text>
      <Typography.Text style={valueStyle} data-testid={testId}>
        {value}
      </Typography.Text>
    </div>
  );
}

export function DetailFieldList({
  items,
  dataTestId
}: {
  items: DetailLine[];
  dataTestId?: string;
}) {
  const { token } = theme.useToken();

  if (items.length === 0) {
    return null;
  }

  return (
    <div
      data-testid={dataTestId}
      style={{ display: "flex", flexDirection: "column", gap: token.paddingXS }}
    >
      {items.map((item, index) => (
        <DetailField
          key={`${item.label}-${index}`}
          label={item.label}
          value={item.value}
          testId={item.testId}
        />
      ))}
    </div>
  );
}
