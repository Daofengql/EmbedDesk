// SPDX-License-Identifier: GPL-3.0-only
import type { Rule } from 'antd/es/form';

export const roleNames = { admin: '系统管理员', user: '注册用户', guest: '访客' };
export const usernameRules: Rule[] = [
  { required: true, message: '请输入用户名' },
  {
    pattern: /^[a-z0-9][a-z0-9_.-]{2,47}$/,
    message: '3–48 位小写字母、数字、下划线、点或短横线，首位须为字母或数字',
  },
];
export function passwordRules(required: boolean): Rule[] {
  return [
    { required, message: '请输入密码' },
    {
      validator: (_, value: string | undefined) => {
        if (!value) return Promise.resolve();
        const bytes = new TextEncoder().encode(value).length;
        return bytes >= 12 && bytes <= 72
          ? Promise.resolve()
          : Promise.reject(new Error('密码须为 12–72 个 UTF-8 字节；汉字通常占 3 字节'));
      },
    },
  ];
}
export const nameRules: Rule[] = [
  {
    validator: (_, value: string | undefined) =>
      !value || Array.from(value).length <= 80
        ? Promise.resolve()
        : Promise.reject(new Error('最多 80 个字符')),
  },
];
