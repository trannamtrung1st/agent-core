import { theme, type ThemeConfig } from "antd";

// The product's dark evergreen palette is resolved here for native controls and
// AppShell's CSS projection, including portals. Semantic error/warning colors
// retain Ant Design's independently recognizable status palettes.
const readableDarkAlgorithm: typeof theme.darkAlgorithm = (seed, map) => ({
  ...theme.darkAlgorithm(seed, map),
  colorPrimaryHover: "#2f8064",
  colorPrimaryActive: "#1d624a",
  colorPrimaryBg: "#203e32",
  colorPrimaryBgHover: "#294b3e",
  colorPrimaryBorder: "#4b9e7c",
  colorPrimaryText: "#86d5af",
  colorPrimaryTextHover: "#b3e8cd",
  colorPrimaryTextActive: "#86d5af",
  colorLink: "#86d5af",
  colorLinkHover: "#b3e8cd",
  colorLinkActive: "#86d5af",
  colorBgLayout: "#0e1613",
  colorBgContainer: "#16201c",
  colorBgElevated: "#202d26",
  colorText: "#e7eee9",
  colorTextSecondary: "#aebdb3",
  colorTextTertiary: "#92a499",
  colorTextPlaceholder: "#92a499",
  colorBorder: "#40564a",
  colorBorderSecondary: "#2b3e33"
});
export const antdTheme: ThemeConfig = {
  algorithm: readableDarkAlgorithm,
  token: { colorPrimary: "#27765b", borderRadius: 8, colorTextPlaceholder: "#92a499" },
  components: {
    Tabs: { itemSelectedColor: "#86d5af", itemHoverColor: "#b3e8cd", itemActiveColor: "#86d5af" }
  }
};
