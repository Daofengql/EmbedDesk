// SPDX-License-Identifier: GPL-3.0-only
let csrfToken = '';

export function setCsrfToken(value: string) {
  csrfToken = value;
}

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message);
  }
}

export async function api<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers = new Headers(options.headers);
  if (options.body) headers.set('Content-Type', 'application/json');
  if (options.method && options.method !== 'GET' && csrfToken) headers.set('X-CSRF-Token', csrfToken);
  const response = await fetch(`/api/console${path}`, {
    ...options,
    headers,
    credentials: 'same-origin',
    cache: 'no-store',
  });
  if (!response.ok) {
    const payload = (await response.json().catch(() => ({}))) as { error?: string };
    if (response.status === 401 && path !== '/auth/login' && path !== '/auth/me') {
      window.dispatchEvent(new Event('console-session-expired'));
    }
    throw new ApiError(payload.error || `请求失败（${response.status}）`, response.status);
  }
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

export function json(method: string, body?: unknown): RequestInit {
  return {
    method,
    ...(method === 'GET' || method === 'HEAD' ? {} : { body: JSON.stringify(body ?? {}) }),
  };
}

export function errorText(error: unknown): string {
  if (error instanceof TypeError) return '无法连接服务器，请检查网络后重试。';
  return error instanceof Error ? error.message : '操作失败，请重试。';
}
