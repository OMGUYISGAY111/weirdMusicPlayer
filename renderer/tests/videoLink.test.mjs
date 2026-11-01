import test from 'node:test'
import assert from 'node:assert/strict'
import { resolveVideoLink, videoItemFromLink } from '../src/search/videoLink.ts'
import { findSourceInfo } from '../src/search/sources.ts'

/** 测试用：拿站点描述 + 假的信息读取函数，组成可注入的站点能力 */
const linkSource = (key, info) => ({ ...findSourceInfo(key), info })

test('B站链接读取真实标题、封面、作者，保留原链接参数', async () => {
  const url = 'https://www.bilibili.com/video/BV1GJ411x7h7/?p=2'
  const result = await resolveVideoLink(url, [linkSource('bilibili', async (input) => {
    assert.equal(input, url)
    return { ok: true, error: null, item: {
      ...videoItemFromLink(input), title: '视频标题', author: 'UP 主',
      thumbnail: 'https://i.example/cover.jpg', duration: 123,
    } }
  })])
  assert.equal(result.title, '视频标题')
  assert.equal(result.author, 'UP 主')
  assert.equal(result.thumbnail, 'https://i.example/cover.jpg')
  assert.equal(result.url, url)
})

test('读取信息失败时返回错误，不能把占位标题当作成功结果', async () => {
  await assert.rejects(resolveVideoLink('https://www.bilibili.com/video/BV1GJ411x7h7', [
    linkSource('bilibili', async () => ({ ok: false, item: null, error: '视频不存在' })),
  ]), /视频不存在/)
})

test('YouTube链接读取标题、封面和频道，保留短链和时间参数，不调用B站接口', async () => {
  const url = 'https://youtu.be/dQw4w9WgXcQ?t=30'
  const result = await resolveVideoLink(url, [
    linkSource('bilibili', async () => { throw new Error('请求了错误的站点') }),
    linkSource('youtube', async (input) => {
      assert.equal(input, url)
      return { ok: true, error: null, item: {
        ...videoItemFromLink(input), title: '音乐视频', author: '频道', thumbnail: 'https://i.ytimg.com/cover.jpg', duration: 210,
      } }
    }),
  ])
  assert.equal(result.source, 'youtube')
  assert.equal(result.title, '音乐视频')
  assert.equal(result.author, '频道')
  assert.equal(result.thumbnail, 'https://i.ytimg.com/cover.jpg')
  assert.equal(result.url, url)
})

test('YouTube解析失败或视频不可用时不返回占位歌曲', async () => {
  await assert.rejects(resolveVideoLink('https://youtu.be/dQw4w9WgXcQ', [
    linkSource('youtube', async () => ({ ok: false, item: null, error: 'YouTube 视频不可用' })),
  ]), /视频不可用/)
})

test('B站 BV 链接保留参数，并与搜索结果使用相同视频 ID', () => {
  const item = videoItemFromLink(' https://www.bilibili.com/video/BV1GJ411x7h7/?p=2 ')
  assert.equal(item.source, 'bilibili')
  assert.equal(item.id, 'BV1GJ411x7h7')
  assert.equal(item.url, 'https://www.bilibili.com/video/BV1GJ411x7h7/?p=2')
})

test('YouTube watch、短链接、Shorts、直播和嵌入链接识别为同一视频', () => {
  for (const url of [
    'https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=20',
    'https://youtu.be/dQw4w9WgXcQ?si=example',
    'https://m.youtube.com/shorts/dQw4w9WgXcQ',
    'https://youtube.com/live/dQw4w9WgXcQ',
    'https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ',
  ]) {
    const item = videoItemFromLink(url)
    assert.equal(item.source, 'youtube')
    assert.equal(item.id, 'dQw4w9WgXcQ')
  }
})

test('不接受非视频页面、错误 ID、相似域名和不支持的短链', () => {
  for (const url of [
    'not a url', 'javascript:alert(1)', 'ftp://youtu.be/dQw4w9WgXcQ',
    'https://youtube.com/', 'https://youtube.com/watch?v=abc',
    'https://bilibili.com/video/BV123', 'https://b23.tv/example',
    'https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ',
  ]) assert.throws(() => videoItemFromLink(url), Error)
})

test('链接识别和错误提示都来自站点描述，不写死站点名', () => {
  // 只给 YouTube 时，B站链接应被拒绝，提示里也不出现 B站
  assert.throws(() => videoItemFromLink('https://www.bilibili.com/video/BV1GJ411x7h7', [
    findSourceInfo('youtube'),
  ]), (error) => error.message.includes('YouTube') && !error.message.includes('B站'))
  // 站点描述里的 linkHint 会出现在提示中
  assert.throws(() => videoItemFromLink('https://b23.tv/example', [findSourceInfo('bilibili')]),
    /B站短链/)
})
