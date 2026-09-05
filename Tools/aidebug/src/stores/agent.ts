import { defineStore } from 'pinia'
import { computed, ref, shallowRef, triggerRef } from 'vue'
import { emptyAgent, type AgentViewState } from '../stream/apply'
import { connection, emptyDebugState, type ConnectionStatus, type DebugViewState } from '../stream/connection'
import type { AgentEventFrame } from '../api/types'
import { useSettings } from './settings'

/** How many frames we keep in the "Bus" tab's raw log. The same as the server's ring. */
const FRAME_LOG = 2048

/** Where we remember the selected agent across page reloads. */
const SELECTED_KEY = 'aidebug.agent'

/** The "nothing to show" placeholder: one for the whole app, so computed doesn't spawn objects. */
const EMPTY = emptyAgent('')

export const useAgent = defineStore('agent', () => {
  // shallowRef: the state is a plain object driven by the machine outside of Vue. Making it
  // deeply reactive would mean paying a proxy cost for every conversation message; instead we
  // bump the ref manually when the loop says something changed.
  const state = shallowRef<DebugViewState>(emptyDebugState())

  const status = ref<ConnectionStatus>('idle')
  const statusDetail = ref<string>('')
  const frames = ref<AgentEventFrame[]>([])
  const resyncs = ref<{ at: number; reason: string }[]>([])

  /** Who's currently alive, per the server. The server sets the order: the core first. */
  const roster = computed(() => state.value.globals.roster)

  /** Process-level stores: memory, skills, notes. Shared across all agents. */
  const globals = computed(() => state.value.globals)

  const selected = computed(() => state.value.selected)

  /**
   * State of the selected agent.
   *
   * An empty agent instead of null — deliberately: the tabs read dozens of fields from it, and
   * every null check in the template would be one more place to get wrong. The "nothing to
   * show" signal is an empty `id`, exactly as `sessionId` being null used to be.
   */
  const current = computed<AgentViewState>(() => {
    const id = state.value.selected

    if (!id)
      return EMPTY

    const slice = state.value.agents.get(id)
    return slice?.gate.seeded ? slice.view : EMPTY
  })

  /** The selected agent's snapshot is in flight. Distinct from "no such agent". */
  const loading = computed(() => {
    const id = state.value.selected
    if (!id)
      return false

    return state.value.agents.get(id)?.gate.state === 'seeding'
  })

  const hasSession = computed(() => current.value.id !== '' && !current.value.ended)

  /** Slice state for the header chip: not loaded / snapshotting / live / gone. */
  function sliceState(id: string): 'absent' | 'seeding' | 'live' | 'ended' {
    const slice = state.value.agents.get(id)

    if (!slice)
      return 'absent'

    if (slice.gate.state === 'live')
      return slice.view.ended ? 'ended' : 'live'

    return slice.gate.state
  }

  function select(id: string): void {
    localStorage.setItem(SELECTED_KEY, id)
    connection.select(id)
    triggerRef(state)
  }

  function unload(id: string): void {
    connection.unload(id)
    triggerRef(state)
  }

  function connect(): void {
    const settings = useSettings()

    // A new state object: the old one belonged to the previous connection, and reusing it would
    // mean mixing two processes into one stream.
    state.value = emptyDebugState()
    state.value.selected = localStorage.getItem(SELECTED_KEY)
    frames.value = []
    resyncs.value = []

    connection.start(
      { baseUrl: settings.baseUrl, token: settings.token },
      state.value,
      {
        onStatus(next, detail) {
          status.value = next
          statusDetail.value = detail ?? ''
        },
        onFrame(frame) {
          frames.value.push(frame)
          if (frames.value.length > FRAME_LOG)
            frames.value.splice(0, frames.value.length - FRAME_LOG)
        },
        onResync(reason) {
          resyncs.value.push({ at: state.value.seq, reason })
        },
        onChanged() {
          // Default selection: the first in the roster, i.e. the core. Here, not in the loop,
          // because this is a UI decision, not a protocol one — the loop doesn't know what the
          // human is looking at.
          if (!state.value.selected && state.value.globals.roster.length > 0)
            select(state.value.globals.roster[0].id)

          triggerRef(state)
        },
      },
    )
  }

  function disconnect(): void {
    connection.stop()
    status.value = 'idle'
  }

  return {
    state,
    status,
    statusDetail,
    frames,
    resyncs,
    roster,
    globals,
    selected,
    current,
    loading,
    hasSession,
    sliceState,
    select,
    unload,
    connect,
    disconnect,
  }
})
