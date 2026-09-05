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
 * The debugger's state — a plain object, without a single import from Vue.
 *
 * This constraint is exactly what makes the riskiest part testable: `apply` can be run against
 * a recorded stream of frames and its outcome checked against a snapshot, the same technique
 * the server uses to prove itself in `BusReplayTests`. It's exactly the same argument that
 * justifies `AgentDebugRouter`'s existence on the server.
 */
export interface GlobalViewState {
  memory: AgentMemory | null
  skills: AgentSkill[]
  notes: AgentPlayerNote[]
  /** Ceiling for one note. Arrives only with a snapshot, same as memory_limit. */
  noteLimit: number
  round: number
  roster: AgentRosterEntry[]
}

/** One brain. Everything here belongs to it alone. */
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

  /** The session has ended: we keep showing the conversation, but stop applying following frames. */
  ended: boolean
}

/** What `apply` asks the outside world to do: it never fetches anything itself. */
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

/** Seeds the process-level snapshot. */
export function seedGlobals(globals: GlobalViewState, snapshot: AgentStateSnapshot): void {
  globals.memory = snapshot.memory
  globals.skills = [...snapshot.skills]
  // With margin for version skew: the page and the server roll out in DIFFERENT steps, and
  // there's always a window between them where a fresh client talks to an old server. A
  // debugger that crashes on `[...undefined]` in that window takes away exactly the tool used
  // to figure out what happened.
  globals.notes = [...(snapshot.notes ?? [])]
  globals.noteLimit = snapshot.note_limit ?? 0
  globals.round = snapshot.round ?? 0
  globals.roster = [...(snapshot.agents ?? [])]
}

/**
 * Seeds one agent's snapshot.
 *
 * The chart series is reset when the agent has changed: the server doesn't keep history, the
 * series is accumulated client-side from the stream, and continuing the old one after a respawn
 * would mean drawing two different lives spliced together as one line.
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

/** Frame kinds that belong to process-level stores, not to an agent. */
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
 * Applies a process-level store frame.
 *
 * Such frames arrive with an empty `session` and apply to ALL agents at once: memory, skills,
 * and notes are shared per process. The selected agent has no effect on them whatsoever.
 */
export function applyGlobal(globals: GlobalViewState, frame: AgentEventFrame): ApplyOutcome {
  switch (frame.type) {
    case 'memory.updated': {
      const p = frame.payload as MemoryUpdatedPayload
      if (!globals.memory)
        return 'resync'

      globals.memory = { ...globals.memory, memory_live: [...p.entries] }

      // The frozen text changes ONLY on a prefix rebuild, and the server sends this same frame
      // when that happens. There's no way to tell the two apart from the payload, so we always
      // update the live column, while the frozen one is caught up by the reseed on
      // prefix.replaced.
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

      // An empty entries is a tombstone: deleting the last entry removes the file too. Not
      // removing the key here would mean drawing a person about whom nothing more is known,
      // all the way until the store reloads.
      if (note.entries.length === 0)
        globals.notes = globals.notes.filter((n) => n.slug !== note.slug)
      else if (at >= 0)
        globals.notes[at] = note
      else
        globals.notes = [...globals.notes, note].sort((a, b) => (a.slug < b.slug ? -1 : 1))

      return 'ok'
    }

    case 'notes.reloaded': {
      // Wholesale, for the same reason as with skills: a note could have been deleted from disk by hand.
      const p = frame.payload as PlayerNotesReloadedPayload
      globals.notes = [...p.notes].sort((a, b) => (a.slug < b.slug ? -1 : 1))
      return 'ok'
    }

    case 'skills.reloaded': {
      // Wholesale, not one at a time: reloading is the only way for a skill to DISAPPEAR, and
      // per-item updates say nothing about the ones that vanished.
      const p = frame.payload as SkillsReloadedPayload
      globals.skills = [...p.skills].sort((a, b) => (a.name < b.name ? -1 : 1))
      return 'ok'
    }
  }

  return 'ok'
}

/**
 * Applies one agent's frame.
 *
 * Returns `'resync'` when a frame can't be honestly applied and the only correct response is
 * to re-fetch THIS agent's snapshot. Guessing isn't allowed: a stream that has silently drifted
 * apart still looks plausible. Importantly, the reseed is now per-agent: it doesn't touch
 * neighbors or the shared cursor.
 */
export function applyAgent(view: AgentViewState, frame: AgentEventFrame): ApplyOutcome {
  switch (frame.type) {
    case 'session.started': {
      // A full reseed, not a local reset: the payload carries {brain, round, prefix_hash} and no
      // state at all. Plus the wire ordering — prefix.replaced is BEFORE session.started, and
      // history.replaced is AFTER — so the session can't be assembled from frames alone anyway.
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
      // We mark it, not clear it: it's useful to look at a dead agent's conversation, but
      // applying subsequent frames to it is not.
      view.ended = true
      return 'ok'
    }
  }

  // The zombie window. `Release` publishes session.ended, then cancels the token and leaves
  // without waiting for the loop; its `finally` still writes out synthetic turn-budget results
  // and one last stats. These frames arrive under the same agent identifier, so the only way to
  // tell them apart is that we've already seen the end.
  if (view.ended)
    return 'ok'

  switch (frame.type) {
    case 'message.appended': {
      const p = frame.payload as MessageAppendedPayload

      // The one thing that can't be verified any other way — and the one non-idempotent event.
      //
      // The server's snapshot is taken NON-atomically: seq is read first, so a change that
      // slips in mid-capture ends up both in the data and in the stream. For every other kind,
      // a repeat is harmless (each carries the whole new value), but a repeated append would
      // duplicate the message. A mismatched index or epoch catches this case, as well as loss,
      // and a second loop.
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

      // Applied IN PLACE, requires no snapshot, and this is a change from the previous behavior.
      //
      // The prefix rebuild used to mean reseeding the whole stream — tolerable with one agent.
      // With four, compactions happen four times as often, and the old rule would have meant a
      // debugger that constantly flickers. The payload carries everything needed: the hash, the
      // prompt, and the tool descriptions. The catch-up for the frozen memory text arrives as a
      // separate memory.updated frame, which the server sends on the same rebuild.
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
