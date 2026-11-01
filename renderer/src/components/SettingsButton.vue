<script setup lang="ts">
import { useSettingsStore } from '@/stores/settings'

/**
 * 设置入口按钮：只负责打开 SettingsDialog（对话框在 App.vue 里挂一次）。
 * 颜色只用 styles/main.css 的 --mp-* 令牌。
 */
withDefaults(defineProps<{ label?: string }>(), { label: '设置' })

const settings = useSettingsStore()

function open(): void {
  void settings.show()
}
</script>

<template>
  <button type="button" class="settings-button" :aria-label="label" :title="label" @click="open">
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
      <circle cx="12" cy="12" r="3.1" />
      <path d="M19.1 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-1.8-.3 1.6 1.6 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.2a1.6 1.6 0 0 0-1-1.5 1.6 1.6 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.6 1.6 0 0 0 .3-1.8 1.6 1.6 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.2a1.6 1.6 0 0 0 1.5-1 1.6 1.6 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.6 1.6 0 0 0 1.8.3H9a1.6 1.6 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.2a1.6 1.6 0 0 0 1 1.5 1.6 1.6 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0-.3 1.8V10a1.6 1.6 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.2a1.6 1.6 0 0 0-1.5 1Z" />
    </svg>
    <span>{{ label }}</span>
  </button>
</template>

<style scoped>
.settings-button {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 5px 12px 5px 9px;
  font-size: 12.5px;
  line-height: 1.6;
  border-radius: 999px;
  border: 1px solid var(--mp-border);
  background: var(--mp-bg);
  color: var(--mp-text);
  white-space: nowrap;
}

.settings-button svg {
  width: 15px;
  height: 15px;
  flex: 0 0 auto;
}

.settings-button:hover {
  border-color: var(--mp-accent);
  color: var(--mp-accent);
}

.settings-button:focus-visible {
  outline: 2px solid var(--mp-accent);
  outline-offset: 2px;
}
</style>
