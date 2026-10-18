// SPDX-License-Identifier: GPL-3.0-only
import { useRef, useState, type ReactNode } from 'react';
import { App, Button, Space, Typography, type TableProps } from 'antd';
import { api, errorText, json } from './api';

type Identifier = string | number;

// A selection belongs to one response of one page, never to a hidden page or stale search.
export function useBatchSelection<T extends { id: Identifier }>(
  items: T[] | undefined,
  requestKey: string,
  disabled: boolean,
) {
  const [selection, setSelection] = useState<{
    items: T[] | undefined;
    requestKey: string;
    keys: Identifier[];
  }>({ items: undefined, requestKey: '', keys: [] });
  const keys = selection.items === items && selection.requestKey === requestKey ? selection.keys : [];
  const clear = () => setSelection({ items, requestKey, keys: [] });
  const selectPage = () => {
    if (!disabled)
      setSelection({ items, requestKey, keys: (items ?? []).slice(0, 100).map((item) => item.id) });
  };
  const rowSelection: NonNullable<TableProps<T>['rowSelection']> = {
    selectedRowKeys: keys,
    preserveSelectedRowKeys: false,
    columnWidth: 44,
    getCheckboxProps: () => ({ disabled }),
    onChange: (nextKeys) => {
      if (disabled) return;
      const currentIds = new Set(items?.map((item) => item.id));
      setSelection({
        items,
        requestKey,
        keys: nextKeys
          .filter(
            (key): key is Identifier =>
              (typeof key === 'string' || typeof key === 'number') && currentIds.has(key),
          )
          .slice(0, 100),
      });
    },
  };
  return { keys, clear, selectPage, rowSelection };
}

export function BatchToolbar({
  count,
  pageCount,
  disabled,
  clear,
  selectPage,
  children,
}: {
  count: number;
  pageCount: number;
  disabled: boolean;
  clear: () => void;
  selectPage: () => void;
  children: ReactNode;
}) {
  return (
    <div className="batch-toolbar" aria-label="当前页批量操作">
      <Space wrap size={[12, 6]}>
        <Typography.Text>
          已选 <strong>{count}</strong> 项
        </Typography.Text>
        <Button size="small" disabled={disabled || !pageCount || count === pageCount} onClick={selectPage}>
          选择当前页
        </Button>
        <Button size="small" disabled={disabled || !count} onClick={clear}>
          清空选择
        </Button>
        <Typography.Text type="secondary">仅当前页，最多 100 项</Typography.Text>
      </Space>
      <Space wrap size={[8, 8]}>
        {children}
      </Space>
    </div>
  );
}

export function useBatchAction(path: string, onSuccess: () => void) {
  const { modal, message } = App.useApp();
  const locked = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const run = async (
    body: { ids: Identifier[]; action: string; [key: string]: unknown },
    confirmation: { title: string; content: ReactNode; danger?: boolean },
  ) => {
    if (locked.current || !body.ids.length || body.ids.length > 100) return false;
    locked.current = true;
    setBusy(true);
    setError('');
    try {
      // Resolve only after explicit confirmation, keeping the entire batch locked
      // while the confirmation is open and the request is in flight.
      const accepted = await new Promise<boolean>((resolve) => {
        modal.confirm({
          ...confirmation,
          okText: '确认执行',
          cancelText: '取消',
          okButtonProps: { danger: confirmation.danger ?? true },
          onOk: () => resolve(true),
          onCancel: () => resolve(false),
        });
      });
      if (!accepted) return false;
      const result = await api<{ affected: number; skipped?: number }>(path, json('POST', body));
      message.success(
        `已处理 ${result.affected} 项${result.skipped ? `，跳过 ${result.skipped} 个已结束会话` : ''}`,
      );
      onSuccess();
      return true;
    } catch (reason) {
      setError(errorText(reason));
      return false;
    } finally {
      locked.current = false;
      setBusy(false);
    }
  };
  return { busy, error, run };
}
