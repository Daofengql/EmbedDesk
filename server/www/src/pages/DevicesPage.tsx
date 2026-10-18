// SPDX-License-Identifier: GPL-3.0-only
import { useState } from 'react';
import { App, Badge, Button, Card, Dropdown, Form, Input, Modal, Space, Table, Tag, Typography } from 'antd';
import {
  DisconnectOutlined,
  EditOutlined,
  MoreOutlined,
  DeleteOutlined,
  StopOutlined,
  CheckCircleOutlined,
  UserSwitchOutlined,
  LinkOutlined,
  DownOutlined,
} from '@ant-design/icons';
import { api, errorText, json } from '../api';
import { BatchToolbar, useBatchAction, useBatchSelection } from '../batch';
import { ErrorBanner, formatDate, PageTitle, tableLocale, Toolbar } from '../components';
import { usePage } from '../hooks';
import type { Device } from '../types';
import { nameRules } from '../validation';
import { DeviceOwnerModal } from './DeviceOwnerModal';
import { BindDeviceModal } from './BindDeviceModal';
import { OwnerSelect } from './OwnerSelect';

export function DevicesPage({ admin, readonly }: { admin: boolean; readonly: boolean }) {
  const list = usePage<Device>('/devices');
  const [editing, setEditing] = useState<Device>();
  const [assigning, setAssigning] = useState<Device>();
  const [binding, setBinding] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [batchAssigning, setBatchAssigning] = useState(false);
  const [batchOwnerId, setBatchOwnerId] = useState<number>();
  const [form] = Form.useForm();
  const { modal, message } = App.useApp();
  const batch = useBatchAction('/devices/batch', () => {
    selection.clear();
    list.reload();
  });
  const batchLocked = batch.busy || batchAssigning;
  const selection = useBatchSelection(list.data?.items, list.requestKey, list.loading || batchLocked);
  const batchAction = (key: string) => {
    if (readonly || batch.busy || !selection.keys.length) return;
    if (key === 'assign') {
      if (!admin) return;
      setBatchOwnerId(undefined);
      setBatchAssigning(true);
      return;
    }
    const details: Record<string, { label: string; content: string }> = {
      enable: { label: '启用', content: '所选设备将允许重新接入中继。所属账号已禁用时，设备仍不能接入。' },
      disable: { label: '禁用', content: '所选设备的当前连接和会话会断开，并禁止重新接入中继。' },
      disconnect: {
        label: '断开',
        content: '所选设备的当前连接会断开，正在传输的数据会中断。客户端可能自动重新连接。',
      },
      delete: {
        label: '删除登记',
        content:
          '所选设备的连接会断开，登记将删除并释放设备配额。符合节点准入条件的设备仍可作为匿名设备重新注册并被绑定；如需阻止接入，请使用禁用设备。',
      },
    };
    const detail = details[key];
    if (!detail) return;
    void batch.run(
      { ids: selection.keys, action: key },
      {
        title: `批量${detail.label} ${selection.keys.length} 台设备？`,
        content: detail.content,
        danger: key !== 'enable',
      },
    );
  };
  const saveBatchOwner = async () => {
    if (!admin || batch.busy) return;
    const saved = await batch.run(
      { ids: selection.keys, action: 'assign', ownerId: batchOwnerId ?? null },
      {
        title: `${batchOwnerId === undefined ? '清空' : '修改'} ${selection.keys.length} 台设备的归属？`,
        content:
          batchOwnerId === undefined
            ? '所选设备会解除账号归属并断开连接，重连后每台使用默认带宽，且可被用户重新绑定。'
            : '所选设备会分配给指定账号并断开连接，重连后与新账号内的其他设备共享总带宽。若目标账号容量不足，整批操作都不会修改。',
      },
    );
    if (saved) setBatchAssigning(false);
  };
  const action = (device: Device, key: string) => {
    if (batchLocked) return;
    if (key === 'assign' && admin) {
      setAssigning(device);
      return;
    }
    if (key === 'edit') {
      form.setFieldsValue({ label: device.label });
      setError('');
      setEditing(device);
      return;
    }
    if (key === 'delete') {
      modal.confirm({
        title: '删除设备登记？',
        content:
          '删除后会断开当前连接并释放设备配额。符合节点准入条件的设备仍可作为匿名设备重新注册并被绑定；如需阻止接入，请使用禁用设备。',
        okText: '删除登记',
        cancelText: '取消',
        okButtonProps: { danger: true },
        onOk: async () => {
          try {
            await api('/devices/' + encodeURIComponent(device.id), json('DELETE'));
            message.success('设备登记已删除');
            list.reload();
          } catch (reason) {
            message.error(errorText(reason));
            throw reason;
          }
        },
      });
      return;
    }
    const disconnect = key === 'disconnect';
    modal.confirm({
      title: disconnect ? '断开设备连接？' : device.disabled ? '启用设备？' : '禁用设备？',
      content: disconnect
        ? `设备「${device.label || device.id}」的当前连接将断开，正在传输的数据会中断。客户端可能自动重新连接。`
        : device.disabled
          ? `允许设备「${device.label || device.id}」重新接入中继。`
          : `设备「${device.label || device.id}」将被禁止接入中继，现有连接也会断开。`,
      okText: disconnect ? '断开连接' : device.disabled ? '启用设备' : '禁用设备',
      cancelText: '取消',
      okButtonProps: { danger: disconnect || !device.disabled },
      onOk: async () => {
        try {
          await api(
            disconnect
              ? `/devices/${encodeURIComponent(device.id)}/disconnect`
              : `/devices/${encodeURIComponent(device.id)}`,
            json(
              disconnect ? 'POST' : 'PATCH',
              disconnect ? undefined : { label: device.label, disabled: !device.disabled },
            ),
          );
          message.success(disconnect ? '已请求断开设备' : device.disabled ? '设备已启用' : '设备已禁用');
          list.reload();
        } catch (reason) {
          message.error(errorText(reason));
          throw reason;
        }
      },
    });
  };
  const save = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values || !editing) return;
    setBusy(true);
    setError('');
    try {
      await api(
        `/devices/${encodeURIComponent(editing.id)}`,
        json('PATCH', { label: values.label?.trim() || '', disabled: editing.disabled }),
      );
      message.success('设备名称已更新');
      setEditing(undefined);
      list.reload();
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  };
  return (
    <>
      <PageTitle
        title="设备管理"
        action={
          !readonly && (
            <Button
              type="primary"
              icon={<LinkOutlined />}
              disabled={batchLocked}
              onClick={() => setBinding(true)}
            >
              绑定设备
            </Button>
          )
        }
        description={
          admin
            ? '管理所有设备的接入状态、当前连接，并分配所属用户。'
            : readonly
              ? '只读查看管理员分配给我的设备及连接状态。'
              : '输入自己的设备 ID 绑定匿名设备；已绑定设备共享账号总带宽配额。'
        }
      />
      <Card>
        <Toolbar
          placeholder="搜索设备 ID 或名称"
          search={(value) => {
            selection.clear();
            list.search(value);
          }}
          reload={() => {
            selection.clear();
            list.reload();
          }}
          loading={list.loading}
          disabled={batchLocked}
        />
        {!readonly && (
          <BatchToolbar
            count={selection.keys.length}
            pageCount={list.data?.items.length ?? 0}
            disabled={list.loading || batchLocked}
            clear={selection.clear}
            selectPage={selection.selectPage}
          >
            <Dropdown
              trigger={['click']}
              disabled={!selection.keys.length || batchLocked || list.loading}
              menu={{
                items: [
                  { key: 'enable', label: '批量启用', icon: <CheckCircleOutlined /> },
                  { key: 'disable', label: '批量禁用', danger: true, icon: <StopOutlined /> },
                  { key: 'disconnect', label: '批量断开连接', danger: true, icon: <DisconnectOutlined /> },
                  ...(admin
                    ? [{ key: 'assign', label: '分配或清空归属', icon: <UserSwitchOutlined /> }]
                    : []),
                  { type: 'divider' },
                  { key: 'delete', label: '批量删除设备登记', danger: true, icon: <DeleteOutlined /> },
                ],
                onClick: ({ key }) => batchAction(key),
              }}
            >
              <Button disabled={!selection.keys.length || batchLocked || list.loading} loading={batch.busy}>
                批量操作 <DownOutlined />
              </Button>
            </Dropdown>
          </BatchToolbar>
        )}
        <ErrorBanner error={list.error} retry={batchLocked ? undefined : list.reload} />
        <ErrorBanner error={batch.error} />
        <Table<Device>
          rowKey="id"
          dataSource={list.data?.items ?? []}
          loading={list.loading}
          locale={tableLocale}
          pagination={{ ...list.pagination, disabled: batchLocked }}
          rowSelection={readonly ? undefined : selection.rowSelection}
          scroll={{ x: admin ? 940 : 800 }}
          columns={[
            {
              title: '设备',
              key: 'name',
              render: (_, device) => (
                <div className="name-cell">
                  <Typography.Text strong>{device.label || '未命名设备'}</Typography.Text>
                  <Typography.Text type="secondary" copyable className="mono">
                    {device.id}
                  </Typography.Text>
                </div>
              ),
            },
            {
              title: '连接状态',
              key: 'status',
              width: 120,
              render: (_, device) => (
                <Badge
                  status={device.online ? 'success' : 'default'}
                  text={device.online ? '在线' : '离线'}
                />
              ),
            },
            {
              title: '接入权限',
              dataIndex: 'disabled',
              width: 110,
              render: (disabled: boolean) => (
                <Tag color={disabled ? 'red' : 'default'}>{disabled ? '已禁用' : '允许接入'}</Tag>
              ),
            },
            ...(admin
              ? [
                  {
                    title: '所属用户',
                    dataIndex: 'ownerName',
                    width: 130,
                    render: (value: string) => value || '匿名设备',
                  },
                ]
              : []),
            { title: '最近活动', dataIndex: 'lastSeenAt', width: 180, render: formatDate },
            ...(!readonly
              ? [
                  {
                    title: '操作',
                    key: 'actions',
                    fixed: 'right' as const,
                    width: 128,
                    render: (_: unknown, device: Device) => (
                      <Space size={2}>
                        <Button type="link" disabled={batchLocked} onClick={() => action(device, 'edit')}>
                          编辑
                        </Button>
                        <Dropdown
                          disabled={batchLocked}
                          trigger={['click']}
                          menu={{
                            items: [
                              { key: 'edit', label: '编辑名称', icon: <EditOutlined /> },
                              ...(admin
                                ? [{ key: 'assign', label: '分配所属用户', icon: <UserSwitchOutlined /> }]
                                : []),
                              {
                                key: 'disconnect',
                                label: '断开连接',
                                icon: <DisconnectOutlined />,
                                disabled: !device.online,
                                danger: true,
                              },
                              { type: 'divider' },
                              {
                                key: 'toggle',
                                label: device.disabled ? '启用设备' : '禁用设备',
                                icon: device.disabled ? <CheckCircleOutlined /> : <StopOutlined />,
                                danger: !device.disabled,
                              },
                              {
                                key: 'delete',
                                label: '删除设备登记',
                                icon: <DeleteOutlined />,
                                danger: true,
                              },
                            ],
                            onClick: ({ key }) => action(device, key),
                          }}
                        >
                          <Button
                            type="text"
                            disabled={batchLocked}
                            aria-label={`设备 ${device.label || device.id} 的更多操作`}
                            icon={<MoreOutlined />}
                          />
                        </Dropdown>
                      </Space>
                    ),
                  },
                ]
              : []),
          ]}
        />
      </Card>
      {admin && (
        <Modal
          title="批量分配设备归属"
          open={batchAssigning}
          onCancel={() => setBatchAssigning(false)}
          onOk={saveBatchOwner}
          okText="继续确认"
          cancelText="取消"
          confirmLoading={batch.busy}
          destroyOnHidden
          closable={!batch.busy}
          maskClosable={!batch.busy}
          cancelButtonProps={{ disabled: batch.busy }}
        >
          <Typography.Paragraph>
            将修改当前选中的 {selection.keys.length} 台设备。清空选择可解除这些设备的归属。
          </Typography.Paragraph>
          <ErrorBanner error={batch.error} />
          <Form layout="vertical">
            <Form.Item label="所属用户" extra="支持搜索用户名或显示名称，最多显示 100 条结果。">
              <OwnerSelect value={batchOwnerId} onChange={setBatchOwnerId} disabled={batch.busy} />
            </Form.Item>
          </Form>
          <Typography.Paragraph type="secondary" className="form-help">
            注册用户可以管理分配给自己的设备；访客仅能查看。归属变化会断开设备当前连接。
          </Typography.Paragraph>
        </Modal>
      )}
      <Modal
        title="编辑设备名称"
        open={!!editing}
        onCancel={() => setEditing(undefined)}
        onOk={save}
        okText="保存"
        cancelText="取消"
        confirmLoading={busy}
        destroyOnHidden
        closable={!busy}
        maskClosable={!busy}
        cancelButtonProps={{ disabled: busy }}
      >
        <ErrorBanner error={error} />
        <Form form={form} layout="vertical" requiredMark={false} preserve={false} disabled={busy}>
          <Form.Item label="设备 ID">
            <Typography.Text className="mono">{editing?.id}</Typography.Text>
          </Form.Item>
          <Form.Item label="设备名称" name="label" rules={nameRules}>
            <Input maxLength={160} placeholder="便于识别的名称（可选）" />
          </Form.Item>
        </Form>
      </Modal>
      {admin && (
        <DeviceOwnerModal device={assigning} onClose={() => setAssigning(undefined)} onSaved={list.reload} />
      )}
      {!readonly && (
        <BindDeviceModal open={binding} onClose={() => setBinding(false)} onSaved={list.reload} />
      )}
    </>
  );
}
