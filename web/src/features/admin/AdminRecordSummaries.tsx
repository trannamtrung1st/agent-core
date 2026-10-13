import { Alert, Flex, List, Tag, Typography } from "antd";
import { useCallback, useEffect, useRef, useState } from "react";
import { listAdminPublicationResources, type AdminDefinitionPublicationResource } from "../../services/adminApi";
import { formatAdminLoadError } from "./adminErrors";
import { AdminRetryAction } from "./adminFailure";

export function PublicationResourcesSummary({
  definitionId,
  version
}: {
  definitionId: string;
  version: number;
}) {
  const [items, setItems] = useState<AdminDefinitionPublicationResource[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<{ message: string; diagnosticId?: string } | null>(null);
  const requestGenerationRef = useRef(0);

  const load = useCallback(
    async (generation: number) => {
      setLoading(true);
      setError(null);
      try {
        const resources = await listAdminPublicationResources(definitionId, version);
        if (generation !== requestGenerationRef.current) {
          return;
        }

        setItems(resources);
      } catch (loadError) {
        if (generation !== requestGenerationRef.current) {
          return;
        }

        setItems([]);
        setError(formatAdminLoadError(loadError));
      } finally {
        if (generation === requestGenerationRef.current) {
          setLoading(false);
        }
      }
    },
    [definitionId, version]
  );

  const reload = useCallback(() => {
    const generation = ++requestGenerationRef.current;
    void load(generation);
  }, [load]);

  useEffect(() => {
    reload();
  }, [reload]);

  if (loading) {
    return <Typography.Text type="secondary">Loading resources…</Typography.Text>;
  }
  if (error) {
    return (
      <Alert
        type="error"
        showIcon
        title={error.message}
        action={<AdminRetryAction onRetry={reload} diagnosticId={error.diagnosticId} />}
      />
    );
  }
  if (items.length === 0) {
    return <Typography.Text type="secondary">No bound resources.</Typography.Text>;
  }
  return (
    <List
      size="small"
      dataSource={items}
      renderItem={(item) => (
        <List.Item>
          {item.logicalPath} · {item.kind} · {item.contentSha256.slice(0, 12)}…
        </List.Item>
      )}
    />
  );
}

export function InstanceIdentityTags({
  lifecycle,
  definitionStatus
}: {
  lifecycle: string;
  definitionStatus?: string;
}) {
  return (
    <Flex gap={8} wrap="wrap">
      <Tag color="blue">Agent Instance</Tag>
      <Tag>{lifecycle}</Tag>
      {definitionStatus ? <Tag>{definitionStatus}</Tag> : null}
    </Flex>
  );
}
