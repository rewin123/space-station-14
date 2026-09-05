import { defineStore } from 'pinia'
import { ref, watch } from 'vue'

const KEY = 'aidebug.endpoint'

/**
 * Endpoint and token.
 *
 * In localStorage, not in the URL: the token grants the full conversation of the agent, its
 * memory, and the right to speak in its voice, and the address bar is the worst place for
 * something like that (history, sharing, proxy logs).
 */
export const useSettings = defineStore('settings', () => {
  // On the Vite dev server we go straight to the debug server (cross-origin, as in prod);
  // in the built version the page lives behind a reverse proxy with the API on the same origin.
  const baseUrl = ref(location.port === '5173' ? 'http://127.0.0.1:9080' : '/api')
  const token = ref('')

  try {
    const saved = localStorage.getItem(KEY)
    if (saved) {
      const parsed = JSON.parse(saved) as { baseUrl?: string; token?: string }
      baseUrl.value = parsed.baseUrl ?? baseUrl.value
      token.value = parsed.token ?? ''
    }
  } catch {
    // Corrupted localStorage isn't a reason to fail to start.
  }

  watch([baseUrl, token], () => {
    try {
      localStorage.setItem(KEY, JSON.stringify({ baseUrl: baseUrl.value, token: token.value }))
    } catch {
      // Private mode — work without persisting.
    }
  })

  return { baseUrl, token }
})
