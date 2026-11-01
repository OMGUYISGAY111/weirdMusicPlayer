<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import SearchBar from './SearchBar.vue'
import SettingsButton from './SettingsButton.vue'

withDefaults(defineProps<{ busy?: boolean; canSubmit?: boolean }>(), {
  busy: false,
  canSubmit: true,
})

const song = defineModel<string>('song', { default: '' })
const artist = defineModel<string>('artist', { default: '' })
const album = defineModel<string>('album', { default: '' })
const url = defineModel<string>('url', { default: '' })
const mode = defineModel<'song' | 'link'>('mode', { default: 'song' })
const emit = defineEmits<{
  submit: []
  /** 页面只需为主搜索框留白，展开内容悬浮显示。 */
  dockResize: [height: number]
}>()

const header = ref<HTMLElement | null>(null)
let observer: ResizeObserver | undefined

onMounted(() => {
  const capsule = header.value?.querySelector<HTMLElement>('.search-capsule')
  if (!capsule) return
  const updateHeight = () => emit('dockResize', capsule.getBoundingClientRect().bottom + 18)
  observer = new ResizeObserver(updateHeight)
  observer.observe(capsule)
  updateHeight()
})

onBeforeUnmount(() => observer?.disconnect())
</script>

<template>
  <header ref="header" class="app-header" aria-label="音乐搜索">
    <div class="header-row">
      <!-- 左右两块等宽占位：搜索栏保持居中，设置按钮贴在 header 右边缘 -->
      <span class="header-slot" aria-hidden="true" />
      <SearchBar
        v-model:song="song"
        v-model:artist="artist"
        v-model:album="album"
        v-model:url="url"
        v-model:mode="mode"
        :busy="busy"
        :can-submit="canSubmit"
        @submit="emit('submit')"
      />
      <div class="header-slot header-actions">
        <SettingsButton />
      </div>
    </div>
  </header>
</template>

<style scoped>
.app-header {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  z-index: 30;
  display: flow-root;
  padding: 12px 28px 0;
  max-height: 100dvh;
  /* 搜索栏展开的「选模式」面板会溢出到 header 下方，这里不能裁剪。 */
  overflow: visible;
  background: var(--mp-page);
}

.header-row {
  display: flex;
  align-items: flex-start;
  gap: 16px;
}

/* 搜索栏自己带 auto 外边距居中，这里改成固定宽度，居中交给两侧等宽占位。 */
.header-row :deep(.search-control) {
  flex: 0 1 680px;
  min-width: 0;
  margin: 14px 0 18px;
}

.header-slot {
  flex: 1 1 0;
  min-width: 0;
}

/* 与搜索胶囊（上边距 14、高度 40）垂直居中；窄窗口里按钮不能被压扁，
   收缩先发生在搜索栏和左侧占位身上。 */
.header-actions {
  display: flex;
  justify-content: flex-end;
  padding-top: 18px;
  min-width: max-content;
}

/* 收起时去掉隐藏控件的占位，实色背景只覆盖主搜索栏。 */
.app-header :deep(.search-control:not(.show-controls) .mode-switch),
.app-header :deep(.search-control:not(.show-controls) .mode-caption) {
  height: 0;
  margin: 0;
  overflow: hidden;
}
</style>
