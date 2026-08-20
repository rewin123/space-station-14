import { ApiError, getEvents, getSession, getState, type Endpoint } from '../api/client'
import type { AgentEventFrame } from '../api/types'
import {
  applyAgent,
  applyGlobal,
  emptyAgent,
  emptyGlobals,
  isGlobalFrame,
  seedAgent,
  seedGlobals,
  type AgentViewState,
  type GlobalViewState,
} from './apply'
import { SeedGate } from './seedgate'
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

/** Один агент на стороне клиента: ворота досева плюс его состояние. */
export interface AgentSlice {
  gate: SeedGate
  view: AgentViewState
}

/**
 * Всё, что видит интерфейс.
 *
 * Курсор `seq` принадлежит СОЕДИНЕНИЮ, а не агенту: лента одна на процесс, и второго курсора
 * здесь быть не должно — две ленты с двумя курсорами дают четыре режима рассинхрона вместо одного.
 */
export interface DebugViewState {
  instance: string
  seq: number
  globals: GlobalViewState
  globalsGate: SeedGate
  agents: Map<string, AgentSlice>
  selected: string | null
}

export function emptyDebugState(): DebugViewState {
  return {
    instance: '',
    seq: 0,
    globals: emptyGlobals(),
    globalsGate: new SeedGate(),
    agents: new Map(),
    selected: null,
  }
}

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
 * и каждое сообщение отрисуется дважды.
 *
 * Отсюда три вещи: модульный синглтон, счётчик поколения (результат запроса, выданного прошлым
 * поколением, отбрасывается) и `import.meta.hot.dispose`, останавливающий петлю перед заменой
 * модуля.
 *
 * Второе следствие — про сервер: оборванный запрос НЕ освобождает серверный слот. Роутер получает
 * токен остановки сервера, а не запроса, поэтому брошенный long-poll досиживает свои 25 секунд,
 * держа один из шестнадцати слотов. Отсюда же single-flight на досевах: вкладка, севшая четырёх
 * агентов разом, заняла бы пять слотов, а три такие вкладки положили бы эндпоинт целиком.
 */
class Connection {
  private generation = 0
  private running = false
  private aborter: AbortController | null = null

  private endpoint: Endpoint | null = null
  private state: DebugViewState | null = null
  private hooks: ConnectionHooks | null = null

  /** Очередь досевов. Пустая строка — процессный снимок. */
  private queue: string[] = []
  private seeding = false

  start(endpoint: Endpoint, state: DebugViewState, hooks: ConnectionHooks): void {
    this.stop()

    this.endpoint = endpoint
    this.state = state
    this.hooks = hooks
    this.running = true
    this.aborter = new AbortController()
    this.queue = []
    this.seeding = false

    const generation = ++this.generation
    void this.run(generation)
  }

  stop(): void {
    this.running = false
    this.generation++
    this.aborter?.abort()
    this.aborter = null
  }

  /**
   * Показать агента: досеять, если его снимка ещё нет.
   *
   * Уже сеянные продолжают набирать кадры в фоне, поэтому возврат к ним мгновенен и без дыр.
   * Не сеянные не копят ничего — про них видно только строку ростера.
   */
  select(id: string): void {
    const state = this.state
    if (!state)
      return

    state.selected = id

    const slice = this.slice(state, id)

    if (slice.gate.state === 'absent')
      this.enqueue(id)

    this.hooks?.onChanged()
  }

  /** Выгрузить агента из памяти вкладки: история одного мозга — это десятки мегабайт строк. */
  unload(id: string): void {
    const state = this.state
    if (!state)
      return

    state.agents.delete(id)

    if (state.selected === id)
      state.selected = null

    this.hooks?.onChanged()
  }

  private slice(state: DebugViewState, id: string): AgentSlice {
    let slice = state.agents.get(id)

    if (!slice) {
      slice = { gate: new SeedGate(), view: emptyAgent(id) }
      state.agents.set(id, slice)
    }

    return slice
  }

  private enqueue(id: string): void {
    const state = this.state
    if (!state)
      return

    if (id === '')
      state.globalsGate.begin()
    else
      this.slice(state, id).gate.begin()

    if (!this.queue.includes(id))
      this.queue.push(id)
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

          state.globalsGate.begin()
          const snapshot = await getState(endpoint, this.aborter?.signal)
          if (!this.alive(generation))
            return

          state.instance = snapshot.instance
          state.seq = snapshot.seq
          seedGlobals(state.globals, snapshot)
          state.globalsGate.land(snapshot.seq)

          // Выбранного агента сеем сразу — на него смотрят прямо сейчас. Остальных не трогаем:
          // четыре истории по сотне тысяч токенов в одной вкладке — это десятки мегабайт строк,
          // и качать их за того, кто на них не смотрит, незачем.
          if (state.selected)
            this.enqueue(state.selected)

          hooks.onChanged()
        }

