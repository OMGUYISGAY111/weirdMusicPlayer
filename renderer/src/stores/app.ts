import { defineStore } from 'pinia'
import { ref } from 'vue'

/**
 * 演示用的全局状态。后续播放队列、当前曲目、播放状态都可以放在这里。
 */
export const useAppStore = defineStore('app', () => {
  const playCount = ref(0)
  const currentTrack = ref('（未播放）')

  function tick() {
    playCount.value += 1
  }

  function setTrack(name: string) {
    currentTrack.value = name
  }

  return { playCount, currentTrack, tick, setTrack }
})
