import { useEffect, useRef } from "react";
import { Button, Flex, Spin, Typography, theme } from "antd";

export function DrawerListFooter({ loadingMore, hasMore, error, count, onLoadMore, onRetry }: {
  loadingMore: boolean; hasMore: boolean; error: string | null; count: number;
  onLoadMore: () => void; onRetry: () => void;
}) {
  const { token } = theme.useToken();
  const footer = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const body = footer.current?.closest(".ant-drawer-body");
    if (!body || !hasMore || loadingMore || error) return;
    const onScroll = () => {
      if (body.scrollHeight - body.scrollTop - body.clientHeight <= 80) onLoadMore();
    };
    body.addEventListener("scroll", onScroll, { passive: true });
    const observer = typeof IntersectionObserver !== "undefined" ? new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) onLoadMore();
    }, { root: body, rootMargin: "0px 0px 80px 0px" }) : null;
    if (footer.current) observer?.observe(footer.current);
    return () => { body.removeEventListener("scroll", onScroll); observer?.disconnect(); };
  }, [hasMore, loadingMore, error, onLoadMore]);
  return <Flex ref={footer} vertical align="center" gap={token.paddingXS} aria-live="polite" className="operational-list-footer">
    {loadingMore ? <Flex align="center" gap={token.paddingXS} role="status"><Spin size="small" />Loading more…</Flex>
      : error ? <Button onClick={onRetry}>Try again</Button>
      : hasMore ? <Button onClick={onLoadMore}>Load more</Button>
      : count > 0 ? <Typography.Text type="secondary">All items loaded</Typography.Text> : null}
  </Flex>;
}
