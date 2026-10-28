import test from 'node:test'
import assert from 'node:assert/strict'
import { createMediaElementController } from '../src/playback/mediaElementController.ts'

class FakeAudio extends EventTarget {
  src = ''
  volume = 1
  muted = false
  currentTime = 0
  duration = 0
  buffered = { length: 0 }
  resets = 0
  rejectPlay = null
  pauses = 0
  play() { return new Promise((_, reject) => { this.rejectPlay = reject }) }
  pause() { this.pauses++ }
  load() { this.resets++ }
  removeAttribute(name) { if (name === 'src') this.src = '' }
  remove() {}
}

class RecoveringAudio extends FakeAudio {
  plays = 0
  load() {
    super.load()
    queueMicrotask(() => {
      this.error = null; this.duration = 90
      this.dispatchEvent(new Event('loadedmetadata'))
      queueMicrotask(() => this.dispatchEvent(new Event('seeked')))
    })
  }
  async play() { this.plays++; this.dispatchEvent(new Event('play')); this.dispatchEvent(new Event('playing')) }
}
const deferred = () => {
  let resolve
  const promise = new Promise((yes) => { resolve = yes })
  return { promise, resolve }
}
async function until(condition) {
  for (let tries = 0; tries < 200; tries++) {
    if (condition()) return
    await new Promise((resolve) => setTimeout(resolve, 5))
  }
  assert.fail('等待状态超时')
}

test('网络中断时原地缓冲，恢复后自动定位继续播放并保持音量、静音状态', { timeout: 3000 }, async () => {
  const previous = globalThis.Audio; globalThis.Audio = RecoveringAudio
  const network = deferred(), started = deferred()
  const logs = []
  const controller = createMediaElementController(0.37, {
    log: (event, data) => logs.push({ event, data }),
    sourceError: async (url) => url.endsWith('/old') ? { message: '网络中断', recoverable: true } : '',
    recoverSource: async () => { started.resolve(); await network.promise; return { url: 'stream://media/restored' } },
    recoveryDelayMs: 5, errorCheckIntervalMs: 5,
  })
  try {
    let ended = 0; controller.on('ended', () => ended++)
    controller.load({ url: 'stream://media/old' }); await controller.play()
    controller.element.currentTime = 37.5; controller.element.dispatchEvent(new Event('timeupdate'))
    controller.setMuted(true)
    await started.promise
    assert.equal(controller.snapshot().loading, true)
    assert.equal(controller.snapshot().playing, true)
    assert.equal(controller.snapshot().error, null)
    controller.element.currentTime = 0; controller.element.dispatchEvent(new Event('timeupdate'))
    controller.element.dispatchEvent(new Event('ended'))
    assert.equal(controller.snapshot().currentTime, 37.5)
    network.resolve()
    await until(() => !controller.snapshot().loading)
    assert.equal(controller.element.src, 'stream://media/restored')
    assert.equal(controller.snapshot().currentTime, 37.5)
    assert.equal(controller.snapshot().playing, true)
    assert.equal(controller.snapshot().volume, 0.37)
    assert.equal(controller.snapshot().muted, true)
    assert.equal(ended, 0)
    assert.ok(logs.some((entry) => entry.event === 'recovery_start' && entry.data.position === 37.5))
    assert.ok(logs.some((entry) => entry.event === 'recovery_success'))
    assert.ok(logs.some((entry) => entry.event === 'source_failure' && entry.data.source === 'stream://media/old'))
  } finally { controller.dispose(); globalThis.Audio = previous }
})

test('诊断回调异常不会影响正常播放', async () => {
  const previous = globalThis.Audio; globalThis.Audio = RecoveringAudio
  const controller = createMediaElementController(0.8, { log() { throw new Error('日志不可用') } })
  try {
    controller.load({ url: 'a.mp3' })
    await controller.play()
    assert.equal(controller.snapshot().playing, true)
    assert.equal(controller.snapshot().error, null)
  } finally { controller.dispose(); globalThis.Audio = previous }
})

