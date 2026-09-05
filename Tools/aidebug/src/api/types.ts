/**
 * Mirror of the DTOs from Content.Server/AiAgent/Bus/AgentStateSnapshot.cs.
 *
 * One file, one source: everything the client knows about the shape of the data lives here.
 * There's deliberately no generator — the DTOs change once every few weeks, and pulling C#
 * codegen into a project that until now didn't have a single package.json costs more than
 * keeping fifty lines by hand.
 */

// ---------------------------------------------------------------- snapshot

/**
 * Process snapshot: what's shared across all agents.
 *
 * There's deliberately no session here. Memory, skills, and notes belong to the process and
 * weigh tens of kilobytes; the system prompt and the conversation belong to the agent and weigh
 * megabytes. A merged response would force downloading four histories to look at one.
 */
export interface AgentStateSnapshot {
  instance: string
  seq: number
  round: number
  /** Who's currently alive. Cheap rows: no prompt, no history. */
  agents: AgentRosterEntry[]
  memory: AgentMemory
  skills: AgentSkill[]
  notes: AgentPlayerNote[]
  /** Ceiling for a SINGLE note in characters — shared across the whole store. */
  note_limit: number
}

/**
 * Snapshot of ONE agent.
 *
 * `agent: null` comes back with status 200, not 404: the agent may have left between the
 * `session.started` frame and the request. This is a normal race, and it must be handled as
 * "the slice went empty", not as an error — a 404 is terminal for us and permanently stops the
 * polling loop.
 */
export interface AgentSessionSnapshot {
  instance: string
  seq: number
  agent: AgentSession | null
}

/**
 * A roster row: just enough for a tab with an indicator.
 *
 * `started_seq` distinguishes "the same agent" from "the same id, a new session after a
 * respawn": the round number doesn't change mid-round and isn't fit for this purpose.
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
  /** Increments on every body renumbering. A message index only makes sense within an epoch. */
  body_epoch: number
  messages: AgentMessage[]
  stats: AgentStats
  last_turn: AgentTurn | null
}

/** Live entries and the frozen text of zone 0 — they diverge by design. */
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
 * A note about one person.
 *
 * Unlike memory, there's no frozen counterpart: notes are never spliced into the system prompt
 * at all — the agent learns about them via a NOTE line and reads them with a tool. There's
 * nothing to show here besides the live content.
 *
 * The key is `slug`, not `name`: two spellings of the same name yield one file.
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

/** `arguments` is the raw string exactly as the model produced it, deliberately not normalized. */
export interface AgentToolCall {
  id: string
  name: string
  arguments: string
}

export interface AgentStats {
  turns: number
  conv_turns: number
  untooled_replies: number
  /** Turns closed by an explicit noop: silence by decision, not by breakage. */
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
  /** The turn's verbatim input together with the SELF line. */
  perception: string
}

// ---------------------------------------------------------------- events

/** Names off the wire — derived server-side from an enum, so the list is closed. */
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
  /** Empty for process-level stores (memory, skills) — they outlive the session. */
  session: string
  payload: unknown
}

export interface AgentEventsResponse {
  instance: string
  seq: number
  /** The cursor is unusable: a different process, from the future, or the ring already overwrote it. */
  resync: boolean
  /**
   * The roster rides along with the stream, but it is NOT A FRAME.
   *
   * Neither the cursor nor resync apply to it; it is captured at the moment the long poll
   * returns and can lag the request by the full twenty-five seconds. Seeding an agent from the
   * roster is therefore not allowed — only from a `session.started` frame or an operator's
   * choice.
   */
  agents: AgentRosterEntry[]
  events: AgentEventFrame[]
}

// Payload shapes by kind.

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

/** An empty `entries` is a tombstone: the note is gone, the key must be removed. */
export type PlayerNoteUpdatedPayload = AgentPlayerNote

export interface PlayerNotesReloadedPayload {
  notes: AgentPlayerNote[]
}

export type StatsPayload = AgentStats

// ---------------------------------------------------------------- health

export interface AgentHealth {
  ok: boolean
  instance: string
  seq: number
  ring: number
  ring_used: number
  round: number
  /** The session and pending_input fields are gone: the former was constant, the latter is now per-agent. */
  agents: AgentRosterEntry[]
}

// ---------------------------------------------------------------- commands

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
  /** Where the change landed: `next_turn` for a message, `disk` for memory and skills. */
  applied?: string
  /** When the MODEL will see it — that's not the same thing as "applied". */
  visible_to_model?: string
  seq?: number
  /** Who the message went to. Present only for message.send. */
  agent?: string
  /** `process` for memory and skills: they're shared across all agents. */
  scope?: string
  error?: string
}
