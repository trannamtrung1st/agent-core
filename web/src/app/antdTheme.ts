import { theme, type ThemeConfig } from "antd";

// AntD's dark palette maps even explicit link seeds darker. Set readable text colors
// after that mapping, while retaining its primary fills, surfaces and other tokens.
const readableDarkAlgorithm: typeof theme.darkAlgorithm = (seed, map) => ({
  ...theme.darkAlgorithm(seed, map),
  colorLink: "#3c89e8",
  colorLinkHover: "#69b1ff",
  colorLinkActive: "#3c89e8"
});
export const antdTheme: ThemeConfig = {
  algorithm: readableDarkAlgorithm,
  token: { colorPrimary: "#1677ff" },
  components: {
    Tabs: { itemSelectedColor: "#3c89e8", itemHoverColor: "#69b1ff", itemActiveColor: "#3c89e8" }
  }
};
