// SPDX-License-Identifier: GPL-3.0-only
import React, { useSyncExternalStore } from 'react';
import ReactDOM from 'react-dom/client';
import { App as AntApp, ConfigProvider, theme } from 'antd';
import zhCN from 'antd/locale/zh_CN';
import dayjs from 'dayjs';
import 'dayjs/locale/zh-cn';
import ConsoleApp from './App';
import 'antd/dist/reset.css';
import './styles.css';

dayjs.locale('zh-cn');

const reducedMotionQuery = window.matchMedia('(prefers-reduced-motion: reduce)');
const subscribeMotionPreference = (onChange: () => void) => {
  reducedMotionQuery.addEventListener('change', onChange);
  return () => reducedMotionQuery.removeEventListener('change', onChange);
};
const getReducedMotion = () => reducedMotionQuery.matches;

function ConsoleRoot() {
  const reducedMotion = useSyncExternalStore(subscribeMotionPreference, getReducedMotion);
  return (
    <ConfigProvider
      locale={zhCN}
      theme={{
        algorithm: theme.darkAlgorithm,
        token: {
          // Let the component library disable motion without animating the
          // temporary coordinates used to measure and align popup menus.
          motion: !reducedMotion,
          colorPrimary: '#6a9fff',
          colorBgBase: '#0b1018',
          colorBgContainer: '#131b27',
          colorBorder: '#2a3546',
          colorText: '#e8edf5',
          colorTextSecondary: '#9aa9bd',
          borderRadius: 8,
          fontFamily: 'Inter, "Segoe UI", "Microsoft YaHei UI", sans-serif',
          controlHeight: 36,
          fontSize: 14,
        },
        components: {
          Layout: { bodyBg: '#0b1018', siderBg: '#101722', headerBg: '#101722' },
          Table: { headerBg: '#182231', cellPaddingBlock: 15 },
          Menu: {
            darkItemBg: '#101722',
            darkItemSelectedBg: '#213757',
            darkItemColor: '#a7b6c9',
            darkItemSelectedColor: '#b8d1ff',
          },
          Modal: { contentBg: '#131b27', headerBg: '#131b27' },
          Card: { headerFontSize: 15 },
        },
      }}
    >
      <AntApp>
        <ConsoleApp />
      </AntApp>
    </ConfigProvider>
  );
}

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ConsoleRoot />
  </React.StrictMode>,
);
