import test from 'node:test'
import assert from 'node:assert/strict'
import {
  LEGACY_PLAYLIST_STORAGE_KEY,
  PLAYLIST_LIBRARY_STORAGE_KEY,
  createPlaylistLibraryPersistence,
  libraryFileText,
} from '../src/playlist/persistence.ts'
import { createLibrary, createRecord, parseLibraryText } from '../src/playlist/library.ts'

const track = (id) => ({
  id, title: `歌曲 ${id}`, artist: '作者', cover: 'https://example.com/cover.jpg',
  source: { kind: 'youtube', url: `https://www.youtube.com/watch?v=${id}` },
})

function memoryStorage() {
  const values = new Map()
  return {
    writes: 0,
    getItem(key) { return values.get(key) ?? null },
    setItem(key, value) { values.set(key, value); this.writes++ },
    has(key) { return values.has(key) },
  }
}

test('多列表存档：保存后读回名称、条目、模式、当前曲目、历史和当前列表', () => {
  const storage = memoryStorage()
  const persistence = createPlaylistLibraryPersistence(() => storage)
  const library = {
    activeId: 'p2',
    playlists: [
      createRecord('p1', '通勤', { items: [track('a'), track('b')], mode: 'shuffle' }),
      createRecord('p2', '学习', { items: [track('c')], currentId: 'c', mode: 'repeat-one', history: ['c'] }),
    ],
  }
  persistence.save(library)

  const restored = persistence.load()
  assert.deepEqual(restored.playlists.map((playlist) => playlist.name), ['通勤', '学习'])
  assert.equal(restored.activeId, 'p2')
  assert.deepEqual(restored.playlists[0].items.map((item) => item.id), ['a', 'b'])
  assert.equal(restored.playlists[0].mode, 'shuffle')
  assert.equal(restored.playlists[1].mode, 'repeat-one')
  assert.equal(restored.playlists[1].currentId, 'c')
  assert.deepEqual(restored.playlists[1].history, ['c'])
})

test('旧版单列表存档自动升级成一个「我的音乐」列表，并写成新键', () => {
  const storage = memoryStorage()
  storage.setItem(LEGACY_PLAYLIST_STORAGE_KEY, JSON.stringify({
    version: 1,
    items: [track('a')],
    currentId: 'a',
    mode: 'repeat-all',
    history: ['a'],
  }))
  const persistence = createPlaylistLibraryPersistence(() => storage)

  const library = persistence.load()
  assert.equal(library.playlists.length, 1)
  assert.equal(library.playlists[0].name, '我的音乐')
  assert.deepEqual(library.playlists[0].items.map((item) => item.id), ['a'])
  assert.equal(library.playlists[0].mode, 'repeat-all')
  assert.ok(storage.has(PLAYLIST_LIBRARY_STORAGE_KEY), '升级后应写入新键，避免每次都重新迁移')
  assert.ok(storage.has(LEGACY_PLAYLIST_STORAGE_KEY), '旧键保留，不销毁用户数据')
})

test('存档损坏时报告一次并使用空库，不阻止启动', () => {
  for (const text of ['{broken', 'null', '{"version":2}']) {
    const storage = memoryStorage()
    storage.setItem(PLAYLIST_LIBRARY_STORAGE_KEY, text)
    const reports = []
    const library = createPlaylistLibraryPersistence(() => storage, (message) => reports.push(message)).load()
    assert.equal(library.playlists.length, 1)
    assert.deepEqual(library.playlists[0].items, [])
    assert.equal(reports.length, 1)
    assert.equal(storage.getItem(PLAYLIST_LIBRARY_STORAGE_KEY), text, '损坏存档不覆盖')
  }
})

test('存储不可访问时不抛错；写失败上报错误，后续保存可恢复', () => {
  const inaccessible = createPlaylistLibraryPersistence(() => { throw new Error('SecurityError') })
  assert.doesNotThrow(() => inaccessible.load())

  const storage = memoryStorage()
  let full = true
  const access = () => ({
    getItem: (key) => storage.getItem(key),
    setItem(key, value) { if (full) throw new Error('QuotaExceededError'); storage.setItem(key, value) },
  })
  const reports = []
  const persistence = createPlaylistLibraryPersistence(access, (message) => reports.push(message))
  persistence.save(createLibrary('我的音乐'))
  assert.ok(reports.at(-1).includes('保存失败'))
  full = false
  persistence.save(createLibrary('我的音乐'))
  assert.equal(reports.at(-1), '')
  assert.equal(storage.writes, 1)
})

test('导出文件带版本号和时间戳，能被导入解析', () => {
  const library = {
    activeId: 'p1',
    playlists: [createRecord('p1', '通勤', { items: [track('a')] })],
  }
  const text = libraryFileText(library)
  const file = JSON.parse(text)
  assert.equal(file.version, 2)
  assert.equal(typeof file.exportedAt, 'string')
  const parsed = parseLibraryText(text)
  assert.equal(parsed.playlists[0].name, '通勤')
  assert.deepEqual(parsed.playlists[0].items.map((item) => item.id), ['a'])
})
