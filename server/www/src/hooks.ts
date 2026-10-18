// SPDX-License-Identifier: GPL-3.0-only
import { useCallback, useEffect, useState } from 'react';
import { api, errorText } from './api';
import type { Page } from './types';

export function useResource<T>(path: string, pollInterval = 0) {
  const [data, setData] = useState<T>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [revision, setRevision] = useState(0);
  const reload = useCallback(() => setRevision((value) => value + 1), []);

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    setLoading(true);
    setError('');
    const request = () =>
      api<T>(path, { signal: controller.signal })
        .then((value) => {
          if (!controller.signal.aborted) {
            setData(value);
            setError('');
          }
        })
        .catch((reason) => {
          if (!controller.signal.aborted) setError(errorText(reason));
        })
        .finally(() => {
          if (!controller.signal.aborted) {
            setLoading(false);
            if (pollInterval > 0) timer = setTimeout(() => void request(), pollInterval);
          }
        });
    void request();
    return () => {
      controller.abort();
      if (timer) clearTimeout(timer);
    };
  }, [path, revision, pollInterval]);

  return { data, loading, error, reload, requestKey: `${path}:${revision}` };
}

export function usePage<T>(path: string, initialPageSize = 20) {
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(initialPageSize);
  const [query, setQuery] = useState('');
  const result = useResource<Page<T>>(
    `${path}?${new URLSearchParams({ page: String(page), pageSize: String(pageSize), q: query })}`,
  );
  const search = (value: string) => {
    setPage(1);
    setQuery(value.trim());
  };
  const pagination = {
    current: page,
    pageSize,
    total: result.data?.total ?? 0,
    showSizeChanger: true,
    pageSizeOptions: [10, 20, 50, 100],
    showTotal: (total: number) => `共 ${total} 项`,
    onChange: (nextPage: number, nextSize: number) => {
      setPage(nextSize !== pageSize ? 1 : nextPage);
      setPageSize(nextSize);
    },
  };
  return { ...result, search, pagination };
}
