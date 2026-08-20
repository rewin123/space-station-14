/**
 * Зеркало DTO из Content.Server/AiAgent/Bus/AgentStateSnapshot.cs.
 *
 * Один файл, один источник: всё, что клиент знает о форме данных, живёт здесь. Генератора нет
 * намеренно — DTO меняются раз в несколько недель, а тянуть кодогенерацию из C# в проект, где
 * до сих пор не было ни одного package.json, дороже, чем держать полсотни строк руками.
 */

// ---------------------------------------------------------------- снимок

/**
 * Процессный снимок: то, что общее на всех агентов.
 *
 * Сессии здесь нет намеренно. Память, навыки и заметки принадлежат процессу и весят десятки
 * килобайт; системный промпт и переписка принадлежат агенту и весят мегабайты. Слитый воедино
 * ответ заставлял бы качать четыре истории, чтобы посмотреть на одну.
 */
export interface AgentStateSnapshot {
  instance: string
  seq: number
  round: number
  /** Кто сейчас жив. Дешёвые строки: ни промпта, ни истории. */
  agents: AgentRosterEntry[]
  memory: AgentMemory
  skills: AgentSkill[]
  notes: AgentPlayerNote[]
  /** Потолок ОДНОЙ заметки в символах — общий на хранилище. */
  note_limit: number
}

/**
 * Снимок ОДНОГО агента.
 *
 * `agent: null` приходит со статусом 200, а не 404: агент мог уйти между кадром `session.started`
 * и запросом. Это штатная гонка, и обрабатывать её надо как «слайс опустел», а не как ошибку —
 * 404 у нас терминален и навсегда останавливает петлю опроса.
 */
export interface AgentSessionSnapshot {
  instance: string
  seq: number
  agent: AgentSession | null
}

/**
 * Строка ростера: столько, сколько нужно на вкладку с индикатором.
 *
 * `started_seq` отличает «тот же агент» от «тот же id, новая сессия после переклейма»: номер
 * раунда посреди раунда не меняется и для этого не годится.
 */
export interface AgentRosterEntry {
  id: string
  name: string
  brain: number
  round: number
  started_seq: number
  alive: boolean
  mode: string
  turns: number
  messages: number
  body_epoch: number
  last_prompt_tokens: number
  context_limit: number
  queue_depth: number
  pending_input: boolean
  last_error: string | null
}

export interface AgentSession {
  id: string
  brain: number
  round: number
  prefix_hash: string
  system_prompt: string
  tools_json: string
  /** Растёт при каждой перенумерации тела. Индекс сообщения имеет смысл только внутри эпохи. */
  body_epoch: number
  messages: AgentMessage[]
  stats: AgentStats
  last_turn: AgentTurn | null
}

/** Живые записи и замороженный текст зоны 0 — они расходятся по устройству. */
export interface AgentMemory {
  memory_live: string[]
  memory_frozen: string
  memory_limit: number
}

export interface AgentSkill {
  name: string
  when: string
  body: string
}

/**
 * Заметка об одном человеке.
 *
 * Замороженного двойника, в отличие от памяти, нет: заметки в системный промпт не вклеиваются
 * вовсе — агент узнаёт о них строкой NOTE и читает инструментом. Показывать здесь нечего, кроме
 * живого содержимого.
 *
 * Ключ — `slug`, а не `name`: два написания одного имени дают один файл.
 */
export interface AgentPlayerNote {
  slug: string
  name: string
  entries: string[]
}

export interface AgentMessage {
  index: number
  role: 'system' | 'user' | 'assistant' | 'tool'
  content: string | null
  tool_calls: AgentToolCall[] | null
  tool_call_id: string | null
}

/** `arguments` — сырая строка ровно как её выдала модель, специально не нормализованная. */
export interface AgentToolCall {
  id: string
  name: string
  arguments: string
}

export interface AgentStats {
  turns: number
  conv_turns: number
  untooled_replies: number
  /** Ходы, закрытые явным noop: молчание по решению, а не по поломке. */
  idle_turns: number
  consecutive_failures: number
  broken_promises: number
  compactions: number
  last_prompt_tokens: number
  chars_per_token: number
  body_chars: number
  context_limit: number
  cache_last_ratio: number
  cache_mean_ratio: number
  cache_alarms: number
  queue_depth: number
  mode: string
  last_error: string | null
  volatile_tail: string | null
}

