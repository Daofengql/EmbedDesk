// SPDX-License-Identifier: GPL-3.0-only
import { useEffect, useState } from 'react';
import { App, Alert, Form, Input, Modal } from 'antd';
import { api, errorText, json } from '../api';
import { ErrorBanner } from '../components';

export function BindDeviceModal({
  open,
  onClose,
  onSaved,
}: {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form] = Form.useForm<{ deviceId: string }>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const { modal, message } = App.useApp();
  useEffect(() => {
    if (open) {
      form.resetFields();
      setError('');
    }
  }, [open, form]);
  const submit = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values) return;
    const deviceId = values.deviceId.trim().toUpperCase();
    const accepted = await modal.confirm({
      title: '将设备绑定到当前账号？',
      content: `设备 ${deviceId} 会断开当前连接，重连后与当前账号下的其他设备共享账号总带宽。已属于其他账号的设备无法直接绑定。`,
      okText: '确认绑定',
      cancelText: '取消',
    });
    if (!accepted) return;
    setBusy(true);
    setError('');
    try {
      await api('/devices/bind', json('POST', { deviceId }));
      message.success('设备已绑定到当前账号');
      form.resetFields();
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
      title="绑定设备"
      open={open}
      onCancel={onClose}
      onOk={submit}
      okText="绑定到我的账号"
      cancelText="取消"
      confirmLoading={busy}
      destroyOnHidden
      closable={!busy}
      maskClosable={!busy}
      cancelButtonProps={{ disabled: busy }}
    >
      <ErrorBanner error={error} />
      <Form form={form} layout="vertical" requiredMark={false} preserve={false} disabled={busy}>
        <Form.Item
          name="deviceId"
          label="设备 ID"
          normalize={(value: string) => value.toUpperCase()}
          rules={[
            { required: true, message: '请输入设备 ID' },
            { pattern: /^[0-9A-F]{12}$/, message: '请输入客户端显示的 12 位十六进制设备 ID' },
          ]}
          extra="先让自己的设备连接到此中继，再输入客户端连接中心显示的设备 ID。"
        >
          <Input autoFocus maxLength={12} autoComplete="off" placeholder="12 位设备 ID" className="mono" />
        </Form.Item>
        <Alert type="info" showIcon message="仅可绑定尚未归属任何账号的匿名设备，绑定后计入账号设备配额。" />
      </Form>
    </Modal>
  );
}
