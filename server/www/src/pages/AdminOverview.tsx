// SPDX-License-Identifier: GPL-3.0-only
import type { ReactNode } from 'react';
import {
  ApiOutlined,
  CloudServerOutlined,
  DashboardOutlined,
  DownloadOutlined,
  LinkOutlined,
  ReloadOutlined,
  TeamOutlined,
  UploadOutlined,
} from '@ant-design/icons';
import {
  Badge,
  Button,
  Card,
  Col,
  Descriptions,
  Progress,
  Row,
  Skeleton,
  Space,
  Statistic,
  Tag,
  Typography,
} from 'antd';
import { bandwidth, duration, ErrorBanner, PageTitle } from '../components';
import { useResource } from '../hooks';
import type { Overview } from '../types';

type Destination = 'devices' | 'relay' | 'users' | 'sessions';
const unavailable = <Typography.Text type="secondary">暂不可用</Typography.Text>;
const finite = (value: number | null | undefined): value is number =>
  value !== null && value !== undefined && Number.isFinite(value);

function bytes(value: number | null | undefined) {
  if (!finite(value)) return '暂不可用';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  const unit = value > 0 ? Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1) : 0;
  return `${new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2 }).format(value / 1024 ** Math.max(0, unit))} ${units[Math.max(0, unit)]}`;
}

function sampledTime(value?: string) {
  if (!value) return '等待采样';
  const time = new Date(value);
  return Number.isNaN(time.getTime()) ? '等待采样' : time.toLocaleTimeString('zh-CN', { hour12: false });
}

function Rate({
  title,
  value,
  prefix,
  prominent = false,
}: {
  title: string;
  value?: number;
  prefix?: ReactNode;
  prominent?: boolean;
}) {
  return (
    <Statistic
      className={prominent ? 'primary-rate' : undefined}
      title={title}
      value={finite(value) ? value / 1_000_000 : '—'}
      precision={finite(value) ? 3 : undefined}
      suffix={finite(value) ? 'Mbps' : undefined}
      prefix={prefix}
    />
  );
}

