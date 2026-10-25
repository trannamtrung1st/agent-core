import { theme } from "antd";
import { describe, expect, it } from "vitest";
import { antdTheme } from "./antdTheme";

describe("antdTheme", () => {
  it("uses Ant Design dark algorithm with the product primary", () => {
    const baseline = theme.getDesignToken({ algorithm: theme.darkAlgorithm, token: antdTheme.token });
    const actual = theme.getDesignToken(antdTheme);
    expect(actual.colorPrimary).toBe(baseline.colorPrimary);
    expect(actual.colorBgContainer).toBe(baseline.colorBgContainer);
    expect(antdTheme.token?.colorPrimary).toBe("#1677ff");
  });
});

it("keeps small links readable against the dark container", () => {
  const tokens = theme.getDesignToken(antdTheme);
  function luminance(hex: string) {
    const rgb = [1, 3, 5].map(offset => parseInt(hex.slice(offset, offset + 2), 16) / 255).map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
  }
  const foreground = luminance(tokens.colorLink); const background = luminance(tokens.colorBgContainer);
  expect((Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05)).toBeGreaterThanOrEqual(4.5);
});
