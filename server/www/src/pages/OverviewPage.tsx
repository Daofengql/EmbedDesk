// SPDX-License-Identifier: GPL-3.0-only
import {
  ApiOutlined,
  ArrowRightOutlined,
  CloudServerOutlined,
  LinkOutlined,
  ReloadOutlined,
  TeamOutlined,
} from '@ant-design/icons';
import {
  Alert,
  Badge,
  Button,
  Card,
  Col,
  Descriptions,
  Row,
  Skeleton,
  Space,
  Statistic,
  Table,
  Tag,
  Typography,
} from 'antd';
import { bandwidth, duration, ErrorBanner, formatDate, PageTitle, tableLocale } from '../components';
import { useResource } from '../hooks';
import type { Device, Overview, Page, User } from '../types';
import { AdminOverview } from './AdminOverview';

interface OverviewProps {
  user: User;
  navigate: (page: 'devices' | 'relay' | 'users' | 'sessions') => void;
}

export function OverviewPage(props: OverviewProps) {
  return props.user.role === 'admin' ? (
    <AdminOverview navigate={props.navigate} />
  ) : (
    <PersonalOverview {...props} />
  );
}

function PersonalOverview({ user, navigate }: OverviewProps) {
  const overview = useResource<Overview>('/overview');
  const devices = useResource<Page<Device>>('/devices?page=1&pageSize=5');
  const data = overview.data;
  const admin = user.role === 'admin';
  const guest = user.role === 'guest';
  const refresh = () => {
    overview.reload();
    devices.reload();
  };
  return (
    <>
      <PageTitle
        title="运行概览"
        description={admin ? '查看中继运行状态与所有用户的接入资源。' : '查看我的设备连接状态和接入资源。'}
        action={
          <Button icon={<ReloadOutlined />} loading={overview.loading} onClick={refresh}>
            刷新数据
          </Button>
        }
      />
      <ErrorBanner error={overview.error} retry={overview.reload} />
      <Row gutter={[16, 16]} className="stats-row">
        {[
          {
            label: '在线设备',
            value: data?.onlineDevices,
            icon: <ApiOutlined />,
            hint: `已登记设备 ${data?.devices ?? '—'}`,
            accent: 'green',
          },
          {
            label: '转发会话',
            value: data?.sessions,
            icon: <LinkOutlined />,
            hint: '当前建立的数据通道',
            accent: 'blue',
          },
          {
            label: '已登记设备',
            value: data?.devices,
            icon: <CloudServerOutlined />,
            hint: admin ? '中继节点登记的设备' : '当前账号绑定的设备',
            accent: 'purple',
          },
          {
            label: admin ? '用户总数' : '设备配额',
            value: admin ? data?.users : user.maxDevices,
            icon: <TeamOutlined />,
            hint: admin ? '平台内已创建的账号' : '允许绑定的最大设备数',
            accent: 'amber',
          },
        ].map((item) => (
          <Col xs={24} sm={12} xl={6} key={item.label}>
            <Card className="stat-card">
              <div className="stat-top">
                <span>{item.label}</span>
                <span className={`stat-icon ${item.accent}`}>{item.icon}</span>
              </div>
              <Skeleton loading={overview.loading && !data} active paragraph={{ rows: 1 }} title={false}>
                <Statistic value={item.value ?? '—'} />
                <Typography.Text type="secondary">{item.hint}</Typography.Text>
              </Skeleton>
            </Card>
          </Col>
        ))}
      </Row>
      <Row gutter={[16, 16]}>
        <Col xs={24} xl={15}>
          <Card
            title={
              <Space>
                <CloudServerOutlined />
                中继服务
              </Space>
            }
            className="full-height"
          >
            <Skeleton loading={overview.loading && !data} active>
              {data ? (
                <>
                  <Descriptions
                    column={{ xs: 1, sm: 2, md: 2 }}
                    layout="vertical"
                    size="small"
                    items={[
                      {
                        key: 'database',
                        label: '数据存储',
                        children: <Tag>{data.relay.databaseDriver.toUpperCase()}</Tag>,
                      },
                      {
                        key: 'redis',
                        label: 'Redis',
                        children: (
                          <Badge
                            status={data.relay.redisEnabled ? 'success' : 'default'}
                            text={data.relay.redisEnabled ? '已启用' : '未启用'}
                          />
                        ),
                      },
                      {
                        key: 'mode',
                        label: '节点准入过滤',
                        children: data.relay.admissionRequired ? '已启用公共令牌过滤' : '未启用令牌过滤',
                      },
                      { key: 'uptime', label: '运行时间', children: duration(data.relay.uptimeSeconds) },
                      {
                        key: 'bandwidth',
                        label: '匿名设备每台默认带宽',
                        children: bandwidth(data.relay.bandwidthBitsPerSecond),
                      },
                      {
                        key: 'accountBandwidth',
                        label: '我的账号总带宽',
                        children: bandwidth(user.bandwidthBitsPerSecond),
                      },
                    ]}
                  />
                  <div className="card-footnote">
                    账号下所有设备共享账号双向合计带宽；匿名设备每台使用节点默认带宽。
                  </div>
                </>
              ) : (
                <Typography.Text type="secondary">暂时无法获取服务信息</Typography.Text>
              )}
            </Skeleton>
          </Card>
        </Col>
        <Col xs={24} xl={9}>
          <Card title={guest ? '我的工作空间' : '开始接入'} className="full-height">
            <Typography.Paragraph type="secondary">
              {guest
                ? '访客账号可查看关联设备和中继会话。'
                : '导入节点公共连接代码，设备接入后可输入自己的设备 ID 绑定账号。'}
            </Typography.Paragraph>
            <div className="quick-actions">
              <Button block icon={<CloudServerOutlined />} onClick={() => navigate('relay')}>
                查看中继接入信息 <ArrowRightOutlined />
              </Button>
              <Button block icon={<ApiOutlined />} onClick={() => navigate('devices')}>
                查看设备 <ArrowRightOutlined />
              </Button>
              <Button block icon={<LinkOutlined />} onClick={() => navigate('sessions')}>
                查看中继会话 <ArrowRightOutlined />
              </Button>
              {admin && (
                <Button block icon={<TeamOutlined />} onClick={() => navigate('users')}>
                  管理用户与配额 <ArrowRightOutlined />
                </Button>
              )}
            </div>
            <Alert
              type="info"
              showIcon
              message={
                guest
                  ? '当前账号仅具有读取权限。'
                  : '可以绑定无主设备，管理员也可分配归属；节点准入令牌统一维护。'
              }
            />
          </Card>
        </Col>
        <Col span={24}>
          <Card
            title="设备快览"
            extra={
              <Button type="link" onClick={() => navigate('devices')}>
                全部设备 <ArrowRightOutlined />
              </Button>
            }
          >
            <ErrorBanner error={devices.error} retry={devices.reload} />
            <Table<Device>
              size="small"
              rowKey="id"
              loading={devices.loading}
              dataSource={devices.data?.items ?? []}
              pagination={false}
              locale={tableLocale}
              scroll={{ x: 640 }}
              columns={[
                {
                  title: '设备',
                  key: 'name',
                  render: (_, device) => (
                    <div className="name-cell">
                      <Typography.Text strong>{device.label || '未命名设备'}</Typography.Text>
                      <Typography.Text type="secondary" className="mono">
                        {device.id}
                      </Typography.Text>
                    </div>
                  ),
                },
                {
                  title: '状态',
                  key: 'state',
                  width: 110,
                  render: (_, device) =>
                    device.disabled ? (
                      <Tag>已禁用</Tag>
                    ) : (
                      <Badge
                        status={device.online ? 'success' : 'default'}
                        text={device.online ? '在线' : '离线'}
                      />
                    ),
                },
                ...(admin
                  ? [
                      {
                        title: '所属用户',
                        dataIndex: 'ownerName',
                        width: 130,
                        render: (name: string) => name || '匿名设备',
                      },
                    ]
                  : []),
                { title: '最近活动', dataIndex: 'lastSeenAt', width: 180, render: formatDate },
              ]}
            />
          </Card>
        </Col>
      </Row>
    </>
  );
}
