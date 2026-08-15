import type {
  AgentEventFrame,
  AgentMemory,
  AgentMessage,
  AgentPlayerNote,
  AgentSkill,
  AgentStateSnapshot,
  AgentStats,
  AgentTurn,
  HistoryReplacedPayload,
  MemoryUpdatedPayload,
  MessageAppendedPayload,
  PlayerNoteUpdatedPayload,
  PlayerNotesReloadedPayload,
  PrefixReplacedPayload,
  SessionEndedPayload,
  SessionStartedPayload,
  SkillsReloadedPayload,
  SkillUpdatedPayload,
  StatsPayload,
} from '../api/types'
import { type StatsSeries, emptySeries, pushSample, resetSeries } from '../lib/series'

/**
 * Состояние отладчика — обычный объект, без единого импорта из Vue.
 *
 * Это ограничение и делает самую рискованную часть тестируемой: `apply` можно прогнать по
 * записанному потоку кадров и сверить итог со снимком, тем же приёмом, каким сервер доказывает
 * себя в `BusReplayTests`. Ровно тот же аргумент, которым `AgentDebugRouter` оправдывает своё
 * существование на сервере.
 */
export interface AgentViewState {
  instance: string
  seq: number

  sessionId: string | null
  brain: number
  round: number
  prefixHash: string
  systemPrompt: string
  toolsJson: string
  bodyEpoch: number
  messages: AgentMessage[]
  stats: AgentStats | null
  lastTurn: AgentTurn | null

  memory: AgentMemory | null
  skills: AgentSkill[]
  notes: AgentPlayerNote[]
  /** Потолок одной заметки. Приходит только со снимком, как и memory_limit. */
  noteLimit: number

  series: StatsSeries

  /** Сессия завершилась, новая ещё не началась — сессионные кадры игнорируются. */
  sessionGone: boolean
}

/** Что `apply` просит сделать снаружи: сам он ничего не качает. */
export type ApplyOutcome = 'ok' | 'resync'

export function emptyState(): AgentViewState {
  return {
    instance: '',
    seq: 0,
    sessionId: null,
    brain: 0,
    round: 0,
    prefixHash: '',
    systemPrompt: '',
    toolsJson: '',
    bodyEpoch: 0,
    messages: [],
    stats: null,
    lastTurn: null,
    memory: null,
    skills: [],
    notes: [],
    noteLimit: 0,
    series: emptySeries(),
    sessionGone: false,
  }
}

/** Посадить снимок. Ряд графиков сбрасывается: он копится только из потока. */
export function seed(state: AgentViewState, snapshot: AgentStateSnapshot): void {
  const instanceChanged = state.instance !== snapshot.instance

  state.instance = snapshot.instance
  state.seq = snapshot.seq
  state.memory = snapshot.memory
  state.skills = [...snapshot.skills]
  // С запасом на рассинхрон версий: страница и сервер выкатываются РАЗНЫМИ шагами, и между ними
  // всегда есть окно, когда свежий клиент говорит со старым сервером. Отладчик, падающий в этом
  // окне на `[...undefined]`, отнимает ровно тот инструмент, которым разбираются, что случилось.
  // Отсутствие поля — это «сервер о заметках ещё не знает», а не ошибка.
  state.notes = [...(snapshot.notes ?? [])]
  state.noteLimit = snapshot.note_limit ?? 0

  const session = snapshot.session

  if (session === null) {
    state.sessionId = null
    state.messages = []
    state.stats = null
    state.lastTurn = null
    state.bodyEpoch = 0
    state.sessionGone = false
    resetSeries(state.series)
    return
  }

  const sessionChanged = state.sessionId === null || state.brain !== session.brain
  state.sessionId = session.id
  state.brain = session.brain
  state.round = session.round
  state.prefixHash = session.prefix_hash
  state.systemPrompt = session.system_prompt
  state.toolsJson = session.tools_json
  state.bodyEpoch = session.body_epoch
  state.messages = [...session.messages]
  state.stats = session.stats
  state.lastTurn = session.last_turn
  state.sessionGone = false

  // Ряд копится клиентом, потому что сервер истории не хранит. Снимок даёт ровно одну точку, так
  // что после смены процесса или агента продолжать старый ряд — значит рисовать склейку двух
  // разных жизней одной линией.
  if (instanceChanged || sessionChanged)
    resetSeries(state.series)

  if (session.stats)
    pushSample(state.series, session.stats)
}

/**
 * Применить один кадр.
 *
 * Возвращает `'resync'`, когда кадр невозможно применить честно и единственный правильный ответ —
 * перечитать `/state`. Гадать нельзя: молча разъехавшаяся лента выглядит правдоподобно.
 */
