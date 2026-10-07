import { createPortal } from "react-dom";
import { ConfigProvider, theme } from "antd";
import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { AppShell } from "./AppShell";
import { antdTheme } from "./antdTheme";

describe("AppShell theme", () => {
  it("keeps body portals on the active theme and follows theme changes", () => {
    function Surface({ primary }: { primary: string }) {
      return <ConfigProvider theme={{ ...antdTheme, token: { colorPrimary: primary } }}>
        <AppShell>{createPortal(<div data-testid="portal" />, document.body)}</AppShell>
      </ConfigProvider>;
    }
    const view = render(<Surface primary="#1677ff" />);
    expect(screen.getByTestId("portal").parentElement).toBe(document.body);
    const initial = document.documentElement.style.getPropertyValue("--ac-color-primary");
    expect(initial).toBe(theme.getDesignToken(antdTheme).colorPrimary);
    view.rerender(<Surface primary="#722ed1" />);
    expect(document.documentElement.style.getPropertyValue("--ac-color-primary"))
      .toBe(theme.getDesignToken({ ...antdTheme, token: { colorPrimary: "#722ed1" } }).colorPrimary);
    expect(document.documentElement.style.getPropertyValue("--ac-color-primary")).not.toBe(initial);
    view.unmount();
  });

  it("restores prior document tokens when the app unmounts", () => {
    const root = document.documentElement.style;
    root.setProperty("--ac-color-primary", "previous-theme");
    const view = render(<ConfigProvider theme={antdTheme}><AppShell>Chat</AppShell></ConfigProvider>);
    expect(root.getPropertyValue("--ac-color-primary")).not.toBe("previous-theme");
    view.unmount();
    expect(root.getPropertyValue("--ac-color-primary")).toBe("previous-theme");
    expect(root.getPropertyValue("--ac-space-section")).toBe("");
    root.removeProperty("--ac-color-primary");
  });
});
