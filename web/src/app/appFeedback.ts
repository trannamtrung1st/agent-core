import type { ConfigOptions } from "antd/es/message/interface";

/** Shared native Ant Design message layout for Chat and Admin. */
export const appMessageConfig: ConfigOptions = {
  duration: 3,
  maxCount: 3,
  top: "auto",
  pauseOnHover: true,
  classNames: { list: "app-feedback-list" },
  styles: {
    list: {
      bottom: "calc(6 * var(--ac-space-section, 16px) + env(safe-area-inset-bottom, 0px))",
      height: "auto",
      maxHeight: "50dvh",
      gap: "var(--ac-space-compact, 8px)",
      padding: "0 var(--ac-space-section, 16px)"
    },
    // Flow layout keeps a bottom-anchored stack correct when wrapped text resizes.
    // Native absolute notices otherwise retain their previous measured height.
    listContent: { display: "contents" },
    root: {
      position: "relative",
      inset: "auto",
      transform: "none",
      marginInline: "auto",
      flexShrink: 0,
      maxWidth: "min(40rem, calc(100vw - 2 * var(--ac-space-section, 16px)))"
    },
    wrapper: { alignItems: "flex-start" },
    icon: { flexShrink: 0, marginBlockStart: "0.2em" },
    title: { minWidth: 0, whiteSpace: "normal", overflowWrap: "anywhere", textAlign: "start" }
  }
};
