import { theme, type ThemeConfig } from "antd";

// The product's deep navy palette is resolved here for native controls and
// AppShell's CSS projection, including portals. Semantic error/warning colors
// retain Ant Design's independently recognizable status palettes.
const readableDarkAlgorithm: typeof theme.darkAlgorithm = (seed, map) => ({
  ...theme.darkAlgorithm(seed, map),
  colorPrimary: "#245fbe",
  colorPrimaryHover: "#2867cc",
  colorPrimaryActive: "#1d4c9d",
  colorPrimaryBg: "#1b3455",
  colorPrimaryBgHover: "#23436b",
  colorPrimaryBorder: "#657d9c",
  colorPrimaryText: "#91bdff",
  colorPrimaryTextHover: "#b9d5ff",
  colorPrimaryTextActive: "#91bdff",
  colorLink: "#91bdff",
  colorLinkHover: "#b9d5ff",
  colorLinkActive: "#91bdff",
  colorBgLayout: "#0b1322",
  colorBgContainer: "#121e30",
  colorBgElevated: "#1b2b42",
  colorText: "#eaf1fa",
  colorTextSecondary: "#b1c1d7",
  colorTextTertiary: "#98abc4",
  colorTextPlaceholder: "#98abc4",
  colorBorder: "#657d9c",
  colorBorderSecondary: "#374b66"
});
export const antdTheme: ThemeConfig = {
  algorithm: readableDarkAlgorithm,
  token: { colorPrimary: "#245fbe", borderRadius: 8, colorTextPlaceholder: "#98abc4" },
  components: {
    Tabs: { itemSelectedColor: "#91bdff", itemHoverColor: "#b9d5ff", itemActiveColor: "#91bdff" }
  }
};
