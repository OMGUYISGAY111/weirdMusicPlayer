<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import SearchBar from './SearchBar.vue'

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
  background: var(--mp-bg);
}

/* 收起时去掉隐藏控件的占位，实色背景只覆盖主搜索栏。 */
.app-header :deep(.search-control:not(.show-controls) .mode-switch),
.app-header :deep(.search-control:not(.show-controls) .mode-caption) {
  height: 0;
  margin: 0;
  overflow: hidden;
}
</style>
