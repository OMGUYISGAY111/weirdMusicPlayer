import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// CEF 宿主在不同场景下用两种方式加载页面：
//   dev  -> http://127.0.0.1:5173  (Vite dev server，热更新)
//   prod -> file://.../renderer/dist/index.html (打包产物)
// 因此 base 必须是相对路径，否则 file:// 下资源会 404。
export default defineConfig({
  base: './',
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    // CEF 154 内核远新于该目标，保守设置即可
    target: 'chrome120',
    assetsDir: 'assets',
    sourcemap: false,
  },
})
