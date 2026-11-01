import test, { afterEach, mock } from 'node:test'
import assert from 'node:assert/strict'
import { createPlaylist as createPlaylistCore } from '../src/playlist/playlist.ts'

// 每个用例创建的队列都要在结束时 dispose，避免自动重试的定时器把测试进程挂住。
const created = []
function createPlaylist(resolve, options) {
  const queue = createPlaylistCore(resolve, options)
  created.push(queue)
  return queue
}
afterEach(() => { for (const queue of created.splice(0)) queue.dispose() })

const track = (id) => ({ id, title: id, source: { kind: 'audio', url: `${id}.mp3` } })

test('补全已有歌曲的信息不复制条目或改变来源，并保留已有的非空信息', () => {
  const queue = createPlaylist(async () => ({ url: 'audio.mp3' }))
  queue.add(track('a'))
  queue.updateMetadata('a', { title: '真实视频标题', artist: 'UP 主', cover: 'https://i.example/cover.jpg' })
  queue.updateMetadata('a', { title: '', artist: '', cover: '' })
  const items = queue.snapshot().items
  assert.equal(items.length, 1)
  assert.equal(items[0].title, '真实视频标题')
  assert.equal(items[0].artist, 'UP 主')
  assert.equal(items[0].cover, 'https://i.example/cover.jpg')
  assert.deepEqual(items[0].source, track('a').source)
})
const deferred = () => {
  let resolve, reject
  const promise = new Promise((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const flush = () => new Promise((resolve) => setImmediate(resolve))

function setup(resolve = async (item) => ({ url: item.source.url }), options) {
  const queue = createPlaylist(resolve, options)
  const loads = []
  let plays = 0, pauses = 0
  const disconnect = queue.connect({
    load: (source) => loads.push(source),
    play: async () => { plays++ },
    pause: () => { pauses++ },
  })
  return { queue, loads, disconnect, plays: () => plays, pauses: () => pauses }
}

test('添加不触发播放，按稳定 ID 去重；删除前面的曲目不改变当前曲目', async () => {
  const { queue, loads } = setup()
  queue.add(track('a')); queue.add(track('a')); queue.add(track('b'))
  assert.equal(queue.snapshot().items.length, 2)
  assert.equal(loads.length, 0)
  await queue.play('b')
  queue.remove('a')
  assert.equal(queue.snapshot().currentId, 'b')
  assert.equal(queue.snapshot().status, 'playing')
})

test('播放、上一首下一首、结束自动续播，到队尾停止；同一首可以重播', async () => {
  const { queue, loads } = setup()
  queue.add(track('a')); queue.add(track('b'))
  await queue.play()
  await queue.next()
  await queue.previous()
  queue.ended()
  await flush()
  assert.equal(queue.snapshot().currentId, 'b')
  queue.ended()
  assert.equal(queue.snapshot().status, 'ended')
  queue.syncPlaying(true) // 播放器自身的播放按钮也可以重播队尾曲目
  assert.equal(queue.snapshot().status, 'playing')
  queue.ended()
  assert.equal(queue.snapshot().status, 'ended')
  await queue.play('b')
  assert.deepEqual(loads.map((source) => source.url), ['a.mp3', 'b.mp3', 'a.mp3', 'b.mp3', 'b.mp3'])
})

test('快速切歌时忽略旧解析结果和旧解析错误', async () => {
  for (const rejectOld of [false, true]) {
    const old = deferred()
    const { queue, loads } = setup((item) => item.id === 'a' ? old.promise : Promise.resolve({ url: 'b.mp3' }))
    queue.add(track('a')); queue.add(track('b'))
    const pending = queue.play('a')
    await queue.play('b')
    if (rejectOld) old.reject(new Error('旧解析失败'))
    else old.resolve({ url: 'a.mp3' })
    await pending
    assert.deepEqual(loads, [{ url: 'b.mp3' }])
    assert.equal(queue.snapshot().status, 'playing')
    assert.equal(queue.snapshot().currentId, 'b')
  }
})

test('解析中清空、暂停或断开连接使旧请求失效', async () => {
  for (const action of ['clear', 'pause', 'disconnect']) {
    const pending = deferred()
    const { queue, loads, disconnect } = setup(() => pending.promise)
    queue.add(track('a'))
    const playing = queue.play()
    if (action === 'disconnect') disconnect()
    else queue[action]()
    pending.resolve({ url: 'a.mp3' })
    await playing
    assert.ok(loads.every((source) => !source.url))
    assert.notEqual(queue.snapshot().status, 'playing')
  }
})

test('移除正在解析的曲目转到下一首，不被旧结果覆盖', async () => {
  const pending = deferred()
  const { queue, loads } = setup((item) => item.id === 'a' ? pending.promise : Promise.resolve({ url: 'b.mp3' }))
  queue.add(track('a')); queue.add(track('b'))
  const playing = queue.play('a')
  queue.remove('a')
  await flush()
  pending.resolve({ url: 'a.mp3' })
  await playing
  assert.equal(queue.snapshot().currentId, 'b')
  assert.equal(loads.at(-1).url, 'b.mp3')
})

test('解析失败保留队列并支持重试；播放器拒绝播放时显示错误', async () => {
  let attempts = 0
  const { queue } = setup(async () => {
    if (++attempts === 1) throw new Error('解析失败')
    return { url: 'a.mp3' }
  })
  queue.add(track('a'))
  await queue.play()
  assert.equal(queue.snapshot().error, '解析失败')
  assert.equal(queue.snapshot().items.length, 1)
  await queue.play()
  assert.equal(queue.snapshot().status, 'playing')
  queue.connect({ load() {}, pause() {}, play: async () => { throw new Error('播放失败') } })
  await queue.play()
  assert.equal(queue.snapshot().status, 'error')
  assert.equal(queue.snapshot().error, '播放失败')
})

test('暂停后恢复复用已加载音频；暂停中删除当前曲目不会自动开播', async () => {
  const { queue, loads, plays } = setup()
  queue.add(track('a')); queue.add(track('b'))
  await queue.play()
  queue.pause()
  await queue.resume()
  assert.equal(loads.length, 1)
  assert.equal(plays(), 2)
  queue.pause()
  queue.remove('a')
  assert.equal(queue.snapshot().status, 'idle')
  assert.equal(queue.snapshot().items.length, 1)
  assert.equal(plays(), 2)
})

test('本地文件直接交给注入的播放器；无播放器时返回可显示的错误', async () => {
  const file = new File(['audio'], 'test.wav')
  const { queue, loads } = setup(async (item) => ({ file: item.source.file }))
  queue.add({ ...track('local'), source: { kind: 'audio', file } })
  await queue.play()
  assert.equal(loads[0].file, file)
  const disconnected = createPlaylist(async () => ({ url: 'a.mp3' }))
  disconnected.add(track('a'))
  await disconnected.play()
  assert.equal(disconnected.snapshot().error, '播放器尚未连接')
})

test('列表循环在队尾返回第一首，手动上一首也可跨越边界', async () => {
  const { queue } = setup()
  queue.add(track('a')); queue.add(track('b'))
  queue.setMode('repeat-all')
  await queue.play('b')
  assert.equal(queue.canNext(), true)
  await queue.ended()
  assert.equal(queue.snapshot().currentId, 'a')
  assert.equal(queue.canPrevious(), true)
  await queue.previous()
  assert.equal(queue.snapshot().currentId, 'b')
})

test('单曲循环自然结束重播当前曲目，手动下一首仍切歌', async () => {
  const { queue, loads } = setup()
  queue.add(track('a')); queue.add(track('b'))
  queue.setMode('repeat-one')
  await queue.play('a')
  await queue.ended()
  assert.deepEqual(loads.map((source) => source.url), ['a.mp3', 'a.mp3'])
  await queue.next()
  assert.equal(queue.snapshot().currentId, 'b')
  await queue.ended()
  assert.equal(queue.snapshot().currentId, 'b')
})

test('随机播放排除当前曲目，上一首返回历史，移除的历史不再被选中', async () => {
  const { queue } = setup(undefined, { random: () => 0.99 })
  for (const id of ['a', 'b', 'c']) queue.add(track(id))
  queue.setMode('shuffle')
  await queue.play('a')
  assert.equal(queue.canPrevious(), false)
  await queue.ended()
  assert.equal(queue.snapshot().currentId, 'c')
  await queue.next()
  assert.equal(queue.snapshot().currentId, 'b')
  await queue.previous()
  assert.equal(queue.snapshot().currentId, 'c')
  queue.remove('a')
  assert.equal(queue.canPrevious(), false)
  await queue.ended()
  assert.equal(queue.snapshot().currentId, 'b')
})

test('空列表和单首列表在各种模式下都有明确的结束行为', async () => {
  for (const mode of ['sequential', 'repeat-all', 'repeat-one', 'shuffle']) {
    const { queue, plays } = setup()
    queue.setMode(mode)
    await queue.ended()
    await queue.next()
    assert.equal(plays(), 0)
    assert.equal(queue.canNext(), false)
    assert.equal(queue.canPrevious(), false)
    queue.add(track('a'))
    await queue.play()
    await queue.ended()
    assert.equal(queue.snapshot().currentId, 'a')
    assert.equal(queue.snapshot().status, mode === 'sequential' ? 'ended' : 'playing')
    assert.equal(plays(), mode === 'sequential' ? 1 : 2)
  }
})

test('修改模式不打断播放，结束事件按最新模式处理，暂停时忽略结束事件', async () => {
  const { queue, plays } = setup()
  queue.add(track('a')); queue.add(track('b'))
  await queue.play('b')
  queue.setMode('repeat-all')
  assert.equal(plays(), 1)
  await queue.ended()
  assert.equal(queue.snapshot().currentId, 'a')
  queue.pause()
  await queue.ended()
  assert.equal(plays(), 2)
  assert.equal(queue.snapshot().status, 'paused')
  queue.clear()
  assert.equal(queue.snapshot().mode, 'repeat-all')
  assert.deepEqual(queue.snapshot().history, [])
})

test('循环模式自动切歌失败时停在错误状态，不无限跳过或重试', async () => {
  const { queue } = setup(async (item) => {
    if (item.id === 'b') throw new Error('下一首解析失败')
    return { url: item.source.url }
  })
  queue.add(track('a')); queue.add(track('b'))
  queue.setMode('repeat-all')
  await queue.play('a')
  await queue.ended()
  assert.equal(queue.snapshot().currentId, 'b')
  assert.equal(queue.snapshot().status, 'error')
  assert.equal(queue.snapshot().error, '下一首解析失败')
  await queue.ended()
  assert.equal(queue.snapshot().status, 'error')
})

test('等待播放完成期间由播放器暂停，旧 play Promise 返回不能覆盖暂停状态', async () => {
  const queue = createPlaylist(async () => ({ url: 'a.mp3' }))
  const pending = deferred()
  queue.connect({ load() {}, pause() {}, play: () => pending.promise })
  queue.add(track('a'))
  const playing = queue.play()
  await flush()
  queue.syncPlaying(false)
  pending.resolve()
  await playing
  assert.equal(queue.snapshot().status, 'paused')
})

test('出错后重试同一首会回到失败时的进度，正常换歌仍从头播放', async () => {
  const queue = createPlaylist(async (item) => ({ url: item.source.url }))
  const seeks = []
  let time = 0
  queue.connect({
    load: () => { time = 0 },
    play: async () => {},
    pause: () => {},
    position: () => time,
    seek: (seconds) => { time = seconds; seeks.push(seconds) },
  })
  queue.add(track('a')); queue.add(track('b'))

  await queue.play('a')
  assert.deepEqual(seeks, [])

  time = 42.5
  queue.fail('网络断开')
  assert.equal(queue.snapshot().status, 'error')
  await queue.play()
  assert.deepEqual(seeks, [42.5])
  assert.equal(queue.snapshot().status, 'playing')

  await queue.play('b')
  assert.deepEqual(seeks, [42.5])
})

test('解析失败（还没加载过音源）时重试不跳转进度', async () => {
  let attempts = 0
  const queue = createPlaylist(async () => {
    if (++attempts === 1) throw new Error('解析失败')
    return { url: 'a.mp3' }
  })
  const seeks = []
  queue.connect({
    load: () => {},
    play: async () => {},
    pause: () => {},
    position: () => 88,
    seek: (seconds) => seeks.push(seconds),
  })
  queue.add(track('a'))
  await queue.play()
  assert.equal(queue.snapshot().status, 'error')
  assert.equal(queue.snapshot().source, null)
  await queue.play()
  assert.deepEqual(seeks, [])
  assert.equal(queue.snapshot().status, 'playing')
})

test('重试时解析也失败（例如 60s 超时），下一次重试仍回到原来的进度', async () => {
  let resolves = 0
  const queue = createPlaylist(async () => {
    if (++resolves === 2) throw new Error('解析超时')
    return { url: 'a.mp3' }
  })
  const seeks = []
  let time = 0
  queue.connect({
    load: () => { time = 0 },
    play: async () => {},
    pause: () => {},
    position: () => time,
    seek: (seconds) => { time = seconds; seeks.push(seconds) },
  })
  queue.add(track('a'))
  await queue.play()
  time = 87
  queue.fail('流断了')
  await queue.play()
  assert.equal(queue.snapshot().status, 'error')
  assert.deepEqual(seeks, [])
  await queue.play()
  assert.deepEqual(seeks, [87])
  assert.equal(queue.snapshot().status, 'playing')
})

test('断流后 5 秒自动重试并回到进度，成功后不再重试', async () => {
  mock.timers.enable({ apis: ['setInterval'] })
  try {
    const queue = createPlaylist(async () => ({ url: 'a.mp3' }))
    const seeks = []
    let time = 0
    queue.connect({
      load: () => { time = 0 },
      play: async () => {},
      pause: () => {},
      position: () => time,
      seek: (seconds) => { time = seconds; seeks.push(seconds) },
    })
    queue.add(track('a'))
    await queue.play()
    time = 64
    queue.fail('流断了')
    assert.equal(queue.snapshot().status, 'error')
    assert.equal(queue.snapshot().autoRetryIn, 5)
    mock.timers.tick(4000)
    assert.equal(queue.snapshot().autoRetryIn, 1)
    mock.timers.tick(1000)
    await flush()
    assert.deepEqual(seeks, [64])
    assert.equal(queue.snapshot().status, 'playing')
    assert.equal(queue.snapshot().autoRetryIn, 0)
    mock.timers.tick(60000)
    await flush()
    assert.deepEqual(seeks, [64])
  } finally {
    mock.timers.reset()
  }
})

test('自动重试按 5/10/20 秒退避，三次都失败后停下等手动重试', async () => {
  mock.timers.enable({ apis: ['setInterval'] })
  try {
    let resolves = 0
    const queue = createPlaylist(async () => {
      resolves += 1
      throw new Error('解析失败')
    })
    queue.connect({ load() {}, play: async () => {}, pause() {} })
    queue.add(track('a'))
    await queue.play()
    assert.equal(queue.snapshot().autoRetryIn, 5)
    mock.timers.tick(5000)
    await flush()
    assert.equal(queue.snapshot().autoRetryIn, 10)
    mock.timers.tick(10000)
    await flush()
    assert.equal(queue.snapshot().autoRetryIn, 20)
    mock.timers.tick(20000)
    await flush()
    assert.equal(queue.snapshot().autoRetryIn, 0)
    assert.equal(queue.snapshot().status, 'error')
    assert.equal(resolves, 4)
    await queue.play()
    assert.equal(queue.snapshot().autoRetryIn, 5)
  } finally {
    mock.timers.reset()
  }
})

test('暂停会取消排队中的自动重试', async () => {
  mock.timers.enable({ apis: ['setInterval'] })
  try {
    const queue = createPlaylist(async () => ({ url: 'a.mp3' }))
    queue.connect({ load() {}, play: async () => {}, pause() {} })
    queue.add(track('a'))
    await queue.play()
    queue.fail('流断了')
    assert.equal(queue.snapshot().autoRetryIn, 5)
    queue.pause()
    assert.equal(queue.snapshot().autoRetryIn, 0)
    mock.timers.tick(10000)
    await flush()
    assert.equal(queue.snapshot().status, 'paused')
  } finally {
    mock.timers.reset()
  }
})

test('切列表（replace）：停掉当前播放和自动重试，换上新的条目、当前曲目、模式和历史', async () => {
  const { queue, loads } = setup()
  queue.add(track('a')); queue.add(track('b'))
  await queue.play('a')
  queue.fail('流断了')
  assert.ok(queue.snapshot().autoRetryIn > 0)

  queue.replace({
    items: [{ id: 'x', title: 'X', source: { kind: 'audio', url: 'x.mp3' } }],
    currentId: 'x',
    mode: 'repeat-one',
    history: ['x'],
  })

  assert.deepEqual(queue.snapshot().items.map((item) => item.id), ['x'])
  assert.equal(queue.snapshot().currentId, 'x')
  assert.equal(queue.snapshot().mode, 'repeat-one')
  assert.deepEqual(queue.snapshot().history, ['x'])
  assert.equal(queue.snapshot().status, 'paused') // 记住当前曲目，但等用户点播放
  assert.equal(queue.snapshot().autoRetryIn, 0)
  assert.equal(queue.snapshot().source, null)
  // 换列表要清空播放器，避免旧音频继续响
  assert.deepEqual(loads.at(-1), {})

  mock.timers.enable({ apis: ['setInterval'] })
  try {
    mock.timers.tick(60000)
    await flush()
    assert.equal(queue.snapshot().status, 'paused') // 旧的自动重试已被取消
  } finally {
    mock.timers.reset()
  }

  // 空列表切换后回到 idle
  queue.replace({ items: [], currentId: null, mode: 'sequential', history: [] })
  assert.equal(queue.snapshot().status, 'idle')
})

