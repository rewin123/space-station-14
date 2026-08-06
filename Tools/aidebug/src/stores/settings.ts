import { defineStore } from 'pinia'
import { ref, watch } from 'vue'

const KEY = 'aidebug.endpoint'

/**
 * Адрес и токен.
 *
 * В localStorage, а не в URL: токен даёт полный разговор агента, его память и право говорить его
 * голосом, и адресная строка — худшее место для такого (история, шаринг, логи прокси).
 */
export const useSettings = defineStore('settings', () => {
  const baseUrl = ref('http://127.0.0.1:9080')
  const token = ref('')

  try {
    const saved = localStorage.getItem(KEY)
    if (saved) {
      const parsed = JSON.parse(saved) as { baseUrl?: string; token?: string }
      baseUrl.value = parsed.baseUrl ?? baseUrl.value
      token.value = parsed.token ?? ''
    }
  } catch {
    // Испорченный localStorage — не повод не запуститься.
  }

  watch([baseUrl, token], () => {
    try {
      localStorage.setItem(KEY, JSON.stringify({ baseUrl: baseUrl.value, token: token.value }))
    } catch {
      // Приватный режим — работаем без сохранения.
    }
  })

  return { baseUrl, token }
})
