import { createApp } from 'vue'
import { createPinia } from 'pinia'

import App from './App.vue'
import router from './router'
import { initHostBridge } from './bridge/host'
import './styles/main.css'

async function bootstrap(): Promise<void> {
  // 必须在挂载前完成：CefSharp 的绑定是异步的，绑定好之后 window.hostApi 才存在
  await initHostBridge()

  const app = createApp(App)
  app.use(createPinia())
  app.use(router)
  app.mount('#app')
}

void bootstrap()
