import test from 'node:test'
import assert from 'node:assert/strict'
import {
  DEFAULT_PLAYLIST_NAME,
  activeRecord,
  createLibrary,
  createRecord,
  mergeLibrary,
  normalizeLibrary,
  normalizeName,
  normalizeRecord,
  parseLibraryText,
  serializeLibrary,
  toPlaylistData,
  uniqueName,
} from '../src/playlist/library.ts'

const track = (id, kind = 'youtube') => ({
  id,
  title: `歌曲 ${id}`,
  artist: '作者',
  cover: 'https://example.com/cover.jpg',
  source: kind === 'audio'
    ? { kind: 'audio', url: `https://example.com/${id}.mp3` }
    : { kind, url: `https://www.bilibili.com/video/BV1GJ411x7h7` },
})

test('空库自带一个可用的默认列表', () => {
  const library = createLibrary()
  assert.equal(library.playlists.length, 1)
  assert.equal(library.playlists[0].name, DEFAULT_PLAYLIST_NAME)
  assert.equal(activeRecord(library).id, library.activeId)
  assert.deepEqual(library.playlists[0].items, [])
})

test('名称去空格、限长，重名自动加序号', () => {
  assert.equal(normalizeName('  我的  音乐 '), '我的 音乐')
  assert.equal(normalizeName('   ', '兜底'), '兜底')
  assert.equal(normalizeName('x'.repeat(80)).length, 40)
  assert.equal(uniqueName(['通勤'], '通勤'), '通勤 2')
  assert.equal(uniqueName(['通勤', '通勤 2'], '通勤'), '通勤 3')
  assert.equal(uniqueName([], '通勤'), '通勤')
})

test('单条列表：修正模式、当前曲目和历史，丢弃坏条目与重复条目', () => {
  const record = normalizeRecord({
    id: 'p1',
    name: '通勤',
    items: [null, {}, track('a'), track('a'), { ...track('bad'), source: { kind: 'unknown', url: 'https://example.com' } }, track('b')],
    currentId: 'missing',
    mode: 'unknown',
    history: ['missing', 'a', 42, 'b'],
  }, '兜底', 'fallback-id')
  assert.deepEqual(record.items.map((item) => item.id), ['a', 'b'])
  assert.equal(record.name, '通勤')
  assert.equal(record.currentId, null)
  assert.equal(record.mode, 'sequential')
  assert.deepEqual(record.history, ['a', 'b'])

  const withCurrent = normalizeRecord({ items: [track('a'), track('b')], currentId: 'b', history: ['a'] }, 'x', 'y')
  assert.equal(withCurrent.currentId, 'b')
  assert.deepEqual(withCurrent.history, ['a', 'b']) // 历史末尾必须对应当前项
})

test('列表库：去重 id、补默认 activeId、没有列表时补一个', () => {
  const library = normalizeLibrary({
    activeId: 'missing',
    playlists: [
      { id: 'p1', name: 'A', items: [track('a')] },
      { id: 'p1', name: 'A', items: [] },
    ],
  })
  assert.equal(library.playlists.length, 2)
  assert.notEqual(library.playlists[0].id, library.playlists[1].id)
  assert.notEqual(library.playlists[0].name, library.playlists[1].name)
  assert.equal(library.activeId, library.playlists[0].id)

  assert.equal(normalizeLibrary({ playlists: [] }).playlists.length, 1)
  assert.equal(normalizeLibrary({ playlists: 'nope' }), null)
})

test('存档序列化：运行期 File / blob / stream 地址不写入，示例字段保持稳定', () => {
  const library = {
    activeId: 'p1',
    playlists: [
      createRecord('p1', '我的音乐', {
        items: [
          track('video'),
          { ...track('local'), source: { kind: 'audio', file: new File(['a'], 'test.wav') } },
          { ...track('blob'), source: { kind: 'audio', url: 'blob:https://example.com/1' } },
          { ...track('stream'), source: { kind: 'audio', url: 'stream://media/1' } },
          { ...track('direct', 'audio') },
        ],
        currentId: 'local',
        history: ['video', 'local'],
      }),
    ],
  }
  const text = serializeLibrary(library)
  assert.ok(!text.includes('blob:') && !text.includes('stream:') && !text.includes('test.wav'))
  const parsed = parseLibraryText(text)
  assert.deepEqual(parsed.playlists[0].items.map((item) => item.id), ['video', 'direct'])
  assert.equal(parsed.playlists[0].currentId, null)
  assert.deepEqual(parsed.playlists[0].history, ['video'])
  // 相同数据序列化结果一致（store 用它判断「要不要写盘」）
  assert.equal(serializeLibrary(parsed), text)
})

test('导入解析兼容：整库 / 旧版单列表 / 裸列表对象', () => {
  const whole = parseLibraryText(JSON.stringify({
    version: 2,
    activeId: 'p1',
    playlists: [{ id: 'p1', name: '通勤', items: [track('a')] }],
  }))
  assert.equal(whole.playlists.length, 1)
  assert.equal(whole.playlists[0].name, '通勤')

  const legacy = parseLibraryText(JSON.stringify({
    version: 1,
    items: [track('a'), track('b')],
    currentId: 'b',
    mode: 'shuffle',
    history: ['a', 'b'],
  }))
  assert.equal(legacy.playlists.length, 1)
  assert.equal(legacy.playlists[0].name, '导入的列表')
  assert.equal(legacy.playlists[0].mode, 'shuffle')
  assert.equal(legacy.playlists[0].currentId, 'b')

  const bare = parseLibraryText(JSON.stringify({ name: '学习', items: [track('a')] }))
  assert.equal(bare.playlists[0].name, '学习')

  for (const text of ['{broken', 'null', '[]', '{"version":2,"playlists":[]}', '{"hello":1}']) {
    assert.throws(() => parseLibraryText(text), Error, text)
  }
})

test('合并导入：保留原列表，新列表重新生成 id，同名自动加序号', () => {
  const target = createLibrary('我的音乐')
  target.playlists[0].items = [track('a')]
  const imported = {
    activeId: 'x1',
    playlists: [
      createRecord('x1', '我的音乐', { items: [track('b')] }),
      createRecord('x2', '通勤', { items: [track('c')] }),
    ],
  }
  const merged = mergeLibrary(target, imported)
  assert.deepEqual(merged.names, ['我的音乐 2', '通勤'])
  assert.deepEqual(merged.library.playlists.map((playlist) => playlist.name), ['我的音乐', '我的音乐 2', '通勤'])
  assert.deepEqual(merged.library.playlists[1].items.map((item) => item.id), ['b'])
  assert.equal(merged.library.activeId, target.activeId)
  assert.equal(new Set(merged.library.playlists.map((playlist) => playlist.id)).size, 3)
})

test('toPlaylistData 只给播放核心需要的字段', () => {
  const record = createRecord('p1', '通勤', { items: [track('a')], mode: 'repeat-all', currentId: 'a', history: ['a'] })
  assert.deepEqual(toPlaylistData(record), {
    items: record.items,
    currentId: 'a',
    mode: 'repeat-all',
    history: ['a'],
  })
})