export function apply(state: AgentViewState, frame: AgentEventFrame): ApplyOutcome {
  // Кадры процессных сторов (память, скиллы) приходят с пустым session и живут своей жизнью —
  // они переживают и сессию, и раунд, поэтому проверок на сессию для них нет.
  switch (frame.type) {
    case 'memory.updated': {
      const p = frame.payload as MemoryUpdatedPayload
      if (!state.memory)
        return 'resync'

      state.memory = { ...state.memory, memory_live: [...p.entries] }

      // Замороженный текст меняется ТОЛЬКО при перестройке префикса, и сервер шлёт этот же кадр,
      // когда она случается. Отличить одно от другого по payload нельзя, поэтому живую колонку
      // обновляем всегда, а замороженную догоняет reseed по prefix.replaced.
      return 'ok'
    }

    case 'skill.updated': {
      const skill = frame.payload as SkillUpdatedPayload
      const at = state.skills.findIndex((s) => s.name === skill.name)
      if (at >= 0)
        state.skills[at] = skill
      else
        state.skills = [...state.skills, skill].sort((a, b) => (a.name < b.name ? -1 : 1))
      return 'ok'
    }

    case 'note.updated': {
      const note = frame.payload as PlayerNoteUpdatedPayload
      const at = state.notes.findIndex((n) => n.slug === note.slug)

      // Пустой entries — надгробие: удаление последней записи сносит и файл. Не удалить ключ
      // здесь значит рисовать человека, о котором уже ничего не известно, до самой перезагрузки
      // хранилища.
      if (note.entries.length === 0)
        state.notes = state.notes.filter((n) => n.slug !== note.slug)
      else if (at >= 0)
        state.notes[at] = note
      else
        state.notes = [...state.notes, note].sort((a, b) => (a.slug < b.slug ? -1 : 1))

      return 'ok'
    }

    case 'notes.reloaded': {
      // Целиком, по тому же доводу, что и у скиллов: заметку могли удалить с диска руками.
      const p = frame.payload as PlayerNotesReloadedPayload
      state.notes = [...p.notes].sort((a, b) => (a.slug < b.slug ? -1 : 1))
      return 'ok'
    }

    case 'skills.reloaded': {
      // Целиком, а не по одному: перечитывание — единственный способ для скилла ИСЧЕЗНУТЬ, и
      // поштучные обновления о пропавших молчат.
      const p = frame.payload as SkillsReloadedPayload
      state.skills = [...p.skills].sort((a, b) => (a.name < b.name ? -1 : 1))
      return 'ok'
    }
  }

  // Дальше — только сессионное.
  switch (frame.type) {
    case 'session.started': {
      // Полный reseed, а не локальный сброс: payload несёт {brain, round, prefix_hash} и никакого
      // состояния. Плюс порядок на проводе — prefix.replaced ДО session.started, а
      // history.replaced ПОСЛЕ, — так что собрать сессию из одних кадров всё равно нельзя.
      const p = frame.payload as SessionStartedPayload
      state.brain = p.brain
      state.round = p.round
      state.sessionGone = false
      resetSeries(state.series)
      return 'resync'
    }

    case 'session.ended': {
      const p = frame.payload as SessionEndedPayload
      void p
      // Помечаем, а не чистим: смотреть на разговор умершего агента полезно, а вот применять к
      // нему приходящие следом кадры — нет.
      state.sessionGone = true
      return 'ok'
    }
  }

  // Окно зомби. `Release` публикует session.ended, затем отменяет токен и уходит, не дожидаясь
  // петли; её `finally` ещё допишет синтетические результаты турного бюджета и последний stats.
  // Эти кадры приходят под тем же session id ("current" — константа), так что отличить их можно
  // только по тому, что мы уже видели конец.
  if (state.sessionGone)
    return 'ok'

  switch (frame.type) {
    case 'message.appended': {
      const p = frame.payload as MessageAppendedPayload

      // Единственная непроверяемая иначе вещь — и единственное неидемпотентное событие.
      //
      // Снимок на сервере снимается НЕ атомарно: seq читается первым, поэтому изменение,
      // проехавшее посреди снятия, попадёт и в данные, и в поток. Для всех остальных видов повтор
      // безвреден (каждый несёт новое значение целиком), а вот повторный append задвоил бы
      // сообщение. Несовпадение индекса или эпохи ловит и это, и потерю, и вторую петлю.
      if (p.body_epoch !== state.bodyEpoch || p.index !== state.messages.length)
        return 'resync'

      state.messages.push(p.message)
      return 'ok'
    }

    case 'history.replaced': {
      const p = frame.payload as HistoryReplacedPayload
      state.bodyEpoch = p.body_epoch
      state.messages = [...p.messages]
      return 'ok'
    }

    case 'prefix.replaced': {
      const p = frame.payload as PrefixReplacedPayload
      state.prefixHash = p.prefix_hash
      state.systemPrompt = p.system_prompt
      state.toolsJson = p.tools_json

      // Перестройка префикса — единственный момент, когда догоняет замороженный текст памяти и
      // перечитывается библиотека скиллов. Оба события сервер шлёт сам, но снимок дешевле и
      // надёжнее, чем угадывать порядок.
      return 'resync'
    }

    case 'stats': {
      const p = frame.payload as StatsPayload
      state.stats = p
      pushSample(state.series, p)
      return 'ok'
    }
  }

  return 'ok'
}
