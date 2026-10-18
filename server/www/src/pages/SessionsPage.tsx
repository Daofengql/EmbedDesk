// SPDX-License-Identifier: GPL-3.0-only
import { App, Badge, Button, Card, Table, Tag, Typography } from 'antd';
import { api, errorText, json } from '../api';
import { BatchToolbar, useBatchAction, useBatchSelection } from '../batch';
import { ErrorBanner, formatDate, PageTitle, tableLocale, Toolbar } from '../components';
import { usePage } from '../hooks';
import type { RelaySession } from '../types';

const stages: Record<string, string> = {
  '0': '待握手',
  '1': '等待证明',
  '2': '等待确认',
  '3': '已连接',
  pending: '等待确认',
  waiting: '等待确认',
  accepted: '已接受',
  active: '转发中',
  connected: '已连接',
  connecting: '连接中',
  open: '已连接',
  relaying: '转发中',
};

export function SessionsPage({ readonly }: { readonly: boolean }) {
  const list = usePage<RelaySession>('/sessions');
  const { modal, message } = App.useApp();
  const batch = useBatchAction('/sessions/batch', () => {
    selection.clear();
    list.reload();
  });
  const selection = useBatchSelection(list.data?.items, list.requestKey, list.loading || batch.busy);
  const closeBatch = () => {
    if (readonly || batch.busy) return;
    void batch.run(
      { ids: selection.keys, action: 'close' },
      {
        title: `关闭 ${selection.keys.length} 个中继会话？`,
        content:
          '所选会话将被关闭，设备之间正在进行的数据传输会中断。已经结束的会话将跳过，并在完成后显示数量。',
      },
    );
  };
  const closeSession = (session: RelaySession) =>
    modal.confirm({
      title: '关闭中继会话？',
      content: `会话「${session.id}」将被关闭，设备之间正在进行的数据传输会中断。`,
      okText: '关闭会话',
      cancelText: '取消',
      okButtonProps: { danger: true },
      onOk: async () => {
        try {
          await api(`/sessions/${encodeURIComponent(session.id)}/close`, json('POST', {}));
          message.success('会话已关闭');
          list.reload();
        } catch (reason) {
          message.error(errorText(reason));
          throw reason;
        }
      },
    });
  return (
    <>
      <PageTitle title="中继会话" description="查看设备之间建立的中继连接和当前阶段。" />
      <Card>
        <Toolbar
          placeholder="搜索会话或设备 ID"
          search={(value) => {
            selection.clear();
            list.search(value);
          }}
          reload={() => {
            selection.clear();
            list.reload();
          }}
          loading={list.loading}
          disabled={batch.busy}
        />
        {!readonly && (
          <BatchToolbar
            count={selection.keys.length}
            pageCount={list.data?.items.length ?? 0}
            disabled={list.loading || batch.busy}
            clear={selection.clear}
            selectPage={selection.selectPage}
          >
            <Button
              danger
              disabled={!selection.keys.length || batch.busy || list.loading}
              loading={batch.busy}
              onClick={closeBatch}
            >
              批量关闭会话
            </Button>
          </BatchToolbar>
        )}
        <ErrorBanner error={list.error} retry={batch.busy ? undefined : list.reload} />
        <ErrorBanner error={batch.error} />
        <Table<RelaySession>
          rowKey="id"
          dataSource={list.data?.items ?? []}
          loading={list.loading}
          locale={tableLocale}
          pagination={{ ...list.pagination, disabled: batch.busy }}
          rowSelection={readonly ? undefined : selection.rowSelection}
          scroll={{ x: readonly ? 930 : 1030 }}
          columns={[
            {
              title: '会话 ID',
              dataIndex: 'id',
              width: 200,
              render: (value: string) => (
                <Typography.Text className="mono break-word" copyable>
                  {value}
                </Typography.Text>
              ),
            },
            {
              title: '连接方设备',
              dataIndex: 'originDeviceId',
              width: 190,
              render: (value: string) => (
                <Typography.Text className="mono break-word">{value}</Typography.Text>
              ),
            },
            {
              title: '目标设备',
              dataIndex: 'targetDeviceId',
              width: 190,
              render: (value: string) => (
                <Typography.Text className="mono break-word">{value}</Typography.Text>
              ),
            },
            {
              title: '阶段',
              dataIndex: 'stage',
              width: 120,
              render: (value: number | string) => <Tag>{stages[String(value)] || value || '—'}</Tag>,
            },
            {
              title: '授权',
              dataIndex: 'accepted',
              width: 110,
              render: (value: boolean) => (
                <Badge status={value ? 'success' : 'processing'} text={value ? '已接受' : '待接受'} />
              ),
            },
            { title: '建立时间', dataIndex: 'createdAt', width: 180, render: formatDate },
            ...(!readonly
              ? [
                  {
                    title: '操作',
                    key: 'actions',
                    width: 100,
                    fixed: 'right' as const,
                    render: (_: unknown, session: RelaySession) => (
                      <Button type="link" danger disabled={batch.busy} onClick={() => closeSession(session)}>
                        关闭
                      </Button>
                    ),
                  },
                ]
              : []),
          ]}
        />
      </Card>
    </>
  );
}