export function AdminOverview({ navigate }: { navigate: (page: Destination) => void }) {
  const overview = useResource<Overview>('/overview', 2000);
  const data = overview.data;
  const metrics = data?.server;
  const traffic = metrics?.traffic;
  const initialLoading = overview.loading && !data;
  const memoryPercent =
    finite(metrics?.memoryUsedBytes) && finite(metrics?.memoryTotalBytes) && metrics.memoryTotalBytes > 0
      ? (metrics.memoryUsedBytes / metrics.memoryTotalBytes) * 100
      : undefined;
  const cumulative = traffic ? traffic.receivedBytes + traffic.sentBytes : undefined;
  return (
    <>
      <PageTitle
        title="服务器概览"
        description="关注服务器负载与中继数据带宽，数据每 2 秒自动刷新。"
        action={
          <Button icon={<ReloadOutlined />} loading={overview.loading} onClick={overview.reload}>
            立即刷新
          </Button>
        }
      />
      <ErrorBanner
        error={overview.error ? `${overview.error}${data ? ' 当前保留最近一次成功采样。' : ''}` : ''}
        retry={overview.reload}
      />
      <div className="metrics-status">
        <Space wrap>
          <Badge
            status={overview.error ? 'warning' : metrics ? 'success' : 'default'}
            text={overview.error ? '等待恢复采样' : '自动刷新'}
          />
          <Typography.Text type="secondary">最近采样 {sampledTime(metrics?.sampledAt)}</Typography.Text>
        </Space>
        <Typography.Text type="secondary">服务器视角 · RX 接收 / TX 发出</Typography.Text>
      </div>
      <Row gutter={[16, 16]} className="monitor-row">
        <Col xs={24} sm={12} xl={6}>
          <Card
            title={
              <Space>
                <DashboardOutlined />
                CPU 利用率
              </Space>
            }
            className="monitor-card full-height"
          >
            <Skeleton active loading={initialLoading} paragraph={{ rows: 2 }} title={false}>
              {finite(metrics?.cpuPercent) ? (
                <>
                  <Statistic value={metrics.cpuPercent} precision={1} suffix="%" />
                  <Progress
                    percent={Math.max(0, Math.min(100, metrics.cpuPercent))}
                    showInfo={false}
                    strokeColor="#78a8ff"
                  />
                  <Typography.Text type="secondary">系统 CPU</Typography.Text>
                </>
              ) : (
                <div className="metric-unavailable">{unavailable}</div>
              )}
            </Skeleton>
          </Card>
        </Col>
        <Col xs={24} sm={12} xl={6}>
          <Card title="系统内存" className="monitor-card full-height">
            <Skeleton active loading={initialLoading} paragraph={{ rows: 2 }} title={false}>
              {finite(memoryPercent) ? (
                <>
                  <Statistic value={memoryPercent} precision={1} suffix="%" />
                  <Progress
                    percent={Math.max(0, Math.min(100, memoryPercent))}
                    showInfo={false}
                    strokeColor="#69c7ab"
                  />
                  <Typography.Text type="secondary">
                    {bytes(metrics?.memoryUsedBytes)} / {bytes(metrics?.memoryTotalBytes)}
                  </Typography.Text>
                </>
              ) : (
                <div className="metric-unavailable">{unavailable}</div>
              )}
            </Skeleton>
          </Card>
        </Col>
        <Col xs={24} xl={12}>
          <Card
            title="当前中继数据带宽"
            className="monitor-card full-height"
            extra={
              <Tag color="blue" bordered={false}>
                RX + TX
              </Tag>
            }
          >
            <Skeleton active loading={initialLoading} paragraph={{ rows: 2 }} title={false}>
              <Rate title="接收与发出合计" value={traffic?.totalBitsPerSecond} prominent />
              <Row gutter={[16, 12]} className="traffic-directions">
                <Col xs={24} sm={12}>
                  <Rate
                    title="RX · 客户端 → 中继"
                    value={traffic?.receivedBitsPerSecond}
                    prefix={<DownloadOutlined />}
                  />
                </Col>
                <Col xs={24} sm={12}>
                  <Rate
                    title="TX · 中继 → 客户端"
                    value={traffic?.sentBitsPerSecond}
                    prefix={<UploadOutlined />}
                  />
                </Col>
              </Row>
            </Skeleton>
          </Card>
        </Col>
        <Col xs={24} xl={12}>
          <Card title="系统平均负载" className="full-height">
            <Skeleton active loading={initialLoading} paragraph={{ rows: 1 }} title={false}>
              <Row gutter={[14, 14]}>
                {[1, 5, 15].map((minutes, index) => {
                  const value = metrics?.loadAverage?.[index];
                  return (
                    <Col span={8} key={minutes}>
                      <Statistic
                        title={`${minutes} 分钟`}
                        value={finite(value) ? value : '—'}
                        precision={finite(value) ? 2 : undefined}
                      />
                      {!finite(value) && unavailable}
                    </Col>
                  );
                })}
              </Row>
              <div className="card-footnote">系统运行队列的平均负载，不是 CPU 百分比。</div>
            </Skeleton>
          </Card>
        </Col>
        <Col xs={24} xl={12}>
          <Card title="累计中继数据" className="full-height">
            <Skeleton active loading={initialLoading} paragraph={{ rows: 1 }} title={false}>
              <Row gutter={[14, 14]}>
                <Col xs={24} sm={8}>
                  <Statistic title="RX 接收" value={bytes(traffic?.receivedBytes)} />
                </Col>
                <Col xs={24} sm={8}>
                  <Statistic title="TX 成功发出" value={bytes(traffic?.sentBytes)} />
                </Col>
                <Col xs={24} sm={8}>
                  <Statistic title="合计" value={bytes(cumulative)} />
                </Col>
              </Row>
              <div className="card-footnote">自中继进程启动累计，重启后重新计数。</div>
            </Skeleton>
          </Card>
        </Col>
      </Row>
      <div className="traffic-note">
        带宽和累计字节仅统计加密数据 body 的接收与成功发送，不含网卡其他流量、WebSocket / TLS
        包头。空闲时速率归零；一次中继转发的 RX 与 TX 分别计数。
      </div>
      <Row gutter={[16, 16]} className="monitor-row">
        {[
          {
            title: '在线设备',
            value: data?.onlineDevices,
            icon: <ApiOutlined />,
            target: 'devices' as const,
          },
          { title: '中继会话', value: data?.sessions, icon: <LinkOutlined />, target: 'sessions' as const },
          {
            title: '已登记设备',
            value: data?.devices,
            icon: <CloudServerOutlined />,
            target: 'devices' as const,
          },
          { title: '用户总数', value: data?.users, icon: <TeamOutlined />, target: 'users' as const },
        ].map((item) => (
          <Col xs={12} xl={6} key={item.title}>
            <Card size="small" className="secondary-metric">
              <Statistic
                title={
                  <Button type="link" size="small" onClick={() => navigate(item.target)}>
                    {item.title}
                  </Button>
                }
                value={item.value ?? '—'}
                prefix={item.icon}
              />
            </Card>
          </Col>
        ))}
      </Row>
      <Card
        title="运行环境"
        extra={
          <Button type="link" onClick={() => navigate('relay')}>
            中继接入与设置
          </Button>
        }
      >
        <Descriptions
          size="small"
          column={{ xs: 1, sm: 2, xl: 3 }}
          items={[
            {
              key: 'host-uptime',
              label: '系统运行时间',
              children: finite(metrics?.uptimeSeconds) ? duration(metrics.uptimeSeconds) : unavailable,
            },
            {
              key: 'process-uptime',
              label: '中继进程运行',
              children: data ? duration(data.relay.uptimeSeconds) : unavailable,
            },
            { key: 'heap', label: 'Go 堆内存', children: bytes(metrics?.heapBytes) },
            { key: 'goroutines', label: 'Goroutine 数', children: metrics?.goroutines ?? unavailable },
            {
              key: 'database',
              label: '数据存储',
              children: data?.relay.databaseDriver.toUpperCase() ?? unavailable,
            },
            {
              key: 'redis',
              label: 'Redis',
              children: data ? (
                <Badge
                  status={data.relay.redisEnabled ? 'success' : 'default'}
                  text={data.relay.redisEnabled ? '已启用' : '未启用'}
                />
              ) : (
                unavailable
              ),
            },
            {
              key: 'default',
              label: '匿名设备每台默认',
              children: data ? bandwidth(data.relay.bandwidthBitsPerSecond) : unavailable,
            },
            {
              key: 'admission',
              label: '公共准入过滤',
              children: data ? (data.relay.admissionRequired ? '已启用' : '未启用') : unavailable,
            },
          ]}
        />
      </Card>
    </>
  );
}
