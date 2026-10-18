// SPDX-License-Identifier: GPL-3.0-only
import { useEffect, useRef, useState } from 'react';
import {
  App,
  Alert,
  Avatar,
  Button,
  Card,
  Divider,
  Drawer,
  Dropdown,
  Form,
  Input,
  Layout,
  Menu,
  Modal,
  Space,
  Spin,
  Tag,
  Typography,
} from 'antd';
import {
  ApiOutlined,
  AppstoreOutlined,
  AuditOutlined,
  DownOutlined,
  CloudServerOutlined,
  LinkOutlined,
  LockOutlined,
  LogoutOutlined,
  MenuOutlined,
  ReloadOutlined,
  SafetyCertificateOutlined,
  TeamOutlined,
  UserOutlined,
} from '@ant-design/icons';
import type { MenuProps } from 'antd';
import { api, ApiError, errorText, json, setCsrfToken } from './api';
import type { AuthSession } from './types';
import { OverviewPage } from './pages/OverviewPage';
import { UsersPage } from './pages/UsersPage';
import { DevicesPage } from './pages/DevicesPage';
import { RelayPage } from './pages/RelayPage';
import { AuditPage } from './pages/AuditPage';
import { SessionsPage } from './pages/SessionsPage';
import { nameRules, passwordRules, roleNames, usernameRules } from './validation';
import { ErrorBanner } from './components';

type Section = 'overview' | 'users' | 'devices' | 'relay' | 'audit' | 'sessions';
const sectionTitles = {
  overview: '运行概览',
  users: '用户管理',
  devices: '设备管理',
  relay: '中继接入',
  audit: '审计日志',
  sessions: '中继会话',
};

function Brand() {
  return (
    <div className="brand">
      <div className="brand-symbol">
        <ApiOutlined />
      </div>
      <div>
        <strong>EmbedDesk</strong>
        <span>RELAY CONSOLE</span>
      </div>
    </div>
  );
}

