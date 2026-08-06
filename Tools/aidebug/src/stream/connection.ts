import { ApiError, getEvents, getState, type Endpoint } from '../api/client'
import type { AgentEventFrame } from '../api/types'
import { apply, seed, type AgentViewState } from './apply'
import { backoffMs, sleep } from './backoff'

export type ConnectionStatus =
  | 'idle'
  | 'connecting'
  | 'live'
  /** Курсор непригоден, идём за снимком. Штатное состояние, не ошибка. */
  | 'resyncing'
  | 'retrying'
  /** Терминально: токен неверен или клиент шлёт мусор. Повтор не поможет. */
  | 'unauthorized'
  | 'broken'

export interface ConnectionHooks {
  onStatus(status: ConnectionStatus, detail?: string): void
  /** Каждый пришедший кадр, до применения — для сырого лога «Шины». */
  onFrame(frame: AgentEventFrame): void
  onResync(reason: string): void
  onChanged(): void
}

/**
 * Единственная петля опроса на всё приложение.
 *
 * <b>Живёт вне дерева компонентов, и это обязательное условие.</b> HMR в Vite перемонтирует
 * компоненты на каждое сохранение файла. Петля, запущенная в `onMounted`, при сохранении даст ВТОРУЮ
 * петлю с тем же курсором: обе прочитают seq=100, обе попросят события после 100, обе применят —
 * и каждое сообщение отрисуется дважды. Проверка индекса в `apply` поймает это на втором дубле и
 * потребует resync, но вторую петлю это не остановит, так что получится шторм переспросов вместо
 * восстановления.
 *
 * Отсюда три вещи: модульный синглтон, счётчик поколения (результат запроса, выданного прошлым
 * поколением, отбрасывается) и `import.meta.hot.dispose`, останавливающий петлю перед заменой
 * модуля.
 *
 * Второе следствие — про сервер: оборванный запрос НЕ освобождает серверный слот. Роутер получает
 * токен остановки сервера, а не запроса, поэтому брошенный long-poll досиживает свои 25 секунд,
 * держа один из шестнадцати слотов. Шестнадцать сохранений за 25 секунд положат отладочный
 * эндпоинт целиком. Поэтому поколение меняется только при явной остановке, а не на каждый чих.
 */
class Connection {
  private generation = 0
  private running = false
  private aborter: AbortController | null = null

  private endpoint: Endpoint | null = null
  private state: AgentViewState | null = null
  private hooks: ConnectionHooks | null = null

  start(endpoint: Endpoint, state: AgentViewState, hooks: ConnectionHooks): void {
    this.stop()

    this.endpoint = endpoint
    this.state = state
    this.hooks = hooks
    this.running = true
    this.aborter = new AbortController()

    const generation = ++this.generation
    void this.run(generation)
  }

  stop(): void {
    this.running = false
    this.generation++
    this.aborter?.abort()
    this.aborter = null
  }

  private alive(generation: number): boolean {
    return this.running && generation === this.generation
  }

  private async run(generation: number): Promise<void> {
    const { endpoint, state, hooks } = this
    if (!endpoint || !state || !hooks)
      return

    let attempt = 0

    // Строго последовательно: снимок, потом петля. Не Promise.all.
    //
    // И курсор после сидирования берётся ИЗ СНИМКА. Опрашивать с since=0 нельзя: `Read` считает
    // самый старый кадр как `seq - count`, так что в начале жизни процесса since=0 будет честно
    // обслужен повтором всего кольца, а не ожидаемым resync — и всё применится по второму разу.
    while (this.alive(generation)) {
      try {
        if (state.instance === '' || state.seq === 0) {
          hooks.onStatus('connecting')
          const snapshot = await getState(endpoint, this.aborter?.signal)
          if (!this.alive(generation))
            return

          seed(state, snapshot)
          hooks.onChanged()
        }

        hooks.onStatus('live')
        attempt = 0

        const response = await getEvents(endpoint, state.instance, state.seq, this.aborter?.signal)
        if (!this.alive(generation))
          return

        if (response.resync) {
          hooks.onStatus('resyncing')
          hooks.onResync(
            `курсор ${state.seq} непригоден (кольцо ушло вперёд, курсор из будущего или другой процесс)`,
          )
          state.instance = ''
          state.seq = 0
          continue
        }

        let needsResync = false

        for (const frame of response.events) {
          hooks.onFrame(frame)
          if (apply(state, frame) === 'resync')
            needsResync = true
        }

        state.seq = response.seq
        hooks.onChanged()

        if (needsResync) {
          hooks.onStatus('resyncing')
          hooks.onResync('кадр потребовал снимка: сменилась сессия, префикс или разъехались индексы')
          state.instance = ''
          state.seq = 0
        }
      } catch (error) {
        if (!this.alive(generation))
          return

        // Терминальные ответы повторять бессмысленно: 401 не станет верным сам собой, а 400 —
        // это баг клиента. Без этой развилки страница вечно долбит сервер и выглядит как
        // «просто не работает».
        if (error instanceof ApiError && error.terminal) {
          hooks.onStatus(error.status === 401 ? 'unauthorized' : 'broken', error.message)
          this.running = false
          return
        }

        const message = error instanceof Error ? error.message : String(error)
        hooks.onStatus('retrying', message)
        await sleep(backoffMs(attempt++), this.aborter?.signal)
      }
    }
  }
}

export const connection = new Connection()

// Перед заменой модуля петля обязана остановиться, иначе старая продолжит жить рядом с новой.
import.meta.hot?.dispose(() => connection.stop())
