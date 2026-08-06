/** Экспоненциальная задержка с потолком: сервер может быть просто выключен, и это надолго. */
export function backoffMs(attempt: number): number {
  const base = Math.min(30_000, 500 * 2 ** Math.min(attempt, 6))
  // Дрожание, чтобы несколько вкладок не долбились в один и тот же момент.
  return base + Math.random() * 250
}

export function sleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    const timer = setTimeout(resolve, ms)
    signal?.addEventListener('abort', () => {
      clearTimeout(timer)
      resolve()
    }, { once: true })
  })
}
