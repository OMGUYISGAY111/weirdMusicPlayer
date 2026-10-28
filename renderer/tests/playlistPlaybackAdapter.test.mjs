import test from 'node:test'
import assert from 'node:assert/strict'
import { createPlaylist } from '../src/playlist/playlist.ts'
import { connectPlaylistPlayback } from '../src/playlist/playbackAdapter.ts'

const flush = () => new Promise((resolve) => setImmediate(resolve))

function createPlayer() {
  const listeners = new Map()
  const element = { ended: false }
  const state = { error: null }
  const loads = []
  const emit = (event) => { for (const callback of listeners.get(event) ?? []) callback() }
  return {
    element, loads, snapshot: () => state,
    on(event, handler) {
      if (!listeners.has(event)) listeners.set(event, new Set())
      listeners.get(event).add(handler)
      return () => listeners.get(event).delete(handler)
    },
    load(source) { loads.push(source); element.ended = false },
    async play() { emit('play') },
    pause() { emit('pause') },
    finish() { element.ended = true; emit('pause'); emit('ended') },
    error() { state.error = '播放出错'; emit('error') },
  }
}

test('播放器只发事件，适配层通知列表，列表决定结束后重播、切歌或停止', async () => {
  const queue = createPlaylist(async (item) => ({ url: item.source.url }))
  const player = createPlayer()
  const disconnect = connectPlaylistPlayback(queue, player)
  queue.add({ id: 'a', title: 'A', source: { kind: 'audio', url: 'a.mp3' } })
  queue.add({ id: 'b', title: 'B', source: { kind: 'audio', url: 'b.mp3' } })
  await queue.play('a')
  player.finish() // 真实媒体可能先 pause，再 ended
  await flush()
  assert.equal(queue.snapshot().currentId, 'b')
  assert.equal(queue.snapshot().status, 'playing')
  player.finish()
  await flush()
  assert.equal(queue.snapshot().status, 'ended')
  queue.setMode('repeat-one')
  await queue.play('b')
  player.finish()
  await flush()
  assert.equal(queue.snapshot().currentId, 'b')
  assert.equal(queue.snapshot().status, 'playing')
  assert.deepEqual(player.loads.map((source) => source.url), ['a.mp3', 'b.mp3', 'b.mp3', 'b.mp3'])
  disconnect()
  player.finish()
  await flush()
  assert.equal(player.loads.length, 4)
})

test('播放器暂停与出错事件同步到列表，不触发模式切歌', async () => {
  const queue = createPlaylist(async () => ({ url: 'a.mp3' }))
  const player = createPlayer()
  const disconnect = connectPlaylistPlayback(queue, player)
  queue.add({ id: 'a', title: 'A', source: { kind: 'audio', url: 'a.mp3' } })
  queue.setMode('repeat-one')
  await queue.play()
  player.pause()
  assert.equal(queue.snapshot().status, 'paused')
  player.error()
  assert.equal(queue.snapshot().status, 'error')
  assert.equal(queue.snapshot().error, '播放出错')
  assert.equal(player.loads.length, 1)
  disconnect()
})
