import { computed, onScopeDispose, shallowRef } from 'vue'
import { defineStore } from 'pinia'
import { createPlaylist } from '../playlist/playlist'
import { resolveTrack } from '../playlist/hostAdapter'
import { createPlaylistPersistence } from '../playlist/persistence'

export const usePlaylistStore = defineStore('playlist', () => {
  const storageError = shallowRef('')
  const persistence = createPlaylistPersistence(() => window.localStorage, (message) => { storageError.value = message })
  const queue = createPlaylist(resolveTrack, { initial: persistence.load() })
  const state = shallowRef(queue.snapshot())
  onScopeDispose(queue.subscribe((value) => { state.value = value }))
  onScopeDispose(persistence.connect(queue))
  onScopeDispose(() => queue.dispose())
  const currentTrack = computed(() => state.value.items.find((item) => item.id === state.value.currentId))
  return { state, currentTrack, storageError, ...queue }
})
