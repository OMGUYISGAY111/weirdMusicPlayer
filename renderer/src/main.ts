import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { initHostBridge, host } from './bridge/host'
import './styles/main.css'

async function bootstrap(): Promise<void> {
  // 必须在挂载前完成：CefSharp 的绑定是异步的，绑定好之后 window.hostApi 才存在
  await initHostBridge()
  window.addEventListener('error', (event) => {
    void host.recordPlaybackLog('window_error', { message: event.message, stack: event.error?.stack, source: event.filename, line: event.lineno }, 'error')
  })
  window.addEventListener('unhandledrejection', (event) => {
    void host.recordPlaybackLog('unhandled_rejection', { message: String(event.reason), stack: event.reason?.stack }, 'error')
  })

  const app = createApp(App)
  app.config.errorHandler = (error, _instance, info) => {
    const exception = error instanceof Error ? error : new Error(String(error))
    void host.recordPlaybackLog('vue_error', { message: exception.message, stack: exception.stack, phase: info }, 'error')
    console.error(error)
  }
  app.use(createPinia())
  app.use(router)
  app.mount('#app')
}

void bootstrap()
