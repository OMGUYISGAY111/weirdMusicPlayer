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
  play() { return new Promise((_, reject) => { this.rejectPlay = reject }) }
  pause() {}
  load() { this.resets++ }
  removeAttribute(name) { if (name === 'src') this.src = '' }
  remove() {}
}

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
