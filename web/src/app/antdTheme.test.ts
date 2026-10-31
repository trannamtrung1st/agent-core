import { theme } from "antd";
import { describe, expect, it } from "vitest";
import { antdTheme } from "./antdTheme";

describe("antdTheme", () => {
  it("uses Ant Design dark algorithm with the product primary", () => {
    const actual = theme.getDesignToken(antdTheme);
    expect(actual.colorPrimary).toBe("#245fbe");
    expect(actual.colorBgContainer).toBe("#121e30");
    expect(antdTheme.token?.colorPrimary).toBe("#245fbe");
  });
});

it("keeps working text and primary action states above the small-text contrast floor", () => {
  const tokens = theme.getDesignToken(antdTheme);
  function luminance(hex: string) {
    if (hex.length === 4) hex = '#' + [...hex.slice(1)].map(character => character.repeat(2)).join('');
    const rgb = [1, 3, 5].map(offset => parseInt(hex.slice(offset, offset + 2), 16) / 255).map(value => value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4);
    return rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
  }
  for (const [name, foreground, background] of [
    ['body', tokens.colorText, tokens.colorBgContainer],
    ['secondary', tokens.colorTextSecondary, tokens.colorBgElevated],
    ['tertiary', tokens.colorTextTertiary, tokens.colorBgElevated],
    ['placeholder', tokens.colorTextPlaceholder, tokens.colorBgElevated],
    ['link', tokens.colorLink, tokens.colorBgContainer],
    ['link hover', tokens.colorLinkHover, tokens.colorBgElevated],
    ['link active', tokens.colorLinkActive, tokens.colorPrimaryBg],
    ['selected text', tokens.colorText, tokens.colorPrimaryBg],
    ['primary', tokens.colorTextLightSolid, tokens.colorPrimary],
    ['primary hover', tokens.colorTextLightSolid, tokens.colorPrimaryHover],
    ['primary active', tokens.colorTextLightSolid, tokens.colorPrimaryActive],
  ]) {
    const front = luminance(foreground), back = luminance(background);
    expect((Math.max(front, back) + 0.05) / (Math.min(front, back) + 0.05), name).toBeGreaterThanOrEqual(4.5);
  }
});