function Login({
  onLogin,
  sessionExpired,
}: {
  onLogin: (session: AuthSession) => void;
  sessionExpired: boolean;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [registrationEnabled, setRegistrationEnabled] = useState(false);
  const [registering, setRegistering] = useState(false);
  const [registered, setRegistered] = useState(false);
  const [captcha, setCaptcha] = useState<{ id: string; image: string; expiresIn: number }>();
  const [captchaLoading, setCaptchaLoading] = useState(false);
  const [captchaError, setCaptchaError] = useState('');
  const captchaAbort = useRef<AbortController | null>(null);
  const [form] = Form.useForm();
  const loadCaptcha = async () => {
    captchaAbort.current?.abort();
    const controller = new AbortController();
    captchaAbort.current = controller;
    setCaptcha(undefined);
    setCaptchaError('');
    setCaptchaLoading(true);
    form.setFieldValue('captchaCode', '');
    try {
      const result = await api<{ id: string; image: string; expiresIn: number }>('/auth/captcha', {
        signal: controller.signal,
      });
      if (!controller.signal.aborted) {
        if (!/^data:image\/png;base64,[a-zA-Z0-9+/]+=*$/.test(result.image))
          throw new Error('验证码图片格式不正确，请重试。');
        setCaptcha(result);
      }
    } catch (reason) {
      if (!controller.signal.aborted) setCaptchaError(errorText(reason));
    } finally {
      if (!controller.signal.aborted) setCaptchaLoading(false);
    }
  };
  useEffect(() => {
    if (registering) void loadCaptcha();
    else {
      setCaptcha(undefined);
      setCaptchaError('');
    }
    return () => captchaAbort.current?.abort();
  }, [registering]);
  useEffect(() => {
    const controller = new AbortController();
    api<{ registrationEnabled: boolean }>('/auth/options', { signal: controller.signal })
      .then((options) => {
        if (!controller.signal.aborted) setRegistrationEnabled(options.registrationEnabled);
      })
      .catch(() => {});
    return () => controller.abort();
  }, []);
  const login = async (values: {
    username: string;
    password: string;
    displayName?: string;
    captchaCode?: string;
  }) => {
    if (registering && (!captcha || captchaLoading)) return;
    setBusy(true);
    setError('');
    try {
      if (registering) {
        await api(
          '/auth/register',
          json('POST', {
            username: values.username,
            password: values.password,
            displayName: values.displayName || '',
            captchaId: captcha?.id,
            captchaCode: values.captchaCode?.trim(),
          }),
        );
        setRegistering(false);
        setRegistered(true);
        form.resetFields();
        form.setFieldValue('username', values.username);
      } else {
        onLogin(
          await api<AuthSession>(
            '/auth/login',
            json('POST', { username: values.username, password: values.password }),
          ),
        );
      }
    } catch (reason) {
      setError(errorText(reason));
      if (registering) void loadCaptcha();
      else form.setFieldValue('password', '');
    } finally {
      setBusy(false);
    }
  };
  return (
    <main className={`login-page${registering ? ' register-page' : ''}`}>
      <div className="login-shell">
        <Brand />
        <Card className="login-card">
          <Tag color="blue" bordered={false}>
            中继管理平台
          </Tag>
          <Typography.Title level={2}>{registering ? '创建账号' : '欢迎回来'}</Typography.Title>
          <Typography.Paragraph type="secondary">
            {registering
              ? '注册个人账号，输入自己的设备 ID 绑定并管理设备。'
              : '登录后管理设备接入、用户配额和中继服务。'}
          </Typography.Paragraph>
          {sessionExpired && !registering && (
            <Alert showIcon type="info" message="登录状态已失效，请重新登录。" className="error-banner" />
          )}
          {registered && (
            <Alert
              showIcon
              type="success"
              message="账号已创建，请使用新密码登录。"
              className="error-banner"
            />
          )}
          {error && <Alert showIcon type="error" message={error} className="error-banner" />}
          <Form
            form={form}
            layout="vertical"
            requiredMark={false}
            onFinish={login}
            disabled={busy}
            preserve={false}
          >
            <Form.Item
              name="username"
              label="用户名"
              normalize={(value: string) => value.toLowerCase()}
              rules={registering ? usernameRules : [{ required: true, message: '请输入用户名' }]}
            >
              <Input
                autoFocus
                autoComplete="username"
                prefix={<UserOutlined />}
                placeholder="请输入用户名"
                size={registering ? 'middle' : 'large'}
                maxLength={48}
              />
            </Form.Item>
            {registering && (
              <Form.Item name="displayName" label="显示名称" rules={nameRules}>
                <Input maxLength={160} placeholder="可选" autoComplete="nickname" />
              </Form.Item>
            )}
            <Form.Item
              name="password"
              label="密码"
              rules={registering ? passwordRules(true) : [{ required: true, message: '请输入密码' }]}
              extra={registering ? '12–72 个 UTF-8 字节，汉字通常占 3 字节。' : undefined}
            >
              <Input.Password
                autoComplete={registering ? 'new-password' : 'current-password'}
                prefix={<LockOutlined />}
                placeholder="请输入密码"
                size={registering ? 'middle' : 'large'}
                maxLength={72}
              />
            </Form.Item>
            {registering && (
              <Form.Item
                name="confirmPassword"
                label="确认密码"
                dependencies={['password']}
                rules={[
                  { required: true, message: '请再次输入密码' },
                  ({ getFieldValue }) => ({
                    validator: (_, value) =>
                      !value || value === getFieldValue('password')
                        ? Promise.resolve()
                        : Promise.reject(new Error('两次输入的密码不一致')),
                  }),
                ]}
              >
                <Input.Password autoComplete="new-password" maxLength={72} />
              </Form.Item>
            )}
            {registering && (
              <>
                <div className="captcha-grid">
                  <div className="captcha-box">
                    <div className="captcha-image">
                      {captchaLoading ? (
                        <Spin size="small" />
                      ) : captcha ? (
                        <img src={captcha.image} alt="注册验证码" />
                      ) : (
                        <Typography.Text type="secondary">验证码加载失败</Typography.Text>
                      )}
                    </div>
                    <Button
                      icon={<ReloadOutlined />}
                      type="link"
                      size="small"
                      onClick={() => void loadCaptcha()}
                      loading={captchaLoading}
                      disabled={busy}
                    >
                      换一张
                    </Button>
                  </div>
                  <Form.Item
                    name="captchaCode"
                    label="图片验证码"
                    rules={[{ required: true, whitespace: true, message: '请输入图片中的验证码' }]}
                    extra={
                      captcha
                        ? `${Math.ceil(captcha.expiresIn / 60)} 分钟内有效，仅限本次提交。`
                        : '请先加载验证码。'
                    }
                  >
                    <Input
                      autoComplete="off"
                      maxLength={16}
                      placeholder="图片中的数字"
                      disabled={captchaLoading || !captcha}
                    />
                  </Form.Item>
                </div>
                <ErrorBanner error={captchaError} retry={() => void loadCaptcha()} />
              </>
            )}
            <Button
              block
              size="large"
              type="primary"
              htmlType="submit"
              loading={busy}
              disabled={registering && (!captcha || captchaLoading)}
            >
              {registering ? '创建账号' : '登录控制台'}
            </Button>
            {registrationEnabled && (
              <Button
                block
                type="link"
                className="auth-switch"
                onClick={() => {
                  setRegistering((value) => !value);
                  setRegistered(false);
                  setError('');
                  form.resetFields();
                }}
              >
                {registering ? '已有账号？返回登录' : '还没有账号？创建账号'}
              </Button>
            )}
          </Form>
        </Card>
        <div className="login-footer">
          <SafetyCertificateOutlined /> 仅限已授权用户访问
        </div>
      </div>
    </main>
  );
}

function PasswordModal({
  open,
  onClose,
  onSaved,
}: {
  open: boolean;
  onClose: () => void;
  onSaved: () => void;
}) {
  const [form] = Form.useForm();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const { message } = App.useApp();
  useEffect(() => {
    if (open) {
      form.resetFields();
      setError('');
    }
  }, [open, form]);
  const submit = async () => {
    const values = await form.validateFields().catch(() => null);
    if (!values) return;
    setBusy(true);
    setError('');
    try {
      await api(
        '/auth/password',
        json('POST', { currentPassword: values.currentPassword, newPassword: values.newPassword }),
      );
      message.success('密码已更新，请重新登录');
      form.resetFields();
      onSaved();
    } catch (reason) {
      setError(errorText(reason));
    } finally {
      setBusy(false);
    }
  };
  return (
    <Modal
      title="修改密码"
      open={open}
      destroyOnHidden
      onCancel={onClose}
      onOk={submit}
      confirmLoading={busy}
      okText="更新密码"
      cancelText="取消"
      closable={!busy}
      maskClosable={!busy}
      cancelButtonProps={{ disabled: busy }}
    >
      <Form form={form} layout="vertical" requiredMark={false} preserve={false} disabled={busy}>
        <Alert
          type="info"
          showIcon
          message="新密码须为 12–72 个 UTF-8 字节；汉字通常占 3 字节。修改成功后需要重新登录。"
          className="form-note"
        />
        {error && <Alert type="error" showIcon message={error} className="error-banner" />}
        <Form.Item
          label="当前密码"
          name="currentPassword"
          rules={[{ required: true, message: '请输入当前密码' }]}
        >
          <Input.Password autoComplete="current-password" maxLength={72} />
        </Form.Item>
        <Form.Item label="新密码" name="newPassword" rules={passwordRules(true)}>
          <Input.Password autoComplete="new-password" maxLength={72} />
        </Form.Item>
        <Form.Item
          label="确认新密码"
          name="confirmPassword"
          dependencies={['newPassword']}
          rules={[
            { required: true, message: '请再次输入新密码' },
            ({ getFieldValue }) => ({
              validator(_, value) {
                return !value || getFieldValue('newPassword') === value
                  ? Promise.resolve()
                  : Promise.reject(new Error('两次输入的密码不一致'));
              },
            }),
          ]}
        >
          <Input.Password autoComplete="new-password" maxLength={72} />
        </Form.Item>
      </Form>
    </Modal>
  );
}

export default function ConsoleApp() {
  const [session, setSession] = useState<AuthSession>();
  const [initializing, setInitializing] = useState(true);
  const [initialError, setInitialError] = useState('');
  const [expired, setExpired] = useState(false);
  const [section, setSection] = useState<Section>('overview');
  const [drawer, setDrawer] = useState(false);
  const [passwordOpen, setPasswordOpen] = useState(false);
  const [logoutBusy, setLogoutBusy] = useState(false);
  const { message } = App.useApp();

  const authenticate = (value: AuthSession) => {
    setCsrfToken(value.csrfToken);
    setSession(value);
    setExpired(false);
    setSection('overview');
  };
  const loadSession = async () => {
    setInitializing(true);
    setInitialError('');
    try {
      authenticate(await api<AuthSession>('/auth/me'));
    } catch (reason) {
      if (!(reason instanceof ApiError && reason.status === 401)) setInitialError(errorText(reason));
    } finally {
      setInitializing(false);
    }
  };
  useEffect(() => {
    void loadSession();
  }, []);
  useEffect(() => {
    const clearSession = () => {
      setCsrfToken('');
      setSession(undefined);
      setExpired(true);
      setPasswordOpen(false);
      setDrawer(false);
    };
    window.addEventListener('console-session-expired', clearSession);
    return () => window.removeEventListener('console-session-expired', clearSession);
  }, []);

  const logout = async () => {
    setLogoutBusy(true);
    try {
      await api('/auth/logout', json('POST'));
      setCsrfToken('');
      setSession(undefined);
      setPasswordOpen(false);
      setDrawer(false);
    } catch (reason) {
      message.error(errorText(reason));
    } finally {
      setLogoutBusy(false);
    }
  };
  if (initializing)
    return (
      <div className="initial-state">
        <Spin size="large" />
        <Typography.Text type="secondary">正在连接控制台…</Typography.Text>
      </div>
    );
  if (initialError)
    return (
      <div className="initial-state">
        <Alert type="error" showIcon message="无法加载控制台" description={initialError} />
        <Button onClick={loadSession}>重新连接</Button>
      </div>
    );
  if (!session) return <Login onLogin={authenticate} sessionExpired={expired} />;
  const user = session.user;
  const admin = user.role === 'admin';
  const guest = user.role === 'guest';
  const items: MenuProps['items'] = [
    { key: 'overview', icon: <AppstoreOutlined />, label: '运行概览' },
    { key: 'devices', icon: <ApiOutlined />, label: '设备管理' },
    { key: 'sessions', icon: <LinkOutlined />, label: '中继会话' },
    { key: 'relay', icon: <CloudServerOutlined />, label: '中继接入' },
    ...(admin
      ? [
          { type: 'divider' as const },
          { key: 'users', icon: <TeamOutlined />, label: '用户管理' },
          { key: 'audit', icon: <AuditOutlined />, label: '审计日志' },
        ]
      : []),
  ];
  const navigate = (next: Section) => {
    setSection(next);
    setDrawer(false);
  };
  const navigation = (
    <>
      <Brand />
      <div className="nav-label">工作空间</div>
      <Menu
        theme="dark"
        mode="inline"
        selectedKeys={[section]}
        items={items}
        onClick={({ key }) => navigate(key as Section)}
      />
      <div className="sidebar-bottom">
        <Divider />
        <div className="workspace-scope">
          <SafetyCertificateOutlined />
          <div>
            <strong>{admin ? '管理员工作空间' : guest ? '访客工作空间' : '个人工作空间'}</strong>
            <span>
              {admin ? '管理所有用户与接入资源' : guest ? '只读访问我的设备与会话' : '管理已绑定的设备与会话'}
            </span>
          </div>
        </div>
      </div>
    </>
  );
  return (
    <Layout className="app-layout">
      <Layout.Sider width={232} className="desktop-sidebar">
        {navigation}
      </Layout.Sider>
      <Drawer
        placement="left"
        width={268}
        open={drawer}
        onClose={() => setDrawer(false)}
        styles={{ body: { padding: 0, background: '#101722' } }}
        title="中继控制台"
      >
        {navigation}
      </Drawer>
      <Layout className="main-layout">
        <Layout.Header className="app-header">
          <Space>
            <Button
              className="mobile-menu"
              aria-label="打开导航"
              icon={<MenuOutlined />}
              type="text"
              onClick={() => setDrawer(true)}
            />
            <span className="header-parent">控制台</span>
            <span className="header-divider">/</span>
            <span>{sectionTitles[section]}</span>
          </Space>
          <Dropdown
            trigger={['click']}
            menu={{
              items: [
                { key: 'password', icon: <LockOutlined />, label: '修改密码' },
                { type: 'divider' },
                {
                  key: 'logout',
                  icon: <LogoutOutlined />,
                  label: logoutBusy ? '正在退出…' : '退出登录',
                  disabled: logoutBusy,
                },
              ],
              onClick: ({ key }) => (key === 'password' ? setPasswordOpen(true) : void logout()),
            }}
          >
            <Button type="text" className="account-menu">
              <Avatar size={28} icon={<UserOutlined />} />
              <span className="account-name">{user.displayName || user.username}</span>
              <Tag color={admin ? 'blue' : 'default'} bordered={false}>
                {roleNames[user.role]}
              </Tag>
              <DownOutlined />
            </Button>
          </Dropdown>
        </Layout.Header>
        <Layout.Content className="main-content">
          {section === 'overview' && <OverviewPage user={user} navigate={navigate} />}
          {section === 'users' && admin && <UsersPage currentUser={user} />}
          {section === 'devices' && <DevicesPage admin={admin} readonly={guest} />}
          {section === 'sessions' && <SessionsPage readonly={guest} />}
          {section === 'relay' && <RelayPage admin={admin} />}
          {section === 'audit' && admin && <AuditPage />}
        </Layout.Content>
        <Layout.Footer className="app-footer">
          <span>EmbedDesk Relay</span>
          <span>设备互联 · 访问管理</span>
        </Layout.Footer>
      </Layout>
      <PasswordModal
        open={passwordOpen}
        onClose={() => setPasswordOpen(false)}
        onSaved={() => {
          setCsrfToken('');
          setSession(undefined);
          setExpired(false);
          setPasswordOpen(false);
          setDrawer(false);
        }}
      />
    </Layout>
  );
}
