import test from 'node:test'
import assert from 'node:assert/strict'
import { supportedAudioTypes } from '../src/playback/supportedAudioTypes.ts'

test('只把运行时可解码的精确 MIME/codec 上报给宿主', () => {
  const queried = []
  const types = supportedAudioTypes({
    canPlayType(type) {
      queried.push(type)
      return type === 'audio/webm; codecs="opus"' ? 'probably' : type === 'audio/mpeg' ? 'maybe' : ''
    },
  })
  assert.deepEqual(types, ['audio/webm; codecs="opus"', 'audio/mpeg'])
  assert.ok(queried.includes('audio/mp4; codecs="mp4a.40.2"'))
})
