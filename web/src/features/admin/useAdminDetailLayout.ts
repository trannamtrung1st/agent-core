import { Grid, type DescriptionsProps } from "antd";

/** Shared label alignment for Admin record, provenance and configuration details. */
export function useAdminDetailLayout(): Pick<DescriptionsProps, "layout" | "styles"> {
  const screens = Grid.useBreakpoint();
  return {
    layout: screens.md ? "horizontal" : "vertical",
    styles: {
      label: { width: screens.md ? "12rem" : undefined, verticalAlign: "top" },
      content: { overflowWrap: "anywhere", whiteSpace: "normal" }
    }
  };
}
