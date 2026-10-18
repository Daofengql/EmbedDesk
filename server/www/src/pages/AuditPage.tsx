// SPDX-License-Identifier: GPL-3.0-only
import { Card, Table, Tag, Typography } from 'antd';
import { ErrorBanner, formatDate, PageTitle, tableLocale, Toolbar } from '../components';
import { usePage } from '../hooks';
import type { AuditEvent } from '../types';

const actionNames: Record<string, string> = {
  'auth.login': '账号登录',
  'auth.logout': '退出登录',
  'auth.password': '修改密码',
  'user.create': '创建用户',
  'user.register': '用户注册',
  'user.update': '更新用户',
  'device.update': '更新设备',
  'device.disconnect': '断开设备',
  'relay.update': '更新中继设置',
  'relay.token.refresh': '刷新节点准入令牌',
  'device.assign': '分配设备归属',
  'device.delete': '删除设备登记',
  'session.close': '关闭中继会话',
  'device.register': '设备注册',
};

export function AuditPage() {
  const list = usePage<AuditEvent>('/audit');
  return (
    <>
      <PageTitle title="审计日志" description="查看账号、设备、会话与中继节点的管理操作记录。" />
      <Card>
        <Toolbar
          placeholder="搜索操作者、操作或目标"
          search={list.search}
          reload={list.reload}
          loading={list.loading}
        />
        <ErrorBanner error={list.error} retry={list.reload} />
        <Table<AuditEvent>
          rowKey="id"
          dataSource={list.data?.items ?? []}
          loading={list.loading}
          locale={tableLocale}
          pagination={list.pagination}
          scroll={{ x: 750 }}
          columns={[
            { title: '发生时间', dataIndex: 'createdAt', width: 190, render: formatDate },
            { title: '操作者', dataIndex: 'actorName', width: 170, render: (name: string) => name || '系统' },
            {
              title: '操作',
              dataIndex: 'action',
              width: 180,
              render: (action: string) => <Tag>{actionNames[action] || action}</Tag>,
            },
            {
              title: '操作目标',
              dataIndex: 'target',
              render: (target: string) => (
                <Typography.Text className="break-word">{target || '—'}</Typography.Text>
              ),
            },
          ]}
        />
      </Card>
    </>
  );
}
