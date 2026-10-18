// SPDX-License-Identifier: GPL-3.0-only
export interface User {
  id: number;
  username: string;
  displayName: string;
  role: 'admin' | 'user' | 'guest';
  disabled: boolean;
  maxDevices: number;
  bandwidthBitsPerSecond: number;
  createdAt: string;
}

export interface AuthSession {
  user: User;
  csrfToken: string;
}

export interface Device {
  id: string;
  ownerId: number | null;
  ownerName: string;
  label: string;
  disabled: boolean;
  online: boolean;
  createdAt: string;
  lastSeenAt: string | null;
}

export interface RelaySettings {
  name: string;
  publicUrl: string;
  admissionRequired: boolean;
  connectionCode: string;
  admissionToken: string;
  defaultBandwidthBitsPerSecond: number;
}
export interface AuditEvent {
  id: number;
  actorId: number | null;
  actorName: string;
  action: string;
  target: string;
  createdAt: string;
}
export interface RelaySession {
  id: string;
  originDeviceId: string;
  targetDeviceId: string;
  accepted: boolean;
  stage: number | string;
  createdAt: string;
  expiresAt: string | null;
}
export interface Page<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}
export interface Overview {
  users: number;
  devices: number;
  onlineDevices: number;
  sessions: number;
  server?: ServerMetrics;
  relay: {
    databaseDriver: string;
    redisEnabled: boolean;
    bandwidthBitsPerSecond: number;
    uptimeSeconds: number;
    admissionRequired: boolean;
    name?: string;
    publicUrl?: string;
  };
}

export interface ServerMetrics {
  sampledAt: string;
  cpuPercent: number | null;
  memoryTotalBytes: number | null;
  memoryUsedBytes: number | null;
  loadAverage: number[] | null;
  uptimeSeconds: number | null;
  goroutines: number;
  heapBytes: number;
  traffic: {
    receivedBitsPerSecond: number;
    sentBitsPerSecond: number;
    totalBitsPerSecond: number;
    receivedBytes: number;
    sentBytes: number;
  };
}
