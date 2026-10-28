export interface StreamRecoveryResult {
  url?: string
  error?: string
  recoverable?: boolean
}

/** 恢复任务不依赖 DOM、宿主和队列；取消后任何旧结果都不能继续播放。 */
export function createStreamRecovery(options: {
  renew: (url: string) => Promise<StreamRecoveryResult>
  suspend: () => number
  restore: (url: string, position: () => number, signal: AbortSignal) => Promise<void>
  resumed: () => void
  failed: (message: string) => void
  delayMs?: number
  log?: (event: string, details: Record<string, unknown>) => void
}) {
  let abort: AbortController | null = null
  let position = 0
  function cancel() {
    if (abort) options.log?.('recovery_cancel', { position })
    abort?.abort(); abort = null
  }
  function delay(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
      const cancel = () => { clearTimeout(timer); reject(new Error('cancelled')) }
      const timer = setTimeout(() => { signal.removeEventListener('abort', cancel); resolve() }, ms)
      signal.addEventListener('abort', cancel, { once: true })
    })
  }
  async function start(url: string): Promise<void> {
    if (abort) return
    const task = new AbortController()
    abort = task
    position = options.suspend()
    options.log?.('recovery_start', { source: url, position })
    let attempts = 0
    let currentUrl = url
    while (!task.signal.aborted) {
      try {
        const delayMs = Math.min((options.delayMs ?? 1500) * 2 ** Math.min(attempts++, 4), 15000)
        options.log?.('recovery_wait', { attempt: attempts, delayMs, position })
        await delay(delayMs, task.signal)
        const result = await options.renew(currentUrl)
        if (task.signal.aborted) return
        if (!result.url) {
          options.log?.('recovery_source_unavailable', { attempt: attempts, message: result.error, recoverable: result.recoverable })
          if (result.recoverable !== false) continue
          abort = null
          options.failed(result.error || '音源无法恢复')
          return
        }
        currentUrl = result.url
        await options.restore(result.url, () => position, task.signal)
        if (task.signal.aborted) return
        abort = null
        options.resumed()
        options.log?.('recovery_success', { attempt: attempts, position })
        return
      } catch (error) {
        if (!task.signal.aborted) options.log?.('recovery_attempt_error', { attempt: attempts, message: String(error), position })
        // 连接、加载或 seek 超时继续等待，用户暂停/切歌/退出则立即结束。
      }
    }
  }
  return { start, cancel, active: () => abort !== null, seek: (seconds: number) => { position = seconds } }
}
