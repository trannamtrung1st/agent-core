import { useCallback, useState } from "react";
import { Input, Typography } from "antd";
import { SearchOutlined } from "@ant-design/icons";

/** Shared collection search; the enclosing collection owns outer insets and gaps. */
export function AdminCollectionToolbar({ label, value, onChange }: {
  label: string;
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <div className="admin-collection-toolbar">
      <Input
        aria-label={`Search ${label}`}
        placeholder={`Search ${label}`}
        prefix={<SearchOutlined />}
        allowClear
        value={value}
        onChange={(event) => onChange(event.target.value)}
      />
      <Typography.Text type="secondary" className="admin-table-scroll-hint">Scroll horizontally for more columns.</Typography.Text>
    </div>
  );
}

export const adminCollectionPagination = {
  defaultPageSize: 10,
  showSizeChanger: true,
  pageSizeOptions: [10, 20, 50],
  hideOnSinglePage: false,
  showTotal: (total: number) => `${total} results`,
  responsive: true
};

/** A changed search starts at the first page while retaining table filters and sorting. */
export function useAdminCollectionSearch() {
  const [search, updateSearch] = useState("");
  const [page, setPage] = useState(1);
  const setSearch = useCallback((value: string) => {
    updateSearch(value);
    setPage(1);
  }, []);
  return { search, setSearch, pagination: { ...adminCollectionPagination, current: page, onChange: setPage } };
}
