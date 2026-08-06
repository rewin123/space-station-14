/**
 * Зеркало DTO из Content.Server/AiAgent/Bus/AgentStateSnapshot.cs.
 *
 * Один файл, один источник: всё, что клиент знает о форме данных, живёт здесь. Генератора нет
 * намеренно — DTO меняются раз в несколько недель, а тянуть кодогенерацию из C# в проект, где
 * до сих пор не было ни одного package.json, дороже, чем держать полсотни строк руками.
 */

// ---------------------------------------------------------------- снимок

export interface AgentStateSnapshot {
  instance: string
  seq: number
  /** null — ядро никем не занято. Это штатный ответ, а не ошибка. */
  session: AgentSession | null
  memory: AgentMemory
  skills: AgentSkill[]
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
  crew_live: string[]
  crew_frozen: string
  crew_limit: number
}

export interface AgentSkill {
  name: string
  when: string
  body: string
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
  compaction_armed: boolean
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
  target: 'memory' | 'crew'
  entries: string[]
}

export type SkillUpdatedPayload = AgentSkill

export interface SkillsReloadedPayload {
  skills: AgentSkill[]
}

export type StatsPayload = AgentStats

// ---------------------------------------------------------------- здоровье

export interface AgentHealth {
  ok: boolean
  instance: string
  seq: number
  ring: number
  ring_used: number
  session: string | null
  /** Сообщение оператора стоит в очереди и уедет следующим ходом. */
  pending_input: boolean
}

// ---------------------------------------------------------------- команды

export type AgentCommand =
  | { type: 'message.send'; text: string }
  | {
      type: 'memory.change'
      target: 'memory' | 'crew'
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
  error?: string
}