export interface AgentTurn {
  index: number
  phase: string
  step: number
  tool_calls: number
  spoke: boolean
  nudged: boolean
  promised: string | null
  exit: string
  delivery: string
  cache_ratio: number
  radio_channel: string | null
  addressed: boolean
  forced: boolean
  /** Дословный вход хода вместе со строкой SELF. */
  perception: string
}

// ---------------------------------------------------------------- события

/** Имена с провода — выводятся на сервере из enum, так что список закрыт. */
export type AgentEventType =
  | 'session.started'
  | 'session.ended'
  | 'message.appended'
  | 'history.replaced'
  | 'prefix.replaced'
  | 'memory.updated'
  | 'skill.updated'
  | 'skills.reloaded'
  | 'note.updated'
  | 'notes.reloaded'
  | 'stats'

export interface AgentEventFrame {
  seq: number
  type: AgentEventType
  /** Пусто у процессных сторов (память, скиллы) — они переживают сессию. */
  session: string
  payload: unknown
}

export interface AgentEventsResponse {
  instance: string
  seq: number
  /** Курсор непригоден: другой процесс, из будущего, или кольцо уже перезаписало. */
  resync: boolean
  /**
   * Ростер едет вместе с лентой, но КАДРОМ НЕ ЯВЛЯЕТСЯ.
   *
   * К нему не относятся ни курсор, ни resync; снимается он в момент возврата долгого опроса и
   * может отставать от запроса на все двадцать пять секунд. Сеять агента по ростеру поэтому
   * нельзя — только по кадру `session.started` или по выбору оператора.
   */
  agents: AgentRosterEntry[]
  events: AgentEventFrame[]
}

// Формы payload по видам.

export interface SessionStartedPayload {
  brain: number
  round: number
  prefix_hash: string
}

export interface SessionEndedPayload {
  brain: number
  reason: string
}

export interface MessageAppendedPayload {
  body_epoch: number
  index: number
  message: AgentMessage
}

export interface HistoryReplacedPayload {
  body_epoch: number
  messages: AgentMessage[]
}

export interface PrefixReplacedPayload {
  prefix_hash: string
  system_prompt: string
  tools_json: string
}

export interface MemoryUpdatedPayload {
  entries: string[]
}

export type SkillUpdatedPayload = AgentSkill

export interface SkillsReloadedPayload {
  skills: AgentSkill[]
}

/** Пустой `entries` — надгробие: заметки больше нет, ключ надо удалить. */
export type PlayerNoteUpdatedPayload = AgentPlayerNote

export interface PlayerNotesReloadedPayload {
  notes: AgentPlayerNote[]
}

export type StatsPayload = AgentStats

// ---------------------------------------------------------------- здоровье

export interface AgentHealth {
  ok: boolean
  instance: string
  seq: number
  ring: number
  ring_used: number
  round: number
  /** Поля session и pending_input убраны: первое было константой, второе теперь у каждого своё. */
  agents: AgentRosterEntry[]
}

// ---------------------------------------------------------------- команды

export type AgentCommand =
  | { type: 'message.send'; agent: string; text: string }
  | {
      type: 'memory.change'
      action: 'add' | 'replace' | 'remove'
      match?: string
      content?: string
    }
  | { type: 'skill.change'; name: string; when?: string; body?: string }
  | { type: 'skill.change'; name: string; match: string; replacement: string }

export interface AgentCommandResult {
  ok: boolean
  message?: string
  usage?: string
  /** Куда правка легла: `next_turn` для сообщения, `disk` для памяти и скиллов. */
  applied?: string
  /** Когда её увидит МОДЕЛЬ — это не то же самое, что «применено». */
  visible_to_model?: string
  seq?: number
  /** Кому ушло сообщение. Есть только у message.send. */
  agent?: string
  /** `process` у памяти и скиллов: они общие на всех агентов. */
  scope?: string
  error?: string
}
