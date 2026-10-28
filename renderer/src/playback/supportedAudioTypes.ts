/** 把实际运行的浏览器解码能力传给宿主，避免按平台猜测编码支持。 */
export function supportedAudioTypes(audio: Pick<HTMLAudioElement, 'canPlayType'> = new Audio()): string[] {
  return [
    'audio/webm; codecs="opus"',
    'audio/webm; codecs="vorbis"',
    'audio/mp4; codecs="mp4a.40.2"',
    'audio/mpeg',
  ].filter((type) => Boolean(audio.canPlayType(type)))
}
