<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import type { PlaylistRecord } from '@/playlist/library'

/**
 * 播放列表栏：切换 / 新建 / 重命名 / 删除 / 导入 / 导出。
 * 只负责交互和展示，数据与副作用都在 store 里，配色只用 --mp-* 令牌。
 */
const props = defineProps<{
  playlists: readonly PlaylistRecord[]
  activeId: string
  /** 播放队列对应的列表 id（切换选中不会改它） */
  playerId?: string
  /** 播放队列正在解析音频 */
  busy?: boolean
  /** 导入 / 导出进行中 */
  transferBusy?: boolean
}>()

const emit = defineEmits<{
  select: [id: string]
  create: []
  rename: [id: string, name: string]
  remove: [id: string]
  import: []
  export: []
}>()

const editingId = ref('')
const deletingId = ref('')
const draft = ref('')
const nameInput = ref<HTMLInputElement | null>(null)

function setNameInput(el: unknown): void {
  nameInput.value = el instanceof HTMLInputElement ? el : null
}

async function startRename(id: string): Promise<void> {
  const record = props.playlists.find((playlist) => playlist.id === id)
  if (!record) return
  deletingId.value = ''
  editingId.value = id
  draft.value = record.name
  await nextTick()
  nameInput.value?.focus()
  nameInput.value?.select()
}

function commitRename(): void {
  if (!editingId.value) return
  const id = editingId.value
  const name = draft.value.trim()
  editingId.value = ''
  if (!name) return
  if (name !== props.playlists.find((playlist) => playlist.id === id)?.name) emit('rename', id, name)
}

function cancelRename(): void {
  editingId.value = ''
}

function askDelete(id: string): void {
  editingId.value = ''
  deletingId.value = id
}

function confirmDelete(): void {
  const id = deletingId.value
  deletingId.value = ''
  if (id) emit('remove', id)
}

function cancelDelete(): void {
  deletingId.value = ''
}

function onWindowPointerDown(event: PointerEvent): void {
  // 点别处时收起删除确认，避免误删按钮一直挂着
  const target = event.target
  if (!(target instanceof Element)) return
  if (deletingId.value && !target.closest('.tab-shell')) deletingId.value = ''
}

onMounted(() => document.addEventListener('pointerdown', onWindowPointerDown, true))
onBeforeUnmount(() => document.removeEventListener('pointerdown', onWindowPointerDown, true))
</script>

<template>
  <section class="library" aria-label="我的播放列表">
    <div class="tabs">
      <div
        v-for="playlist in playlists"
        :key="playlist.id"
        class="tab-shell"
        :class="{ current: playlist.id === activeId, deleting: playlist.id === deletingId }"
      >
        <template v-if="editingId === playlist.id">
          <input
            :ref="setNameInput"
            v-model="draft"
            class="rename-input"
            maxlength="40"
            aria-label="播放列表名称"
            @keyup.enter="commitRename"
            @keyup.esc="cancelRename"
            @blur="commitRename"
          />
        </template>

        <template v-else-if="deletingId === playlist.id">
          <span class="confirm-text">删除「{{ playlist.name }}」？</span>
          <button type="button" class="mini danger" @click="confirmDelete">删除</button>
          <button type="button" class="mini" @click="cancelDelete">取消</button>
        </template>

        <template v-else>
          <button
            type="button"
            class="tab"
            :aria-current="playlist.id === activeId ? 'true' : undefined"
            :title="playlist.name"
            @click="emit('select', playlist.id)"
          >
            <span
              v-if="playlist.id === playerId"
              class="tab-queue"
              :class="{ busy }"
              :title="busy ? '正在解析音频' : '当前播放队列'"
              aria-label="当前播放队列"
            >{{ busy ? '·' : '♪' }}</span>
            <span class="tab-name">{{ playlist.name }}</span>
            <span class="tab-count">{{ playlist.items.length }}</span>
          </button>
          <button
            v-if="playlist.id === activeId"
            type="button"
            class="icon"
            aria-label="重命名列表"
            title="重命名列表"
            @click="startRename(playlist.id)"
          >✎</button>
          <button
            v-if="playlist.id === activeId && playlists.length > 1"
            type="button"
            class="icon"
            aria-label="删除列表"
            title="删除列表"
            @click="askDelete(playlist.id)"
          >✕</button>
        </template>
      </div>

      <button type="button" class="add" @click="emit('create')">＋ 新建列表</button>
    </div>

    <div class="actions">
      <button type="button" :disabled="transferBusy" @click="emit('import')">导入</button>
      <button type="button" :disabled="transferBusy" @click="emit('export')">导出</button>
    </div>
  </section>
</template>

<style scoped>
.library {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  padding: 8px 10px;
  border: 1px solid var(--mp-border);
  border-radius: 14px;
  background: var(--mp-panel);
}

.tabs {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
  min-width: 0;
}

.tab-shell {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  padding: 2px 4px 2px 2px;
  border-radius: 10px;
  border: 1px solid transparent;
}

.tab-shell.current {
  border-color: var(--mp-border);
  background: var(--mp-accent-soft);
}

.tab-shell.deleting {
  gap: 6px;
  border-color: color-mix(in srgb, var(--mp-danger) 45%, transparent);
  background: color-mix(in srgb, var(--mp-danger) 10%, transparent);
}

.tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  max-width: 240px;
  padding: 5px 10px;
  font-size: 12.5px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--mp-dim);
}

.tab:hover {
  background: var(--mp-hover);
  color: var(--mp-text);
  border-color: transparent;
  transform: none;
}

.tab-shell.current .tab {
  color: var(--mp-text);
  font-weight: 600;
}

.tab-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.tab-count {
  font-size: 11px;
  color: var(--mp-dim);
  font-variant-numeric: tabular-nums;
}

/* 播放队列标记：切换选中列表不会影响它 */
.tab-queue {
  color: var(--mp-accent);
  font-size: 11px;
  line-height: 1;
}

.tab-queue.busy {
  color: var(--mp-warn);
  animation: tab-queue-pulse 1s ease-in-out infinite;
}

@keyframes tab-queue-pulse {
  50% { opacity: 0.35; }
}

.icon {
  width: 22px;
  height: 22px;
  padding: 0;
  display: grid;
  place-items: center;
  font-size: 11px;
  line-height: 1;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--mp-dim);
}

.icon:hover {
  background: var(--mp-track);
  color: var(--mp-text);
  border-color: transparent;
  transform: none;
}

.add {
  padding: 5px 10px;
  font-size: 12.5px;
  border-radius: 8px;
  border: 1px dashed var(--mp-border);
  background: transparent;
  color: var(--mp-dim);
}

.add:hover {
  border-color: var(--mp-accent);
  color: var(--mp-accent);
  background: transparent;
}

.rename-input {
  width: 160px;
  padding: 4px 8px;
  font-size: 12.5px;
  border-radius: 8px;
}

.confirm-text {
  font-size: 12px;
  color: var(--mp-danger);
  white-space: nowrap;
}

.mini {
  padding: 3px 8px;
  font-size: 11.5px;
  border-radius: 6px;
}

.mini.danger {
  color: var(--mp-danger);
  border-color: color-mix(in srgb, var(--mp-danger) 45%, transparent);
}

.mini.danger:hover {
  background: color-mix(in srgb, var(--mp-danger) 12%, transparent);
  border-color: var(--mp-danger);
}

.actions {
  display: flex;
  gap: 6px;
}

.actions button {
  padding: 5px 12px;
  font-size: 12.5px;
  border-radius: 8px;
}
</style>
