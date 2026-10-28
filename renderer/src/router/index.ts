import { createRouter, createWebHashHistory } from 'vue-router'
import type { RouteRecordRaw } from 'vue-router'

const routes: RouteRecordRaw[] = [
  {
    path: '/',
    name: 'home',
    component: () => import('@/views/HomeView.vue'),
    meta: { title: '首页' },
  },
  {
    path: '/search',
    name: 'search',
    component: () => import('@/views/SearchView.vue'),
    meta: { title: '搜视频' },
  },
  {
    path: '/test',
    name: 'testMain',
    component: () => import('@/views/MainView.vue'),
    meta: { title: 'test'}
  },
  {
    path: '/player',
    name: 'player',
    component: () => import('@/views/PlayerView.vue'),
    meta: { title: '音频播放器' },
  },
  {
    path: '/:pathMatch(.*)*',
    redirect: '/',
  },
]

// CEF 生产环境用 file:// 加载 index.html，只有 hash 路由在 file:// 下可靠，
// 因此这里固定使用 createWebHashHistory（dev server 下同样工作）。
const router = createRouter({
  history: createWebHashHistory(),
  routes,
})

export default router