test('等待恢复时暂停、切歌或卸载，旧恢复结果均不能重新播放', { timeout: 3000 }, async () => {
  const previous = globalThis.Audio; globalThis.Audio = RecoveringAudio
  try {
    for (const action of ['pause', 'load', 'dispose']) {
      const network = deferred(), started = deferred()
      const controller = createMediaElementController(0.8, {
        sourceError: async (url) => url.endsWith('/old') ? '网络中断' : '',
        recoverSource: async () => { started.resolve(); await network.promise; return { url: 'stream://media/restored' } },
        recoveryDelayMs: 5, errorCheckIntervalMs: 5,
      })
      try {
        controller.load({ url: 'stream://media/old' }); await controller.play()
        await started.promise
        const plays = controller.element.plays
        if (action === 'load') controller.load({ url: 'stream://media/another' })
        else controller[action]()
        network.resolve()
        await new Promise((resolve) => setTimeout(resolve, 20))
        assert.equal(controller.element.plays, plays)
        assert.notEqual(controller.element.src, 'stream://media/restored')
      } finally { controller.dispose() }
    }
  } finally { globalThis.Audio = previous }
})

test('暂时无法刷新地址时继续等待，拖动进度会改变恢复位置', { timeout: 3000 }, async () => {
  const previous = globalThis.Audio; globalThis.Audio = RecoveringAudio
  let attempts = 0
  const controller = createMediaElementController(0.8, {
    sourceError: async (url) => url.endsWith('/old') ? '网络中断' : '',
    recoverSource: async () => ++attempts < 3 ? { recoverable: true } : { url: 'stream://media/restored' },
    recoveryDelayMs: 5, errorCheckIntervalMs: 5,
  })
  try {
    controller.load({ url: 'stream://media/old' }); await controller.play()
    await until(() => attempts > 0)
    controller.seek(22)
    await until(() => attempts >= 3 && !controller.snapshot().loading)
    assert.equal(controller.snapshot().currentTime, 22)
    assert.equal(controller.snapshot().error, null)
  } finally { controller.dispose(); globalThis.Audio = previous }
})

test('音源永久不可用时结束等待并显示错误', { timeout: 3000 }, async () => {
  const previous = globalThis.Audio; globalThis.Audio = RecoveringAudio
  const controller = createMediaElementController(0.8, {
    sourceError: async () => '网络中断',
    recoverSource: async () => ({ error: '视频已删除', recoverable: false }),
    recoveryDelayMs: 5, errorCheckIntervalMs: 5,
  })
  try {
    controller.load({ url: 'stream://media/old' }); await controller.play()
    await until(() => Boolean(controller.snapshot().error))
    assert.equal(controller.snapshot().error, '视频已删除')
    assert.equal(controller.snapshot().loading, false)
    assert.equal(controller.snapshot().playing, false)
  } finally { controller.dispose(); globalThis.Audio = previous }
})

test('临时 HTTP 拒绝导致浏览器报告不支持格式时，按宿主诊断恢复而非误报解码失败', { timeout: 3000 }, async () => {
  const previous = globalThis.Audio; globalThis.Audio = RecoveringAudio
  const controller = createMediaElementController(0.8, {
    sourceError: async (url) => ({ message: url.endsWith('/old') ? '临时访问失败' : '', recoverable: true }),
    recoverSource: async () => ({ url: 'stream://media/restored' }),
    recoveryDelayMs: 5, errorCheckIntervalMs: 1000,
  })
  try {
    controller.load({ url: 'stream://media/old' }); await controller.play()
    controller.element.error = { code: 4 }; controller.element.dispatchEvent(new Event('error'))
    await until(() => controller.element.src.endsWith('/restored') && !controller.snapshot().loading)
    assert.equal(controller.snapshot().error, null)
    assert.equal(controller.snapshot().playing, true)
  } finally { controller.dispose(); globalThis.Audio = previous }
})

