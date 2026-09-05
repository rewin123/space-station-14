/** Exponential backoff with a ceiling: the server might simply be off, and for a long while. */
export function backoffMs(attempt: number): number {
  const base = Math.min(30_000, 500 * 2 ** Math.min(attempt, 6))
  // Jitter, so several tabs don't hammer the server at the exact same moment.
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
