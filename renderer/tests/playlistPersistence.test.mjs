import test from 'node:test'
import assert from 'node:assert/strict'
import { createPlaylist } from '../src/playlist/playlist.ts'
import { createPlaylistPersistence, parsePlaylist, serializePlaylist, PLAYLIST_STORAGE_KEY } from '../src/playlist/persistence.ts'

const track = (id) => ({
  id, title: `歌曲 ${id}`, artist: '作者', cover: 'https://example.com/cover.jpg',
  source: { kind: 'youtube', url: `https://www.youtube.com/watch?v=${id}` },
})
const resolve = async () => ({ url: 'stream://media/temporary' })

function memoryStorage() {
  const values = new Map()
  return {
    writes: 0,
    getItem(key) { return values.get(key) ?? null },
    setItem(key, value) { values.set(key, value); this.writes++ },
  }
}

test('重建列表后恢复顺序、元信息、模式、当前歌曲和历史，恢复时不解析或播放', async () => {
  const storage = memoryStorage()
  const persistence = createPlaylistPersistence(() => storage)
  assert.equal(persistence.load(), undefined)
  const original = createPlaylist(resolve)
  const stop = persistence.connect(original)
  original.connect({ load() {}, pause() {}, async play() {} })
  original.add(track('a')); original.add(track('b'))
  original.setMode('shuffle')
  await original.play('a')
  await original.play('b')
  let resolves = 0, loads = 0, plays = 0
  const restored = createPlaylist(async () => { resolves++; return resolve() }, { initial: persistence.load() })
  restored.connect({ load() { loads++ }, pause() {}, async play() { plays++ } })
  assert.deepEqual(restored.snapshot().items, original.snapshot().items)
  assert.equal(restored.snapshot().mode, 'shuffle')
  assert.equal(restored.snapshot().currentId, 'b')
  assert.deepEqual(restored.snapshot().history, ['a', 'b'])
  assert.equal(restored.snapshot().source, null)
  assert.equal(restored.snapshot().status, 'paused')
  assert.deepEqual([resolves, loads, plays], [0, 0, 0])
  assert.equal(storage.getItem(PLAYLIST_STORAGE_KEY).includes('stream://'), false)
  await restored.resume()
  assert.deepEqual([resolves, loads, plays], [1, 1, 1])
  stop()
})

test('移除和清空立即落盘，清空后重启不会复活旧歌曲并保留模式', () => {
  const storage = memoryStorage()
  const persistence = createPlaylistPersistence(() => storage)
  const queue = createPlaylist(resolve)
  const stop = persistence.connect(queue)
  queue.add(track('a')); queue.add(track('b'))
  queue.remove('a')
  assert.deepEqual(persistence.load().items.map((item) => item.id), ['b'])
  queue.setMode('repeat-all')
  queue.clear()
  const restored = createPlaylist(resolve, { initial: persistence.load() })
  assert.deepEqual(restored.snapshot().items, [])
  assert.equal(restored.snapshot().currentId, null)
  assert.equal(restored.snapshot().mode, 'repeat-all')
  stop()
})

test('运行期 File、blob 和 stream 条目不持久化，音频直链可以恢复', () => {
  const items = [
    track('video'),
    { ...track('local'), source: { kind: 'audio', file: new File(['audio'], 'test.wav') } },
    { ...track('blob'), source: { kind: 'audio', url: 'blob:https://example.com/123' } },
    { ...track('stream'), source: { kind: 'audio', url: 'stream://media/1' } },
    { ...track('direct'), source: { kind: 'audio', url: 'https://example.com/song.mp3' } },
  ]
  const text = serializePlaylist({ items, currentId: 'local', mode: 'repeat-one', history: ['video', 'local'] })
  const saved = parsePlaylist(text)
  assert.deepEqual(saved.items.map((item) => item.id), ['video', 'direct'])
  assert.equal(saved.currentId, null)
  assert.deepEqual(saved.history, ['video'])
  assert.ok(!text.includes('blob:') && !text.includes('stream:') && !text.includes('test.wav'))
})

test('恢复时过滤损坏、重复和未知类型条目，修正模式、当前 ID 与历史', () => {
  const data = parsePlaylist(JSON.stringify({
    version: 1,
    items: [null, {}, track('a'), track('a'), { ...track('bad'), source: { kind: 'unknown', url: 'https://example.com' } }, track('b')],
    currentId: 'missing', mode: 'unknown', history: ['missing', 'a', 42],
  }))
  assert.deepEqual(data.items.map((item) => item.id), ['a', 'b'])
  assert.equal(data.currentId, null)
  assert.equal(data.mode, 'sequential')
  assert.deepEqual(data.history, ['a'])
  const current = parsePlaylist(JSON.stringify({ version: 1, items: [track('a'), track('b')], currentId: 'b', history: ['a'] }))
  assert.deepEqual(current.history, ['a', 'b'])
})

test('损坏 JSON、未知版本和不可访问的存储均降级，不阻止创建列表', () => {
  for (const text of ['{broken', '{"version":2,"items":[]}', 'null', '{"version":1}']) {
    const reports = []
    const storage = memoryStorage()
    storage.setItem(PLAYLIST_STORAGE_KEY, text)
    const persistence = createPlaylistPersistence(() => storage, (message) => reports.push(message))
    const queue = createPlaylist(resolve, { initial: persistence.load() })
    assert.deepEqual(queue.snapshot().items, [])
    assert.equal(reports.length, 1)
    assert.equal(storage.getItem(PLAYLIST_STORAGE_KEY), text)
  }
  const inaccessible = createPlaylistPersistence(() => { throw new Error('SecurityError') })
  assert.doesNotThrow(() => inaccessible.load())
})

test('写入失败保留内存队列并报告错误，后续修改可重新保存', () => {
  const storage = memoryStorage()
  let full = true
  const access = () => ({
    getItem: (key) => storage.getItem(key),
    setItem(key, value) { if (full) throw new Error('QuotaExceededError'); storage.setItem(key, value) },
  })
  const reports = []
  const persistence = createPlaylistPersistence(access, (message) => reports.push(message))
  const queue = createPlaylist(resolve)
  const stop = persistence.connect(queue)
  queue.add(track('a'))
  assert.equal(queue.snapshot().items.length, 1)
  assert.ok(reports.at(-1).includes('保存失败'))
  full = false
  queue.setMode('repeat-all')
  assert.equal(persistence.load().items[0].id, 'a')
  assert.equal(reports.at(-1), '')
  stop()
})

test('单纯暂停或状态变化不会重复写盘，取消订阅后停止保存', async () => {
  const storage = memoryStorage()
  const persistence = createPlaylistPersistence(() => storage)
  const queue = createPlaylist(resolve)
  queue.connect({ load() {}, pause() {}, async play() {} })
  const stop = persistence.connect(queue)
  assert.equal(storage.writes, 0)
  queue.add(track('a'))
  await queue.play()
  const writes = storage.writes
  queue.pause()
  await queue.resume()
  queue.syncPlaying(false)
  assert.equal(storage.writes, writes)
  stop()
  queue.add(track('b'))
  assert.equal(storage.writes, writes)
})
