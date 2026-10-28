import test from 'node:test'
import assert from 'node:assert/strict'
import { searchSongVideos } from '../src/search/songVideoSearch.ts'

const response = (source) => ({ ok: true, source, keyword: '', items: [], error: null })

test('中文字段保留、去空格，两平台收到相同关键词和数量', async () => {
  const calls = []
  const provider = (source) => async (keyword, limit) => {
    calls.push([source, keyword, limit])
    return response(source)
  }
  await searchSongVideos({ song: ' 晴天 ', artist: ' 周杰伦 ', album: ' 叶惠美 ' }, {
    bilibili: provider('bilibili'), youtube: provider('youtube'),
  }, 100)
  assert.deepEqual(calls, [
    ['bilibili', '晴天 周杰伦 叶惠美', 30], ['youtube', '晴天 周杰伦 叶惠美', 30],
  ])
})

test('歌名必填，缺失时不请求平台', async () => {
  let called = false
  const provider = async () => { called = true; return response('youtube') }
  await assert.rejects(searchSongVideos({ song: ' ', artist: '周杰伦' }, {
    bilibili: provider, youtube: provider,
  }), /歌曲名/)
  assert.equal(called, false)
})

test('较快的平台先通知，另一个失败时保留已返回的视频链接', async () => {
  let rejectYoutube
  const youtube = new Promise((_, reject) => { rejectYoutube = reject })
  const notifications = []
  const url = 'https://www.bilibili.com/video/BV1GJ411x7h7'
  const done = searchSongVideos({ song: 'Never Gonna Give You Up' }, {
    bilibili: async () => ({ ...response('bilibili'), items: [{ url }] }),
    youtube: () => youtube,
  }, 6, (result) => notifications.push(result))
  await new Promise((resolve) => setImmediate(resolve))
  assert.equal(notifications.length, 1)
  assert.equal(notifications[0].items[0].url, url)
  rejectYoutube(new Error('代理连接失败'))
  const results = await done
  assert.equal(results[0].items[0].url, url)
  assert.equal(results[1].ok, false)
  assert.equal(results[1].error, '代理连接失败')
})

test('非有限数量使用默认值，失败信封有可显示的错误', async () => {
  const provider = async (_, limit) => {
    assert.equal(limit, 6)
    return { ...response('youtube'), ok: false }
  }
  const results = await searchSongVideos({ song: '晴天' }, {
    bilibili: provider, youtube: provider,
  }, NaN)
  assert.ok(results.every((result) => result.error))
})
