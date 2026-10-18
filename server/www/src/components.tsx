// SPDX-License-Identifier: GPL-3.0-only
import { Alert, Button, Empty, Input, Space, Typography } from 'antd';
import { ReloadOutlined } from '@ant-design/icons';
import type { ReactNode } from 'react';

export function PageTitle({
  title,
  description,
  action,
}: {
  title: string;
  description: string;
  action?: ReactNode;
}) {
  return (
    <div className="page-heading">
      <div>
        <Typography.Title level={2}>{title}</Typography.Title>
        <Typography.Text type="secondary">{description}</Typography.Text>
      </div>
      {action}
    </div>
  );
}

export function Toolbar({
  placeholder,
  search,
  reload,
  loading,
  extra,
  disabled = false,
}: {
  placeholder: string;
  search: (value: string) => void;
  reload: () => void;
  loading: boolean;
  extra?: ReactNode;
  disabled?: boolean;
}) {
  return (
    <div className="table-toolbar">
      <Input.Search
        aria-label={placeholder}
        placeholder={placeholder}
        allowClear
        onSearch={search}
        disabled={disabled}
        style={{ maxWidth: 360 }}
      />
      <Space wrap>
        {extra}
        <Button icon={<ReloadOutlined />} onClick={reload} loading={loading} disabled={disabled}>
          刷新
        </Button>
      </Space>
    </div>
  );
}

export function ErrorBanner({ error, retry }: { error: string; retry?: () => void }) {
  return error ? (
    <Alert
      type="error"
      showIcon
      message={error}
      className="error-banner"
      action={
        retry && (
          <Button size="small" onClick={retry}>
            重试
          </Button>
        )
      }
    />
  ) : null;
}

export const tableLocale = {
  emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无记录" />,
};

export function formatDate(value?: string | null) {
  if (!value) return '—';
  const date = new Date(value);
  return Number.isNaN(date.getTime())
    ? '—'
    : new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'short', hour12: false }).format(
        date,
      );
}

export function bandwidth(value: number) {
  return `${new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 3 }).format(value / 1_000_000)} Mbps`;
}

export function duration(seconds: number) {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  return days ? `${days} 天 ${hours} 小时` : hours ? `${hours} 小时 ${minutes} 分钟` : `${minutes} 分钟`;
}
