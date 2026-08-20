import type {
  AgentEventFrame,
  AgentMemory,
  AgentMessage,
  AgentPlayerNote,
  AgentSkill,
  AgentRosterEntry,
  AgentSession,
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
export interface GlobalViewState {
  memory: AgentMemory | null
  skills: AgentSkill[]
  notes: AgentPlayerNote[]
  /** Потолок одной заметки. Приходит только со снимком, как и memory_limit. */
  noteLimit: number
  round: number
  roster: AgentRosterEntry[]
}

/** Один мозг. Всё, что здесь есть, принадлежит ему одному. */
export interface AgentViewState {
  id: string
  brain: number
  round: number
  startedSeq: number
  prefixHash: string
  systemPrompt: string
  toolsJson: string
  bodyEpoch: number
  messages: AgentMessage[]
  stats: AgentStats | null
  lastTurn: AgentTurn | null

  series: StatsSeries

  /** Сессия завершилась: разговор показываем, приходящие следом кадры не применяем. */
  ended: boolean
}

/** Что `apply` просит сделать снаружи: сам он ничего не качает. */
export type ApplyOutcome = 'ok' | 'resync'

export function emptyGlobals(): GlobalViewState {
  return {
    memory: null,
    skills: [],
    notes: [],
    noteLimit: 0,
    round: 0,
    roster: [],
  }
}

export function emptyAgent(id: string): AgentViewState {
  return {
    id,
    brain: 0,
    round: 0,
    startedSeq: 0,
    prefixHash: '',
    systemPrompt: '',
    toolsJson: '',
    bodyEpoch: 0,
    messages: [],
    stats: null,
    lastTurn: null,
    series: emptySeries(),
    ended: false,
  }
}

/** Посадить процессный снимок. */
export function seedGlobals(globals: GlobalViewState, snapshot: AgentStateSnapshot): void {
  globals.memory = snapshot.memory
  globals.skills = [...snapshot.skills]
  // С запасом на рассинхрон версий: страница и сервер выкатываются РАЗНЫМИ шагами, и между ними
  // всегда есть окно, когда свежий клиент говорит со старым сервером. Отладчик, падающий в этом
  // окне на `[...undefined]`, отнимает ровно тот инструмент, которым разбираются, что случилось.
  globals.notes = [...(snapshot.notes ?? [])]
  globals.noteLimit = snapshot.note_limit ?? 0
  globals.round = snapshot.round ?? 0
  globals.roster = [...(snapshot.agents ?? [])]
}

/**
 * Посадить снимок одного агента.
 *
 * Ряд графиков сбрасывается, когда агент сменился: сервер истории не хранит, ряд копится клиентом
 * из потока, и продолжать старый после переклейма значит рисовать склейку двух разных жизней
 * одной линией.
 */
export function seedAgent(view: AgentViewState, session: AgentSession): void {
  const changed = view.brain !== session.brain || view.bodyEpoch !== session.body_epoch

  view.id = session.id
  view.brain = session.brain
  view.round = session.round
  view.prefixHash = session.prefix_hash
  view.systemPrompt = session.system_prompt
  view.toolsJson = session.tools_json
  view.bodyEpoch = session.body_epoch
  view.messages = [...session.messages]
  view.stats = session.stats
  view.lastTurn = session.last_turn
  view.ended = false

  if (changed)
    resetSeries(view.series)

  if (session.stats)
    pushSample(view.series, session.stats)
}

/** Виды кадров, которые относятся к процессным хранилищам, а не к агенту. */
export function isGlobalFrame(type: string): boolean {
  return (
    type === 'memory.updated' ||
    type === 'skill.updated' ||
    type === 'skills.reloaded' ||
    type === 'note.updated' ||
    type === 'notes.reloaded'
  )
}

/**
 * Применить кадр процессного хранилища.
 *
 * Такие кадры приходят с пустым `session` и относятся ко ВСЕМ агентам сразу: память, навыки и
 * заметки одни на процесс. Выбранный агент на них не влияет никак.
 */
export function applyGlobal(globals: GlobalViewState, frame: AgentEventFrame): ApplyOutcome {
  switch (frame.type) {
    case 'memory.updated': {
      const p = frame.payload as MemoryUpdatedPayload
      if (!globals.memory)
        return 'resync'

      globals.memory = { ...globals.memory, memory_live: [...p.entries] }

      // Замороженный текст меняется ТОЛЬКО при перестройке префикса, и сервер шлёт этот же кадр,
      // когда она случается. Отличить одно от другого по payload нельзя, поэтому живую колонку
      // обновляем всегда, а замороженную догоняет пересев по prefix.replaced.
      return 'ok'
    }

    case 'skill.updated': {
      const skill = frame.payload as SkillUpdatedPayload
      const at = globals.skills.findIndex((s) => s.name === skill.name)
      if (at >= 0)
        globals.skills[at] = skill
      else
        globals.skills = [...globals.skills, skill].sort((a, b) => (a.name < b.name ? -1 : 1))
      return 'ok'
    }

    case 'note.updated': {
      const note = frame.payload as PlayerNoteUpdatedPayload
      const at = globals.notes.findIndex((n) => n.slug === note.slug)

      // Пустой entries — надгробие: удаление последней записи сносит и файл. Не удалить ключ
      // здесь значит рисовать человека, о котором уже ничего не известно, до самой перезагрузки
      // хранилища.
      if (note.entries.length === 0)
        globals.notes = globals.notes.filter((n) => n.slug !== note.slug)
      else if (at >= 0)
        globals.notes[at] = note
      else
        globals.notes = [...globals.notes, note].sort((a, b) => (a.slug < b.slug ? -1 : 1))

      return 'ok'
    }

    case 'notes.reloaded': {
      // Целиком, по тому же доводу, что и у скиллов: заметку могли удалить с диска руками.
      const p = frame.payload as PlayerNotesReloadedPayload
      globals.notes = [...p.notes].sort((a, b) => (a.slug < b.slug ? -1 : 1))
      return 'ok'
    }

    case 'skills.reloaded': {
      // Целиком, а не по одному: перечитывание — единственный способ для скилла ИСЧЕЗНУТЬ, и
      // поштучные обновления о пропавших молчат.
      const p = frame.payload as SkillsReloadedPayload
      globals.skills = [...p.skills].sort((a, b) => (a.name < b.name ? -1 : 1))
      return 'ok'
    }
  }

  return 'ok'
}

/**
 * Применить кадр одного агента.
 *
 * Возвращает `'resync'`, когда кадр невозможно применить честно и единственный правильный ответ —
 * перечитать снимок ЭТОГО агента. Гадать нельзя: молча разъехавшаяся лента выглядит правдоподобно.
 * Важно, что пересев теперь поагентный: соседей и общий курсор он не трогает.
 */
export function applyAgent(view: AgentViewState, frame: AgentEventFrame): ApplyOutcome {
  switch (frame.type) {
    case 'session.started': {
      // Полный пересев, а не локальный сброс: payload несёт {brain, round, prefix_hash} и никакого
      // состояния. Плюс порядок на проводе — prefix.replaced ДО session.started, а
      // history.replaced ПОСЛЕ, — так что собрать сессию из одних кадров всё равно нельзя.
      const p = frame.payload as SessionStartedPayload
      view.brain = p.brain
      view.round = p.round
      view.startedSeq = frame.seq
      view.ended = false
      resetSeries(view.series)
      return 'resync'
    }

    case 'session.ended': {
      const p = frame.payload as SessionEndedPayload
      void p
      // Помечаем, а не чистим: смотреть на разговор умершего агента полезно, а вот применять к
      // нему приходящие следом кадры — нет.
      view.ended = true
      return 'ok'
    }
  }

  // Окно зомби. `Release` публикует session.ended, затем отменяет токен и уходит, не дожидаясь
  // петли; её `finally` ещё допишет синтетические результаты турного бюджета и последний stats.
  // Эти кадры приходят под тем же идентификатором агента, так что отличить их можно только по
  // тому, что мы уже видели конец.
  if (view.ended)
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
      if (p.body_epoch !== view.bodyEpoch || p.index !== view.messages.length)
        return 'resync'

      view.messages.push(p.message)
      return 'ok'
    }

    case 'history.replaced': {
      const p = frame.payload as HistoryReplacedPayload
      view.bodyEpoch = p.body_epoch
      view.messages = [...p.messages]
      return 'ok'
    }

    case 'prefix.replaced': {
      const p = frame.payload as PrefixReplacedPayload
      view.prefixHash = p.prefix_hash
      view.systemPrompt = p.system_prompt
      view.toolsJson = p.tools_json

      // Применяется НА МЕСТЕ, снимка не требует, и это изменение против прежнего поведения.
      //
      // Раньше перестройка префикса означала пересев всей ленты — на одном агенте терпимо. На
      // четырёх компакции случаются вчетверо чаще, и прежнее правило означало бы отладчик,
      // который непрерывно моргает. Payload несёт всё, что нужно: хеш, промпт и описание
      // инструментов. Догоняющий замороженный текст памяти приезжает отдельным кадром
      // memory.updated, который сервер шлёт при той же перестройке.
      return 'ok'
    }

    case 'stats': {
      const p = frame.payload as StatsPayload
      view.stats = p
      pushSample(view.series, p)
      return 'ok'
    }
  }

  return 'ok'
}
