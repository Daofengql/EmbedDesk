// SPDX-License-Identifier: GPL-3.0-only
import { useCallback, useEffect, useRef, useState } from 'react';
import { Select } from 'antd';
import { api, errorText } from '../api';
import { ErrorBanner } from '../components';
import type { Page, User } from '../types';
import { roleNames } from '../validation';

interface OwnerOption {
  value: number;
  label: string;
  disabled?: boolean;
}

export function OwnerSelect({
  value,
  id,
  onChange,
  disabled,
  currentOwnerName,
}: {
  value?: number;
  id?: string;
  onChange?: (value: number | undefined) => void;
  disabled?: boolean;
  currentOwnerName?: string;
}) {
  const [options, setOptions] = useState<OwnerOption[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const abort = useRef<AbortController | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const selected = useRef<OwnerOption | undefined>(undefined);
  const search = useCallback(async (query = '') => {
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setLoading(true);
    setError('');
    try {
      const result = await api<Page<User>>(
        `/users?${new URLSearchParams({ page: '1', pageSize: '100', q: query })}`,
        { signal: controller.signal },
      );
      if (!controller.signal.aborted) {
        const items = result.items.map((user) => ({
          value: user.id,
          label: `${user.displayName || user.username} (${user.username}) · ${roleNames[user.role]}`,
          disabled: user.disabled,
        }));
        if (selected.current && !items.some((item) => item.value === selected.current?.value))
          items.unshift({ ...selected.current, disabled: selected.current.disabled ?? false });
        setOptions(items);
      }
    } catch (reason) {
      if (!controller.signal.aborted) setError(errorText(reason));
    } finally {
      if (!controller.signal.aborted) setLoading(false);
    }
  }, []);
  useEffect(() => {
    selected.current =
      value === undefined
        ? undefined
        : selected.current?.value === value
          ? selected.current
          : {
              value,
              label: currentOwnerName || `用户 #${value}`,
            };
  }, [value, currentOwnerName]);
  useEffect(() => {
    void search();
    return () => {
      abort.current?.abort();
      if (timer.current) clearTimeout(timer.current);
    };
  }, [search]);
  return (
    <>
      <Select<number, OwnerOption>
        id={id}
        value={value}
        onChange={(nextValue, option) => {
          selected.current = Array.isArray(option) ? undefined : option;
          onChange?.(nextValue);
        }}
        disabled={disabled}
        allowClear
        showSearch
        filterOption={false}
        loading={loading}
        options={options}
        placeholder="匿名设备（未绑定）"
        style={{ width: '100%' }}
        onSearch={(query) => {
          if (timer.current) clearTimeout(timer.current);
          timer.current = setTimeout(() => void search(query), 250);
        }}
      />
      <ErrorBanner error={error} retry={disabled ? undefined : () => void search()} />
    </>
  );
}