        await this.drainSeeds(generation)
        if (!this.alive(generation))
          return

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
          this.dropEverything(state)
          continue
        }

        state.globals.roster = [...(response.agents ?? [])]

        for (const frame of response.events) {
          hooks.onFrame(frame)
          this.route(state, frame, hooks)
        }

        state.seq = response.seq
        hooks.onChanged()
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

  /**
   * Разложить кадр по слайсам.
   *
   * Кадр с пустым `session` — процессный и относится ко ВСЕМ агентам сразу. Кадр незнакомого
   * агента отбрасывается: на него никто не смотрит, и заводить под него слайс значит копить
   * историю, которую никто не просил.
   */
  private route(state: DebugViewState, frame: AgentEventFrame, hooks: ConnectionHooks): void {
    if (frame.session === '' || isGlobalFrame(frame.type)) {
      const verdict = state.globalsGate.offer(frame)

      if (verdict === 'apply' && applyGlobal(state.globals, frame) === 'resync') {
        hooks.onResync('процессные хранилища требуют снимка')
        this.enqueue('')
      }

      return
    }

    const slice = state.agents.get(frame.session)

    if (!slice)
      return

    if (slice.gate.offer(frame) !== 'apply')
      return

    if (applyAgent(slice.view, frame) === 'resync') {
      hooks.onResync(`агент ${frame.session}: сменилась сессия или разъехались индексы`)
      this.enqueue(frame.session)
    }
  }

  /** Забрать один снимок из очереди. Строго по одному: см. про слоты сервера выше. */
  private async drainSeeds(generation: number): Promise<void> {
    const { endpoint, state, hooks } = this
    if (!endpoint || !state || !hooks || this.seeding)
      return

    const id = this.queue.shift()
    if (id === undefined)
      return

    this.seeding = true

    try {
      hooks.onStatus('resyncing')

      if (id === '') {
        const snapshot = await getState(endpoint, this.aborter?.signal)
        if (!this.alive(generation))
          return

        if (snapshot.instance !== state.instance) {
          this.dropEverything(state)
          return
        }

        seedGlobals(state.globals, snapshot)
        const { replay, overflowed } = state.globalsGate.land(snapshot.seq)

        for (const frame of replay)
          applyGlobal(state.globals, frame)

        if (overflowed)
          this.enqueue('')

        hooks.onChanged()
        return
      }

      const snapshot = await getSession(endpoint, id, this.aborter?.signal)
      if (!this.alive(generation))
        return

      if (snapshot.instance !== state.instance) {
        this.dropEverything(state)
        return
      }

      const slice = this.slice(state, id)

      // Агент ушёл, пока снимок летел. Штатная гонка: слайс возвращается в исходное, кадры снова
      // отбрасываются, а строка ростера сама исчезнет со следующим ответом ленты.
      if (snapshot.agent === null) {
        slice.gate.reset()
        hooks.onChanged()
        return
      }

      seedAgent(slice.view, snapshot.agent)
      const { replay, overflowed } = slice.gate.land(snapshot.seq)

      let again = overflowed

      for (const frame of replay) {
        if (applyAgent(slice.view, frame) === 'resync')
          again = true
      }

      if (again)
        this.enqueue(id)

      hooks.onChanged()
    } finally {
      this.seeding = false
    }
  }

  /**
   * Пересев СОЕДИНЕНИЯ: другой процесс или курсор непригоден.
   *
   * Выбор агента сохраняется, все слайсы возвращаются в исходное. Заново сеется только выбранный —
   * остальные лениво, при переключении, чтобы не устраивать шторм из четырёх снимков.
   */
  private dropEverything(state: DebugViewState): void {
    state.instance = ''
    state.seq = 0
    state.globalsGate.reset()

    for (const slice of state.agents.values())
      slice.gate.reset()

    this.queue = []
  }
}

export const connection = new Connection()

// Перед заменой модуля петля обязана остановиться, иначе старая продолжит жить рядом с новой.
import.meta.hot?.dispose(() => connection.stop())
