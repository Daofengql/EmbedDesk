// SPDX-License-Identifier: GPL-3.0-only
import { useEffect, useState } from 'react';
import {
  App,
  Alert,
  Button,
  Card,
  Col,
  Dropdown,
  Form,
  Input,
  InputNumber,
  Modal,
  Row,
  Select,
  Space,
  Switch,
  Table,
  Tag,
  Typography,
} from 'antd';
import { DownOutlined, EditOutlined, PlusOutlined } from '@ant-design/icons';
import { api, errorText, json } from '../api';
import { BatchToolbar, useBatchAction, useBatchSelection } from '../batch';
import { bandwidth, ErrorBanner, formatDate, PageTitle, tableLocale, Toolbar } from '../components';
import { usePage } from '../hooks';
import type { RelaySettings, User } from '../types';
import { nameRules, passwordRules, roleNames, usernameRules } from '../validation';

interface UserValues {
  username: string;
  displayName: string;
  password?: string;
  role: 'admin' | 'user' | 'guest';
  disabled: boolean;
  maxDevices: number;
  bandwidthMbps: number;
}

export function UsersPage({ currentUser }: { currentUser: User }) {
  const list = usePage<User>('/users');
  const [editing, setEditing] = useState<User | null>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [loadingDefault, setLoadingDefault] = useState(false);
  const [defaultError, setDefaultError] = useState('');
  const [defaultRevision, setDefaultRevision] = useState(0);
  const [batchSetting, setBatchSetting] = useState<'bandwidth' | 'deviceLimit'>();
  const [batchForm] = Form.useForm<{ value: number }>();
  const [form] = Form.useForm<UserValues>();
  const { message, modal } = App.useApp();
  const batch = useBatchAction('/users/batch', () => {
    selection.clear();
    list.reload();
  });
  const batchLocked = batch.busy || !!batchSetting;
  const selection = useBatchSelection(list.data?.items, list.requestKey, list.loading || batchLocked);
  const batchAction = (action: string) => {
    if (batch.busy || !selection.keys.length) return;
    if (action === 'bandwidth' || action === 'deviceLimit') {
      batchForm.resetFields();
      setBatchSetting(action);
      return;
    }
    void batch.run(
      { ids: selection.keys, action },
      {
        title: `${action === 'enable' ? '启用' : '禁用'} ${selection.keys.length} 个用户？`,
        content:
          action === 'enable'
            ? '所选账号将恢复登录与设备接入权限。已被单独禁用的设备仍保持禁用。'
            : '所选账号将无法登录，关联设备会立即断开并禁止重新接入。至少需要保留一个可用的管理员账号。',
        danger: action !== 'enable',
      },
    );
  };
  const saveBatchSetting = async () => {
    if (!batchSetting || batch.busy) return;
    const values = await batchForm.validateFields().catch(() => null);
    if (!values) return;
    const isBandwidth = batchSetting === 'bandwidth';
    const saved = await batch.run(
      {
        ids: selection.keys,
        action: batchSetting,
        ...(isBandwidth
          ? { bandwidthBitsPerSecond: Math.round(values.value * 1_000_000) }
          : { maxDevices: values.value }),
      },
      {
        title: `修改 ${selection.keys.length} 个用户的${isBandwidth ? '总带宽配额' : '设备数量上限'}？`,
        content: isBandwidth
          ? `每个所选账号的总带宽将设为 ${values.value} Mbps，关联设备会断开。重连后各账号内的所有设备共享该账号的双向合计配额。`
          : `每个所选账号的设备数量上限将设为 ${values.value} 台。现有已绑定设备保持归属和连接；达到或超过新上限后，不能新增绑定设备。`,
        danger: isBandwidth,
      },
    );
    if (saved) setBatchSetting(undefined);
  };
  const open = editing !== undefined;
  useEffect(() => {
    if (!open) return;
    setError('');
    setDefaultError('');
    setLoadingDefault(false);
    form.resetFields();
    form.setFieldsValue(
      editing
        ? { ...editing, password: undefined, bandwidthMbps: editing.bandwidthBitsPerSecond / 1_000_000 }
        : {
            username: '',
            displayName: '',
            role: 'user',
            disabled: false,
            maxDevices: 16,
            bandwidthMbps: undefined,
          },
    );
  }, [editing, open, form]);
  useEffect(() => {
    if (!open || editing !== null) return;
    const controller = new AbortController();
    setLoadingDefault(true);
    setDefaultError('');
    api<RelaySettings>('/relay', { signal: controller.signal })
      .then((settings) => {
        if (!controller.signal.aborted)
          form.setFieldValue('bandwidthMbps', settings.defaultBandwidthBitsPerSecond / 1_000_000);
      })
      .catch((reason) => {
        if (!controller.signal.aborted) setDefaultError(`无法读取节点默认带宽：${errorText(reason)}`);
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoadingDefault(false);
      });
    return () => controller.abort();
  }, [editing, open, form, defaultRevision]);
  const save = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values) return;
    if (
      editing &&
      ((values.disabled && !editing.disabled) ||
        values.role !== editing.role ||
        values.password ||
        Math.round(values.bandwidthMbps * 1_000_000) !== editing.bandwidthBitsPerSecond)
    ) {
      const accepted = await modal.confirm({
        title: '确认修改用户设置？',
        content: `即将更新 ${editing.username} 的设置。修改带宽会断开该用户关联设备，重连后所有关联设备共享新的账号总带宽配额；禁用账号将立即断开关联设备，并阻止后续登录和设备接入。`,
        okText: '确认修改',
        cancelText: '取消',
        okButtonProps: { danger: true },
      });
      if (!accepted) return;
    }
    setBusy(true);
    setError('');
    try {
      const body = {
        displayName: values.displayName?.trim() || editing?.username || values.username.trim(),
        role: values.role,
        maxDevices: values.maxDevices,
        bandwidthBitsPerSecond: Math.round(values.bandwidthMbps * 1_000_000),
        ...(values.password ? { password: values.password } : {}),
      };
      await api(
        editing ? `/users/${editing.id}` : '/users',
        json(
          editing ? 'PATCH' : 'POST',
          editing ? { ...body, disabled: values.disabled } : { ...body, username: values.username.trim() },
        ),
      );
      message.success(editing ? '用户已更新' : '用户已创建');
      setEditing(undefined);
      form.resetFields();
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
        title="用户管理"
        description="管理账号角色、设备数量与账号总带宽配额。"
        action={
          <Button
            type="primary"
            icon={<PlusOutlined />}
            disabled={batchLocked}
            onClick={() => setEditing(null)}
          >
            创建用户
          </Button>
        }
      />
      <Card>
        <Toolbar
          placeholder="搜索用户名或显示名称"
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
                { key: 'enable', label: '批量启用' },
                { key: 'disable', label: '批量禁用', danger: true },
                { type: 'divider' },
                { key: 'bandwidth', label: '设置账号总带宽' },
                { key: 'deviceLimit', label: '设置设备数量上限' },
              ],
              onClick: ({ key }) => batchAction(key),
            }}
          >
            <Button disabled={!selection.keys.length || batchLocked || list.loading} loading={batch.busy}>
              批量操作 <DownOutlined />
            </Button>
          </Dropdown>
        </BatchToolbar>
        <ErrorBanner error={list.error} retry={batchLocked ? undefined : list.reload} />
        <ErrorBanner error={batch.error} />
        <Table<User>
          rowKey="id"
          dataSource={list.data?.items ?? []}
          loading={list.loading}
          locale={tableLocale}
          pagination={{ ...list.pagination, disabled: batchLocked }}
          rowSelection={selection.rowSelection}
          scroll={{ x: 930 }}
          columns={[
            {
              title: '用户',
              key: 'user',
              render: (_, user) => (
                <div className="name-cell">
                  <Space>
                    <Typography.Text strong>{user.displayName || user.username}</Typography.Text>
                    {user.id === currentUser.id && <Tag bordered={false}>当前账号</Tag>}
                  </Space>
                  <Typography.Text type="secondary">{user.username}</Typography.Text>
                </div>
              ),
            },
            {
              title: '角色',
              dataIndex: 'role',
              width: 120,
              render: (role: User['role']) => (
                <Tag color={role === 'admin' ? 'blue' : 'default'}>{roleNames[role]}</Tag>
              ),
            },
            {
              title: '状态',
              dataIndex: 'disabled',
              width: 100,
              render: (disabled: boolean) => (
                <Tag color={disabled ? 'default' : 'green'}>{disabled ? '已禁用' : '正常'}</Tag>
              ),
            },
            {
              title: '设备配额',
              dataIndex: 'maxDevices',
              width: 100,
              render: (value: number) => `${value} 台`,
            },
            { title: '账号总配额', dataIndex: 'bandwidthBitsPerSecond', width: 130, render: bandwidth },
            { title: '创建时间', dataIndex: 'createdAt', width: 180, render: formatDate },
            {
              title: '操作',
              key: 'actions',
              width: 100,
              fixed: 'right',
              render: (_, user) => (
                <Button
                  type="link"
                  icon={<EditOutlined />}
                  disabled={batchLocked}
                  onClick={() => setEditing(user)}
                >
                  编辑
                </Button>
              ),
            },
          ]}
        />
      </Card>
      <Modal
        title={batchSetting === 'bandwidth' ? '批量设置账号总带宽' : '批量设置设备数量上限'}
        open={!!batchSetting}
        onCancel={() => setBatchSetting(undefined)}
        onOk={saveBatchSetting}
        okText="继续确认"
        cancelText="取消"
        confirmLoading={batch.busy}
        destroyOnHidden
        closable={!batch.busy}
        maskClosable={!batch.busy}
        cancelButtonProps={{ disabled: batch.busy }}
      >
        <Typography.Paragraph>将修改当前选中的 {selection.keys.length} 个用户。</Typography.Paragraph>
        <ErrorBanner error={batch.error} />
        <Form form={batchForm} layout="vertical" preserve={false} disabled={batch.busy}>
          <Form.Item
            name="value"
            label={batchSetting === 'bandwidth' ? '每个账号的总带宽配额' : '每个账号的设备数量上限'}
            rules={
              batchSetting === 'bandwidth'
                ? [
                    { required: true, message: '请输入带宽配额' },
                    { type: 'number', min: 0.008, max: 100000, message: '允许 0.008–100000 Mbps' },
                  ]
                : [
                    { required: true, message: '请输入设备数量上限' },
                    { type: 'integer', min: 1, max: 1024, message: '允许 1–1024 台设备' },
                  ]
            }
          >
            {batchSetting === 'bandwidth' ? (
              <InputNumber min={0.008} max={100000} precision={3} suffix="Mbps" style={{ width: '100%' }} />
            ) : (
              <InputNumber min={1} max={1024} precision={0} suffix="台" style={{ width: '100%' }} />
            )}
          </Form.Item>
        </Form>
      </Modal>
      <Modal
        title={editing ? '编辑用户' : '创建用户'}
        open={open}
        onCancel={() => setEditing(undefined)}
        onOk={save}
        okText={editing ? '保存修改' : '创建用户'}
        cancelText="取消"
        confirmLoading={busy}
        okButtonProps={{ disabled: loadingDefault || !!defaultError }}
        destroyOnHidden
        width={560}
        closable={!busy}
        maskClosable={!busy}
        cancelButtonProps={{ disabled: busy }}
      >
        <Form
          form={form}
          layout="vertical"
          requiredMark={false}
          preserve={false}
          disabled={busy || loadingDefault}
        >
          <ErrorBanner error={error} />
          <ErrorBanner error={defaultError} retry={() => setDefaultRevision((value) => value + 1)} />
          {editing?.id === currentUser.id && (
            <Alert
              type="info"
              showIcon
              message="正在编辑当前登录账号。请保留可用的管理员账号。"
              className="form-note"
            />
          )}
          <Row gutter={16}>
            <Col span={12}>
              <Form.Item
                name="username"
                label="用户名"
                normalize={(value: string) => value.toLowerCase()}
                rules={usernameRules}
              >
                <Input disabled={!!editing} autoComplete="off" maxLength={48} placeholder="用于登录" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item name="displayName" label="显示名称" rules={nameRules}>
                <Input maxLength={160} placeholder="可选" />
              </Form.Item>
            </Col>
          </Row>
          <Form.Item
            name="password"
            label={editing ? '重置密码' : '初始密码'}
            extra={editing ? '留空保留原密码。' : '12–72 个 UTF-8 字节，汉字通常占 3 字节。'}
            rules={passwordRules(!editing)}
          >
            <Input.Password
              autoComplete="new-password"
              maxLength={72}
              placeholder={editing ? '留空不修改' : '请输入初始密码'}
            />
          </Form.Item>
          <Row gutter={16}>
            <Col span={12}>
              <Form.Item name="role" label="角色" rules={[{ required: true }]}>
                <Select
                  options={[
                    { value: 'user', label: '注册用户 · 管理自己的资源' },
                    { value: 'guest', label: '访客 · 只读访问自己的资源' },
                    { value: 'admin', label: '系统管理员 · 管理全平台' },
                  ]}
                />
              </Form.Item>
            </Col>
            <Col span={12}>
              {editing && (
                <Form.Item name="disabled" label="账号状态" valuePropName="checked">
                  <Switch checkedChildren="已禁用" unCheckedChildren="正常" />
                </Form.Item>
              )}
            </Col>
          </Row>
          <Row gutter={16}>
            <Col span={12}>
              <Form.Item
                name="maxDevices"
                label="设备数量上限"
                rules={[
                  { required: true, message: '请输入设备配额' },
                  { type: 'number', min: 1, max: 1024, message: '允许 1–1024 台设备' },
                ]}
              >
                <InputNumber min={1} max={1024} precision={0} style={{ width: '100%' }} suffix="台" />
              </Form.Item>
            </Col>
            <Col span={12}>
              <Form.Item
                name="bandwidthMbps"
                label="账号总带宽配额"
                rules={[
                  { required: true, message: '请输入带宽配额' },
                  { type: 'number', min: 0.008, max: 100000, message: '允许 0.008–100000 Mbps' },
                ]}
              >
                <InputNumber min={0.008} max={100000} precision={3} style={{ width: '100%' }} suffix="Mbps" />
              </Form.Item>
            </Col>
          </Row>
          <Typography.Paragraph type="secondary" className="form-help">
            该用户名下所有设备共享此双向合计带宽配额。修改用户带宽会断开关联设备，重连后生效。创建用户时初始值取自节点当前默认带宽。
          </Typography.Paragraph>
        </Form>
      </Modal>
    </>
  );
}
