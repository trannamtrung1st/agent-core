import type { ReactNode } from "react";
import type { HookAPI } from "antd/es/modal/useModal";

export interface ConfirmActionOptions {
  title: string;
  content?: ReactNode;
  okText?: string;
  cancelText?: string;
  danger?: boolean;
  onOk?: () => void | Promise<void>;
  afterClose?: () => void;
}

/** Centered confirmation modal — use instead of Popconfirm for destructive or high-friction actions. */
export function confirmAction(modal: HookAPI, options: ConfirmActionOptions): void {
  const { title, content, okText, cancelText, danger, onOk, afterClose } = options;
  modal.confirm({
    title,
    content,
    okText: okText ?? "Confirm",
    cancelText: cancelText ?? "Cancel",
    centered: true,
    mask: { closable: true },
    ...(danger ? { okType: "danger", okButtonProps: { danger: true } } : {}),
    onOk,
    afterClose
  });
}
