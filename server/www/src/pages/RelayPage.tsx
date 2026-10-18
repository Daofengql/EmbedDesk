// SPDX-License-Identifier: GPL-3.0-only
import { useRef, useState } from 'react';
import {
  App,
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Form,
  Input,
  InputNumber,
  Modal,
  Row,
  Skeleton,
  Space,
  Switch,
  Tag,
  Typography,
} from 'antd';
import {
  CloudServerOutlined,
  EyeInvisibleOutlined,
  EyeOutlined,
  ReloadOutlined,
  SettingOutlined,
} from '@ant-design/icons';
import { api, errorText, json } from '../api';
import { bandwidth, ErrorBanner, PageTitle } from '../components';
import { useResource } from '../hooks';
import type { RelaySettings } from '../types';

interface SettingsValues {
  name: string;
  publicUrl: string;
  admissionEnabled: boolean;
  defaultBandwidthMbps: number;
}

export function RelayPage({ admin }: { admin: boolean }) {
  const relay = useResource<RelaySettings>('/relay');
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [showToken, setShowToken] = useState(false);
  const [tokenBusy, setTokenBusy] = useState(false);
  const [tokenError, setTokenError] = useState('');
  const tokenLocked = useRef(false);
  const [form] = Form.useForm<SettingsValues>();
  const admissionEnabled = Form.useWatch('admissionEnabled', form);
  const { modal, message } = App.useApp();
  const data = relay.data;
  const edit = () => {
    if (!data) return;
    setError('');
    form.resetFields();
    form.setFieldsValue({
      name: data.name,
      publicUrl: data.publicUrl,
      admissionEnabled: data.admissionRequired,
      defaultBandwidthMbps: data.defaultBandwidthBitsPerSecond / 1_000_000,
    });
    setOpen(true);
  };
  const save = async () => {
    if (busy || tokenLocked.current) return;
    const values = await form.validateFields().catch(() => null);
    if (!values || !data) return;
    const admissionChanged = values.admissionEnabled !== data.admissionRequired;
    const accepted = await modal.confirm({
      title: admissionChanged ? '更改节点准入过滤？' : '保存中继节点设置？',
      content: admissionChanged
        ? values.admissionEnabled
          ? '启用后服务器将随机生成公共令牌。客户端再次注册前，需要导入最新的连接代码；现有连接保留。'
          : '关闭后设备无需提供公共令牌即可注册；现有连接保留。'
        : '更新节点设置。默认带宽只影响后续新注册账号和匿名设备，不覆盖现有用户。已在线匿名设备在下次重连时应用新值。',
      okText: '确认更新',
      cancelText: '取消',
      okButtonProps: { danger: admissionChanged },
    });
    if (!accepted) return;
    setBusy(true);
    setError('');
    try {
      await api(
        '/relay',
        json('PATCH', {
          name: values.name.trim(),
          publicUrl: values.publicUrl?.trim() || '',
          defaultBandwidthBitsPerSecond: Math.round(values.defaultBandwidthMbps * 1_000_000),
          ...(admissionChanged ? { admissionEnabled: values.admissionEnabled } : {}),
        }),
      );
      message.success('中继节点设置已保存');
      setOpen(false);
      form.resetFields();
      setShowToken(false);
      relay.reload();
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  };
  const refreshToken = async () => {
    if (tokenLocked.current || busy || open) return;
    tokenLocked.current = true;
    setTokenBusy(true);
    setTokenError('');
    try {
      const accepted = await modal.confirm({
        title: '刷新节点准入令牌？',
        content:
          '服务器将随机生成新的公共令牌并启用简单准入过滤。旧令牌不能用于后续设备注册，客户端再次注册前需重新导入连接代码；现有连接保留。',
        okText: '确认刷新',
        cancelText: '取消',
        okButtonProps: { danger: true },
      });
      if (!accepted) return;
      await api('/relay/admission-token/refresh', json('POST'));
      setShowToken(false);
      relay.reload();
      message.success('准入令牌已刷新，请重新复制客户端连接代码');
    } catch (reason) {
      setTokenError(errorText(reason));
    } finally {
      tokenLocked.current = false;
      setTokenBusy(false);
    }
  };
  return (
    <>
      <PageTitle
        title="中继接入"
        description="使用此节点统一的连接代码和准入令牌，将 EmbedDesk 客户端接入中继。"
        action={
          <Space wrap>
            <Button
              icon={<ReloadOutlined />}
              loading={relay.loading}
              disabled={tokenBusy || busy}
              onClick={() => {
                setShowToken(false);
                relay.reload();
              }}
            >
              刷新
            </Button>
            {admin && (
              <Button
                type="primary"
                icon={<SettingOutlined />}
                disabled={!data || tokenBusy || busy}
                onClick={edit}
              >
                中继设置
              </Button>
            )}
          </Space>
        }
      />
      <ErrorBanner error={relay.error} retry={relay.reload} />
      <ErrorBanner error={tokenError} />
      <Row gutter={[16, 16]}>
        <Col xs={24} xl={15}>
          <Card
            title={
              <Space>
                <CloudServerOutlined />
                节点连接信息
              </Space>
            }
            className="relay-credentials"
          >
            <Skeleton loading={relay.loading && !data} active>
              {data ? (
                <>
                  <Descriptions
                    column={1}
                    size="small"
                    items={[
                      { key: 'name', label: '节点名称', children: data.name || '未设置' },
                      {
                        key: 'address',
                        label: '公开地址',
                        children: data.publicUrl ? (
                          <Typography.Text copyable className="break-word">
                            {data.publicUrl}
                          </Typography.Text>
                        ) : (
                          '未配置'
                        ),
                      },
                      {
                        key: 'admission',
                        label: '准入过滤',
                        children: (
                          <Tag color={data.admissionRequired ? 'blue' : 'default'}>
                            {data.admissionRequired ? '已启用公共令牌过滤' : '未启用令牌过滤'}
                          </Tag>
                        ),
                      },
                      {
                        key: 'defaultBandwidth',
                        label: '新用户总带宽初始值',
                        children: bandwidth(data.defaultBandwidthBitsPerSecond),
                      },
                    ]}
                  />
                  <Typography.Title level={5}>客户端连接代码</Typography.Title>
                  {data.connectionCode ? (
                    <>
                      <Typography.Paragraph type="secondary">
                        复制后，在 EmbedDesk 的中继服务器设置中导入。
                      </Typography.Paragraph>
                      <Typography.Paragraph
                        className="secret-value"
                        copyable={{ text: data.connectionCode, tooltips: ['复制连接代码', '已复制'] }}
                      >
                        {data.connectionCode}
                      </Typography.Paragraph>
                    </>
                  ) : (
                    <Alert
                      type="info"
                      showIcon
                      message="暂未生成连接代码"
                      description={
                        admin
                          ? '请在中继设置中填写可供客户端访问的公开地址。'
                          : '请联系管理员配置公开地址，或在客户端手动填写节点地址。'
                      }
                    />
                  )}
                  <div className="relay-token-heading">
                    <Typography.Title level={5}>节点准入令牌</Typography.Title>
                    {admin && (
                      <Button
                        size="small"
                        icon={<ReloadOutlined />}
                        loading={tokenBusy}
                        disabled={open || busy || relay.loading}
                        onClick={() => void refreshToken()}
                      >
                        刷新令牌
                      </Button>
                    )}
                  </div>
                  {data.admissionRequired ? (
                    <>
                      <Space align="start" className="relay-token-row">
                        <Typography.Paragraph
                          className="secret-value"
                          copyable={{ text: data.admissionToken, tooltips: ['复制准入令牌', '已复制'] }}
                        >
                          {showToken ? data.admissionToken : '••••••••••••••••••••••••'}
                        </Typography.Paragraph>
                        <Button
                          type="text"
                          aria-label={showToken ? '隐藏准入令牌' : '显示准入令牌'}
                          icon={showToken ? <EyeInvisibleOutlined /> : <EyeOutlined />}
                          onClick={() => setShowToken((value) => !value)}
                        />
                      </Space>
                      <Typography.Paragraph type="secondary">
                        所有客户端共用此令牌，仅用于简单过滤扫描请求。节点管理员更新后，客户端下次注册前需重新导入连接代码。
                      </Typography.Paragraph>
                    </>
                  ) : (
                    <Typography.Paragraph type="secondary">
                      当前节点未设置准入令牌，客户端无需填写。
                    </Typography.Paragraph>
                  )}
                </>
              ) : (
                <Typography.Text type="secondary">暂时无法获取节点信息</Typography.Text>
              )}
            </Skeleton>
          </Card>
        </Col>
        <Col xs={24} xl={9}>
          <Card title="接入与设备归属">
            <div className="relay-steps">
              <div>
                <span>1</span>
                <div>
                  <Typography.Text strong>导入节点连接代码</Typography.Text>
                  <Typography.Paragraph type="secondary">
                    所有账号使用同一份节点连接配置。
                  </Typography.Paragraph>
                </div>
              </div>
              <div>
                <span>2</span>
                <div>
                  <Typography.Text strong>客户端连接中继</Typography.Text>
                  <Typography.Paragraph type="secondary">
                    客户端通过节点的简单准入过滤后，设备会登记到中继。
                  </Typography.Paragraph>
                </div>
              </div>
              <div>
                <span>3</span>
                <div>
                  <Typography.Text strong>输入设备 ID 绑定账号</Typography.Text>
                  <Typography.Paragraph type="secondary">
                    注册用户输入自己的设备 ID
                    即可绑定无主设备；管理员也可分配归属。绑定后账号内所有设备共享总带宽，访客只读查看。
                  </Typography.Paragraph>
                </div>
              </div>
            </div>
            <Alert
              type="info"
              showIcon
              message="准入令牌统一维护。匿名设备各自使用默认带宽；绑定设备共享账号总配额。"
            />
          </Card>
        </Col>
      </Row>
      {admin && (
        <Modal
          title="中继节点设置"
          open={open}
          onCancel={() => setOpen(false)}
          onOk={save}
          okText="保存设置"
          cancelText="取消"
          confirmLoading={busy}
          destroyOnHidden
          width={600}
          closable={!busy}
          maskClosable={!busy}
          cancelButtonProps={{ disabled: busy }}
        >
          <Form form={form} layout="vertical" preserve={false} requiredMark={false} disabled={busy}>
            <ErrorBanner error={error} />
            <Form.Item
              name="name"
              label="节点名称"
              rules={[
                { required: true, whitespace: true, message: '请输入节点名称' },
                { max: 64, message: '最多 64 个字符' },
                { pattern: /^[^\x00-\x1f\x7f-\x9f]*$/, message: '名称不能包含控制字符' },
              ]}
            >
              <Input maxLength={64} />
            </Form.Item>
            <Form.Item
              name="publicUrl"
              label="公开地址"
              extra="供客户端连接的 HTTP 或 HTTPS 根地址，不含路径；留空时不生成连接代码。"
              rules={[
                { max: 2048, message: '地址最多 2048 个字符' },
                {
                  validator: (_, value: string | undefined) => {
                    if (!value?.trim()) return Promise.resolve();
                    try {
                      const url = new URL(value.trim());
                      if (
                        !['http:', 'https:'].includes(url.protocol) ||
                        !url.hostname ||
                        url.username ||
                        url.password ||
                        (url.pathname !== '' && url.pathname !== '/') ||
                        /[?#\x00-\x20\x7f]/.test(value.trim()) ||
                        url.port === '0'
                      )
                        throw new Error();
                      return Promise.resolve();
                    } catch {
                      return Promise.reject(
                        new Error('请输入 HTTP 或 HTTPS 根地址，不能包含账号、路径、查询参数或片段'),
                      );
                    }
                  },
                },
              ]}
            >
              <Input maxLength={2048} placeholder="https://HOST" autoComplete="off" />
            </Form.Item>
            <Form.Item
              name="defaultBandwidthMbps"
              label="新用户总带宽初始值 / 匿名设备每台默认带宽"
              extra="仅作为后续新注册账号的总配额，不覆盖现有用户。匿名设备每台使用此默认值，已在线匿名设备下次重连生效。"
              rules={[
                { required: true, message: '请输入默认带宽' },
                { type: 'number', min: 0.008, max: 100000, message: '允许 0.008–100000 Mbps' },
              ]}
            >
              <InputNumber min={0.008} max={100000} precision={3} style={{ width: '100%' }} suffix="Mbps" />
            </Form.Item>
            <Form.Item
              name="admissionEnabled"
              label="公共令牌过滤"
              valuePropName="checked"
              extra="启用时自动生成令牌；更换令牌请使用页面上的“刷新令牌”。"
            >
              <Switch checkedChildren="启用" unCheckedChildren="关闭" />
            </Form.Item>
            <Alert
              type={admissionEnabled === data?.admissionRequired ? 'info' : 'warning'}
              showIcon
              message={
                admissionEnabled === data?.admissionRequired
                  ? '默认带宽只影响新注册账号和匿名设备，不覆盖现有用户。'
                  : admissionEnabled
                    ? '启用后客户端下次注册需提供自动生成的令牌；现有连接保留。'
                    : '关闭后设备无需提供节点准入令牌即可注册；现有连接保留。'
              }
            />
          </Form>
        </Modal>
      )}
    </>
  );
}
