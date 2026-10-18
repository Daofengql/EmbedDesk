import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  base: '/console/',
  plugins: [react()],
  server: { proxy: { '/api': 'http://127.0.0.1:7080' } },
  build: {
    sourcemap: false,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes('node_modules')) return;
          if (/node_modules\/(react|react-dom|scheduler)\//.test(id)) return 'react';
          if (/node_modules\/(antd|@ant-design\/icons|@ant-design\/icons-svg)\//.test(id)) return 'antd';
          return 'components';
        },
      },
    },
  },
});
