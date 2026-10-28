import { host } from '../bridge/host'
import { createMediaElementController } from './mediaElementController'

/** 组合宿主故障诊断，核心媒体控制器仍可独立使用和测试。 */
export function createHostMediaController(initialVolume = 0.8) {
  return createMediaElementController(initialVolume, {
    sourceError: (url) => host.getStreamFailure(url),
    recoverSource: (url) => host.recoverStream(url),
    log: (event, details) => { void host.recordPlaybackLog(event, details) },
  })
}
