import type {
  AgentCommand,
  AgentCommandResult,
  AgentEventsResponse,
  AgentHealth,
  AgentStateSnapshot,
} from './types'

/**
 * Ошибка, о которой известно, чем она была.
 *
 * Различать их обязательно: 401 повторять бессмысленно (токен не станет верным сам), 400 — тоже
 * (это баг клиента), а вот обрыв сети или 5xx — ровно то, ради чего backoff и нужен. Клиент без
 * этой развилки бесконечно долбит сервер неверным токеном и выглядит как «просто не работает».
 */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }

  /** Повторять нет смысла: ответ не изменится, пока не изменится запрос или токен. */
  get terminal(): boolean {
    return this.status === 401 || this.status === 400 || this.status === 404
  }
}

/**
 * Сколько ждём ответа, прежде чем счесть сервер мёртвым.
 *
 * СТРОГО больше серверного PollTimeout (25с), и это не запас на всякий случай.
 * `AgentDebugServer.HandleAsync` передаёт роутеру токен ОСТАНОВКИ СЕРВЕРА, а не запроса, и
 * `HttpListener` не сообщает об отключении клиента. Оборванный запрос остаётся припаркованным
 * внутри `ReadAsync` все оставшиеся секунды и продолжает держать один из шестнадцати слотов.
 * Таймаут в две секунды — это тринадцать занятых слотов на вкладку, а при исчерпании блокируется
 * сам цикл приёма: /state и /command перестают отвечать для всех.
 */
const DEAD_SERVER_MS = 35_000

export interface Endpoint {
  baseUrl: string
  token: string
}

async function request<T>(
  endpoint: Endpoint,
  path: string,
  init: RequestInit,
  signal?: AbortSignal,
): Promise<T> {
  const timer = new AbortController()
  const timeout = setTimeout(() => timer.abort(), DEAD_SERVER_MS)

  // Внешний signal (размонтирование, смена поколения) и наш таймаут — оба обрывают запрос.
  const signals = signal ? AbortSignal.any([signal, timer.signal]) : timer.signal

  try {
    const response = await fetch(endpoint.baseUrl.replace(/\/$/, '') + path, {
      ...init,
      signal: signals,
      headers: {
        // Пустой токен — не шлём заголовок совсем. За обратным прокси его подставляет сам
        // прокси, и страница о нём не знает; посылать `Bearer ` пустой строкой было бы просто
        // мусором в запросе.
        ...(endpoint.token ? { Authorization: `Bearer ${endpoint.token}` } : {}),
        ...(init.body ? { 'Content-Type': 'application/json' } : {}),
        ...init.headers,
      },
      // 'same-origin' — и ровно оно, не 'omit' и не 'include'.
      //
      // 'include' сломал бы разработку: Allow-Credentials несовместим с `*` в Allow-Origin,
      // который ставит отладочный сервер, и каждый кросс-ориджинный запрос упал бы с невнятной
      // ошибкой про wildcard.
      //
      // 'omit' ломает прод, и это было здесь написано. За обратным прокси страница и API лежат
      // на одном origin, а доступ закрыт basic-auth; браузер приложил бы креды сам, но 'omit'
      // их срезает — и до игрового сервера запрос не доходит вовсе. Наружу это выглядит как
      // «неверный токен», хотя 401 отдал прокси.
      //
      // 'same-origin' даёт оба поведения разом: свой origin получает креды, чужой — нет.
      credentials: 'same-origin',
    })

    const text = await response.text()

    if (!response.ok) {
      let detail: string
      try {
        detail = (JSON.parse(text) as { error?: string }).error ?? text
      } catch {
        // Не JSON — значит отвечал не наш сервер, а что-то по дороге (обратный прокси, шлюз).
        // Вываливать его HTML-страницу в интерфейс бессмысленно: там сотня строк разметки и
        // ни одного слова о причине. Говорим, КТО отказал, — это и есть полезная часть.
        detail = `${response.statusText || 'ошибка'} — ответил не отладочный сервер, а посредник`
      }
      throw new ApiError(response.status, detail || response.statusText)
    }

    return JSON.parse(text) as T
  } finally {
    clearTimeout(timeout)
  }
}

export function getHealth(endpoint: Endpoint, signal?: AbortSignal): Promise<AgentHealth> {
  return request<AgentHealth>(endpoint, '/health', { method: 'GET' }, signal)
}

export function getState(endpoint: Endpoint, signal?: AbortSignal): Promise<AgentStateSnapshot> {
  return request<AgentStateSnapshot>(endpoint, '/state', { method: 'GET' }, signal)
}

/**
 * Долгий опрос: сервер держит ответ до 25 секунд, если ничего не произошло.
 *
 * Пустой список по истечении — нормальный ответ, а не деградация.
 */
export function getEvents(
  endpoint: Endpoint,
  instance: string,
  since: number,
  signal?: AbortSignal,
): Promise<AgentEventsResponse> {
  const query = new URLSearchParams({ since: String(since), instance })
  return request<AgentEventsResponse>(endpoint, `/events?${query}`, { method: 'GET' }, signal)
}

/**
 * Команда агенту.
 *
 * Вызывающий обязан быть single-flight и НИКОГДА не повторять автоматически: `AgentInbox.Enqueue`
 * склеивает два сообщения через перевод строки вместо того, чтобы отвергнуть второе, а ключа
 * идемпотентности на проводе нет. Повтор успевшего запроса даст одно сообщение с текстом дважды.
 */
export function postCommand(
  endpoint: Endpoint,
  command: AgentCommand,
  signal?: AbortSignal,
): Promise<AgentCommandResult> {
  return request<AgentCommandResult>(
    endpoint,
    '/command',
    { method: 'POST', body: JSON.stringify(command) },
    signal,
  )
}
