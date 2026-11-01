import test from 'node:test'
import assert from 'node:assert/strict'
import {
  REMOTE_SOURCE_KEYS,
  isRemoteSourceKey,
  normalizeSourceKey,
} from '../src/search/sourceKeys.ts'
import {
  SOURCE_INFOS,
  findSourceInfo,
  sourceInfosFor,
  sourceLabels,
} from '../src/search/sources.ts'
import { SEARCH_MODES, modeSourceKeys } from '../src/search/modes.ts'

test('站点描述表覆盖全部站点键，顺序即面板顺序', () => {
  assert.deepEqual(SOURCE_INFOS.map((source) => source.key), [...REMOTE_SOURCE_KEYS])
})

test('每个站点描述都是完整的，且不认领其他站点的链接', () => {
  const foreign = new URL('https://example.com/watch?v=dQw4w9WgXcQ')
  for (const source of SOURCE_INFOS) {
    assert.ok(source.label.trim(), `${source.key} 缺少面板标题`)
    assert.ok(source.site.trim(), `${source.key} 缺少域名角标`)
    assert.ok(source.kindLabel.trim(), `${source.key} 缺少平台名`)
    assert.match(source.searchUrl('晴天 周杰伦'), /^https:\/\//, `${source.key} 的兜底搜索页不是 https`)
    assert.equal(source.matchId(foreign), null, `${source.key} 不应认领其他站点的链接`)
    assert.ok(source.placeholderTitle('ID123').includes('ID123'), `${source.key} 的占位标题丢了 ID`)
  }
})

test('未知站点键不会被当成已知站点（也就不会落到别的站点分支）', () => {
  assert.equal(isRemoteSourceKey('bilibili'), true)
  assert.equal(isRemoteSourceKey('soundcloud'), false)
  assert.equal(isRemoteSourceKey(null), false)
  assert.equal(normalizeSourceKey('youtube'), 'youtube')
  assert.equal(normalizeSourceKey('soundcloud'), null)
  assert.equal(normalizeSourceKey(undefined), null)
  assert.deepEqual(sourceInfosFor(['youtube', 'soundcloud', 'bilibili']).map((source) => source.key), [
    'youtube',
    'bilibili',
  ])
  assert.equal(findSourceInfo('soundcloud'), undefined)
})

test('站点名串用于错误提示', () => {
  assert.equal(sourceLabels(SOURCE_INFOS), 'B站 / YouTube')
  assert.equal(sourceLabels([findSourceInfo('youtube')]), 'YouTube')
})

test('模式声明自己涉及哪些站点：都必须是合法键，且顺序与面板一致', () => {
  assert.deepEqual(SEARCH_MODES.map((mode) => mode.key), ['song', 'link'])
  for (const mode of SEARCH_MODES) {
    assert.ok(mode.label.trim(), `${mode.key} 缺少模式标题`)
    assert.ok(mode.sources.length > 0, `${mode.key} 没有声明站点`)
    for (const key of mode.sources) assert.ok(REMOTE_SOURCE_KEYS.includes(key), `${mode.key} 声明了未知站点 ${key}`)
    assert.deepEqual(modeSourceKeys(mode.key), mode.sources)
  }
})

test('每个站点都有独立的展示差异入口（formatMeta 可覆盖）', () => {
  const item = {
    source: 'bilibili', id: 'x', title: 't', author: 'a', url: 'https://example.com',
    thumbnail: '', duration: 1, playCount: 12_300,
  }
  for (const source of SOURCE_INFOS) {
    const meta = source.formatMeta?.(item) ?? []
    assert.ok(meta.every((text) => typeof text === 'string' && text.trim()))
  }
  assert.deepEqual(findSourceInfo('bilibili').formatMeta(item), ['1.2 万播放'])
  assert.deepEqual(findSourceInfo('bilibili').formatMeta({ ...item, playCount: 0 }), [])
})
