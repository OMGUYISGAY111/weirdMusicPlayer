import { defineStore } from 'pinia'
import { ref } from 'vue'
import { host, type SettingsSnapshot } from '@/bridge/host'

/**
 * 设置面板的共享状态：只有「是否打开 / 最近一次快照 / 是否读取中」。
 * 表单编辑和保存逻辑留在 SettingsDialog.vue，这里不掺 UI 细节，
 * 以后要加更多设置分区时只需要复用 refresh()。
 */
export const useSettingsStore = defineStore('settings', () => {
  const open = ref(false)
  const snapshot = ref<SettingsSnapshot | null>(null)
  const loading = ref(false)
  const error = ref('')

  async function refresh(): Promise<void> {
    loading.value = true
    try {
      snapshot.value = await host.getSettings()
      error.value = ''
    } catch (issue) {
      error.value = issue instanceof Error ? issue.message : String(issue)
    } finally {
      loading.value = false
    }
  }

  async function show(): Promise<void> {
    open.value = true
    await refresh()
  }

  function hide(): void {
    open.value = false
  }

  return { open, snapshot, loading, error, show, hide, refresh }
})
