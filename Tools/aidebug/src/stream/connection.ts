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
  /** The cursor is unusable, fetching a snapshot. A normal state, not an error. */
  | 'resyncing'
  | 'retrying'
  /** Terminal: the token is invalid or the client is sending garbage. Retrying won't help. */
  | 'unauthorized'
  | 'broken'

/** One agent on the client side: the seed gate plus its state. */
export interface AgentSlice {
  gate: SeedGate
  view: AgentViewState
}

/**
 * Everything the UI sees.
 *
 * The `seq` cursor belongs to the CONNECTION, not to the agent: there's one stream per process,
 * and there must not be a second cursor here — two streams with two cursors would give four
 * ways to drift apart instead of one.
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
  /** Every incoming frame, before it's applied — for the "Bus" tab's raw log. */
  onFrame(frame: AgentEventFrame): void
  onResync(reason: string): void
  onChanged(): void
}

/**
 * The single polling loop for the whole app.
 *
 * <b>It lives outside the component tree, and that's a hard requirement.</b> Vite's HMR
 * remounts components on every file save. A loop started in `onMounted` would spawn a SECOND
 * loop with the same cursor on every save: both would read seq=100, both would ask for events
 * after 100, both would apply them — and every message would render twice.
 *
 * Hence three things: a module-level singleton, a generation counter (the result of a request
 * issued by a past generation is discarded), and `import.meta.hot.dispose`, which stops the
 * loop before the module is replaced.
 *
 * The second consequence concerns the server: an aborted request does NOT free the server slot.
 * The router receives a server-shutdown token, not a request one, so an abandoned long-poll
 * sits out its full 25 seconds, holding one of the sixteen slots. That's also why seeding is
 * single-flight: a tab that seeded four agents at once would occupy five slots, and three such
 * tabs would take down the endpoint entirely.
 */
class Connection {
  private generation = 0
  private running = false
  private aborter: AbortController | null = null

  private endpoint: Endpoint | null = null
  private state: DebugViewState | null = null
  private hooks: ConnectionHooks | null = null

  /** Seed queue. An empty string means the process-level snapshot. */
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
   * Shows an agent: seeds it if it doesn't have a snapshot yet.
   *
   * Already-seeded agents keep accumulating frames in the background, so switching back to
   * them is instant and gap-free. Unseeded ones accumulate nothing — only their roster row is
   * visible.
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

  /** Unloads an agent from the tab's memory: one brain's history is tens of megabytes of strings. */
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

    // Strictly sequential: snapshot, then the loop. Not Promise.all.
    //
    // And after seeding, the cursor is taken FROM THE SNAPSHOT. Polling with since=0 is not
    // allowed: `Read` computes the oldest frame as `seq - count`, so early in the process's
    // life, since=0 would be honestly served with a replay of the whole ring instead of the
    // expected resync — and everything would get applied a second time.
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

          // We seed the selected agent right away — it's being looked at right now. We leave
          // the rest alone: four histories of a hundred thousand tokens each in one tab are
          // tens of megabytes of strings, and there's no point downloading them on behalf of
          // someone who isn't looking at them.
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

        // Retrying terminal responses is pointless: a 401 won't become valid on its own, and a
        // 400 is a client bug. Without this branch the page hammers the server forever and
        // looks like it "just doesn't work".
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
   * Routes a frame to its slice.
   *
   * A frame with an empty `session` is process-level and applies to ALL agents at once. A frame
   * for an unfamiliar agent is dropped: nobody is looking at it, and creating a slice for it
   * would mean accumulating history nobody asked for.
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

  /** Pulls one snapshot from the queue. Strictly one at a time: see the note on server slots above. */
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

      // The agent left while the snapshot was in flight. A normal race: the slice goes back to
      // its initial state, frames get dropped again, and the roster row will disappear on its
      // own with the next stream response.
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
   * Reseeds the CONNECTION: a different process, or the cursor is unusable.
   *
   * The agent selection is kept, all slices go back to their initial state. Only the selected
   * one gets reseeded right away — the rest lazily, on switching, to avoid a storm of four
   * snapshots.
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

// The loop must stop before the module is replaced, or the old one keeps living alongside the new one.
import.meta.hot?.dispose(() => connection.stop())
