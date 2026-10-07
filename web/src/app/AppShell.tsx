import { useLayoutEffect, type CSSProperties, type ReactNode } from "react";
import { theme } from "antd";

export function AppShell({ children }: { children: ReactNode }) {
  const { token } = theme.useToken();
  // Product CSS consumes the same resolved dark tokens as Ant Design controls.
  const style = {
    "--ac-color-primary": token.colorPrimary,
    "--ac-color-primary-bg": token.colorPrimaryBg,
    "--ac-color-success": token.colorSuccess,
    "--ac-color-success-hover": token.colorSuccessTextHover,
    "--ac-color-success-bg": token.colorSuccessBg,
    "--ac-color-error": token.colorError,
    "--ac-color-text": token.colorText,
    "--ac-color-text-secondary": token.colorTextSecondary,
    "--ac-color-text-tertiary": token.colorTextTertiary,
    "--ac-color-layout": token.colorBgLayout,
    "--ac-color-container": token.colorBgContainer,
    "--ac-color-elevated": token.colorBgElevated,
    "--ac-color-border": token.colorBorderSecondary,
    "--ac-color-fill": token.colorFillTertiary,
    "--ac-color-bubble": token.colorFillSecondary,
    "--ac-space-compact": `${token.paddingXS}px`,
    "--ac-space-default": `${token.paddingSM}px`,
    "--ac-space-section": `${token.padding}px`,
    "--ac-radius-inline": `${token.borderRadiusSM}px`,
    "--ac-radius-control": `${token.borderRadius}px`,
    "--ac-radius-surface": `${token.borderRadiusLG}px`,
    "--ac-font-size-label": `${token.fontSizeSM}px`,
    fontFamily: token.fontFamily,
    fontSize: token.fontSize,
    lineHeight: token.lineHeight
  } as CSSProperties;

  // Drawers, dropdowns and dialogs portal to body and need the same product roles.
  useLayoutEffect(() => {
    const rootStyle = document.documentElement.style;
    const previous = new Map<string, string>();
    for (const [name, value] of Object.entries(style)) {
      if (!name.startsWith("--ac-")) continue;
      previous.set(name, rootStyle.getPropertyValue(name));
      rootStyle.setProperty(name, String(value));
    }
    return () => {
      for (const [name, value] of previous) {
        if (value) rootStyle.setProperty(name, value);
        else rootStyle.removeProperty(name);
      }
    };
  }, [token]);

  return <div className="app-shell" style={style}>{children}</div>;
}