test('换源、暂停和卸载时，旧 play Promise 的拒绝不污染新状态', async () => {
  const previous = globalThis.Audio
  globalThis.Audio = FakeAudio
  try {
    for (const action of ['load', 'pause', 'dispose']) {
      const controller = createMediaElementController()
      controller.load({ url: 'a.mp3' })
      const pending = controller.play()
      const reject = controller.element.rejectPlay
      if (action === 'load') controller.load({ url: 'b.mp3' })
      else controller[action]()
      reject(new Error('旧播放请求被中止'))
      await pending
      assert.equal(controller.snapshot().error, null)
      controller.dispose()
    }
  } finally { globalThis.Audio = previous }
})

test('加载或解码报错时暂停媒体并冻结进度，换源后恢复进度更新', () => {
  const previous = globalThis.Audio
  globalThis.Audio = FakeAudio
  try {
    const controller = createMediaElementController()
    controller.load({ url: 'stream://media/1' })
    const element = controller.element
    element.currentTime = 2
    element.dispatchEvent(new Event('timeupdate'))
    element.dispatchEvent(new Event('play'))
    const pauses = element.pauses
    element.error = { code: 3 }
    element.dispatchEvent(new Event('error'))
    assert.equal(element.pauses, pauses + 1)
    assert.equal(controller.snapshot().playing, false)
    assert.ok(controller.snapshot().error)
    element.currentTime = 8
    element.dispatchEvent(new Event('timeupdate'))
    assert.equal(controller.snapshot().currentTime, 2)
    controller.load({ url: 'stream://media/2' })
    element.currentTime = 1
    element.dispatchEvent(new Event('timeupdate'))
    assert.equal(controller.snapshot().error, null)
    assert.equal(controller.snapshot().currentTime, 1)
    controller.dispose()
  } finally { globalThis.Audio = previous }
})

test('清空音频源会重置媒体元素并清除进度和错误', () => {
  const previous = globalThis.Audio
  globalThis.Audio = FakeAudio
  try {
    const controller = createMediaElementController()
    controller.load({ url: 'a.mp3' })
    const element = controller.element
    controller.seek(4)
    controller.load({})
    assert.equal(element.src, '')
    assert.equal(element.resets, 2)
    assert.equal(controller.snapshot().currentTime, 0)
    assert.equal(controller.snapshot().loading, false)
    controller.dispose()
  } finally { globalThis.Audio = previous }
})

test('CEF 没发 error 时仍从宿主故障诊断停止播放，错误不会变成 ended', { timeout: 2000 }, async () => {
  const previous = globalThis.Audio
  globalThis.Audio = FakeAudio
  const controller = createMediaElementController(0.8, {
    sourceError: async () => '音频流提前结束，请重试', errorCheckIntervalMs: 5,
  })
  try {
    let ended = 0
    controller.on('ended', () => ended++)
    const failed = new Promise((resolve) => controller.on('error', resolve))
    controller.load({ url: 'stream://media/failed' })
    controller.element.dispatchEvent(new Event('play'))
    const pauses = controller.element.pauses
    await failed
    assert.equal(controller.snapshot().error, '音频流提前结束，请重试')
    assert.equal(controller.snapshot().playing, false)
    assert.equal(controller.element.pauses, pauses + 1)
    controller.element.dispatchEvent(new Event('ended'))
    assert.equal(ended, 0)
  } finally { controller.dispose(); globalThis.Audio = previous }
})

test('切歌后忽略旧曲目异步返回的后台错误', { timeout: 2000 }, async () => {
  const previous = globalThis.Audio
  globalThis.Audio = FakeAudio
  let rejectOld
  let started
  const oldStarted = new Promise((resolve) => { started = resolve })
  const controller = createMediaElementController(0.8, {
    sourceError: (url) => url.endsWith('/old') ? new Promise((resolve) => { rejectOld = resolve; started() }) : Promise.resolve(''),
    errorCheckIntervalMs: 5,
  })
  try {
    controller.load({ url: 'stream://media/old' })
    await oldStarted
    controller.load({ url: 'stream://media/new' })
    rejectOld('旧曲目解码失败')
    await new Promise((resolve) => setTimeout(resolve, 20))
    assert.equal(controller.snapshot().error, null)
  } finally { controller.dispose(); globalThis.Audio = previous }
})
