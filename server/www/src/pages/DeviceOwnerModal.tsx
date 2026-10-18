// SPDX-License-Identifier: GPL-3.0-only
import { useEffect, useState } from 'react';
import { App, Alert, Form, Modal, Typography } from 'antd';
import { api, errorText, json } from '../api';
import { ErrorBanner } from '../components';
import type { Device } from '../types';
import { OwnerSelect } from './OwnerSelect';

export function DeviceOwnerModal({
  device,
  onClose,
  onSaved,
}: {
  device?: Device;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form] = Form.useForm<{ ownerId?: number }>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const { modal, message } = App.useApp();
  useEffect(() => {
    if (device) {
      form.resetFields();
      form.setFieldsValue({ ownerId: device.ownerId ?? undefined });
      setError('');
    }
  }, [device, form]);
  const save = async () => {
    if (!device) return;
    const values = await form.validateFields().catch(() => null);
    if (!values) return;
    const ownerId = values.ownerId ?? null;
    if (ownerId === device.ownerId) {
      onClose();
      return;
    }
    const accepted = await modal.confirm({
      title: ownerId === null ? '清空设备归属？' : '分配设备所属用户？',
      content:
        '更改归属会断开此设备当前连接，重连后与新账号的其他设备共享总带宽。清空后恢复为使用每台默认带宽的匿名设备，可被用户重新绑定。',
      okText: '确认分配',
      cancelText: '取消',
      okButtonProps: { danger: true },
    });
    if (!accepted) return;
    setBusy(true);
    setError('');
    try {
      await api(`/devices/${encodeURIComponent(device.id)}`, json('PATCH', { ownerId }));
      message.success('设备归属已更新');
      onSaved();
      onClose();
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal
      title="分配设备归属"
      open={!!device}
      onCancel={onClose}
      onOk={save}
      okText="保存归属"
      cancelText="取消"
      confirmLoading={busy}
      destroyOnHidden
      closable={!busy}
      maskClosable={!busy}
      cancelButtonProps={{ disabled: busy }}
    >
      <ErrorBanner error={error} />
      <Form form={form} layout="vertical" requiredMark={false} preserve={false} disabled={busy}>
        <Form.Item label="设备">
          <Typography.Text strong>{device?.label || '未命名设备'}</Typography.Text>
          <Typography.Paragraph type="secondary" className="mono">
            {device?.id}
          </Typography.Paragraph>
        </Form.Item>
        <Form.Item
          label="所属用户"
          name="ownerId"
          extra="支持搜索用户名或显示名称，最多显示 100 条结果。清空可解除归属。"
        >
          <OwnerSelect disabled={busy} currentOwnerName={device?.ownerName} />
        </Form.Item>
        <Alert
          type="info"
          showIcon
          message="注册用户可以管理分配给自己的设备；访客仅能查看。分配后设备会断开，重连后与账号内其他设备共享总带宽。"
        />
      </Form>
    </Modal>
  );
}
