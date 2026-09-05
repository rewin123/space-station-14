import type { AgentMessage, AgentToolCall } from '../api/types'

/**
 * State of matching a call with its result.
 *
 * Three states, not two, and "ambiguous" is drawn explicitly: that's exactly where the paired
 * view lies, and exactly why the debugger exists.
 */
export type PairState = 'paired' | 'pending' | 'ambiguous'

export interface PairedCall {
  call: AgentToolCall
  /** The message with the result, if it has already arrived. */
  result: AgentMessage | null
  state: PairState
}

export interface ConversationRow {
  key: string
  message: AgentMessage
  /** This assistant message's calls together with their results. */
  calls: PairedCall[]
  /** A result that no call claimed — a protocol oddity that must not be hidden. */
  orphanResult: boolean
}

/**
 * Assembles the stream by nesting results under their calls.
 *
 * <b>Matching is done by scanning backward, NOT by a global map keyed on id.</b> This isn't a
 * stylistic choice: `NextCallId` on the server is used only in tests, while the real
 * identifiers come from the model (`LlamaClient`, `idEl.GetString() ?? ""`). If the model's
 * server didn't send them, every call in the turn gets an empty string — and a global map would
 * link all the results to the first call, silently hiding the rest. On top of that, identifiers
 * repeat after restoring from a snapshot: the call counter isn't persisted, but the body comes
 * back with the old numbers.
 *
 * So the id is only a hint. From a result we walk backward to the nearest assistant message
 * with a free slot and take the slot by id, and if it's empty or already taken, by position,
 * marking it `ambiguous`.
 */
export function pairConversation(messages: AgentMessage[], epoch: number): ConversationRow[] {
  const rows: ConversationRow[] = []
  const byIndex = new Map<number, ConversationRow>()

  for (const message of messages) {
    const row: ConversationRow = {
      key: `${epoch}:${message.index}`,
      message,
      calls: (message.tool_calls ?? []).map((call) => ({
        call,
        result: null,
        // A call without a result is normal mid-turn, not an error. It gets its own state so
        // that "waiting" doesn't look like "broken".
        state: 'pending' as PairState,
      })),
      orphanResult: false,
    }

    rows.push(row)
    byIndex.set(message.index, row)
  }

  for (const message of messages) {
    if (message.role !== 'tool')
      continue

    const row = byIndex.get(message.index)!
    const slot = findSlot(rows, message)

    if (slot === null) {
      row.orphanResult = true
      continue
    }

    slot.pair.result = message
    slot.pair.state = slot.byId ? 'paired' : 'ambiguous'
  }

  // Results are shown nested, so they must be removed from the flat stream — except for
  // orphans, which would otherwise disappear entirely.
  return rows.filter((row) => row.message.role !== 'tool' || row.orphanResult)
}

interface Slot {
  pair: PairedCall
  /** Found by identifier, not positionally. */
  byId: boolean
}

/** The nearest preceding assistant message with a free slot. */
function findSlot(rows: ConversationRow[], result: AgentMessage): Slot | null {
  const at = rows.findIndex((r) => r.message.index === result.index)

  for (let i = at - 1; i >= 0; i--) {
    const row = rows[i]

    // Reached the previous user message — that's the turn boundary, nothing further to search.
    if (row.message.role === 'user' && row.calls.length === 0)
      return null

    if (row.calls.length === 0)
      continue

    const id = result.tool_call_id
    if (id) {
      const exact = row.calls.find((c) => c.call.id === id && c.result === null)
      if (exact)
        return { pair: exact, byId: true }
    }

    // The identifier is empty, repeats, or is already spent — fall back to order within the turn.
    const free = row.calls.find((c) => c.result === null)
    if (free)
      return { pair: free, byId: false }
  }

  return null
}
