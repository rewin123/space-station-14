import { defineStore } from 'pinia'
import { computed, ref, shallowRef, triggerRef } from 'vue'
import { emptyAgent, type AgentViewState } from '../stream/apply'
import { connection, emptyDebugState, type ConnectionStatus, type DebugViewState } from '../stream/connection'
import type { AgentEventFrame } from '../api/types'
import { useSettings } from './settings'

/** Сколько кадров держим в сыром логе «Шины». Столько же, сколько кольцо на сервере. */
const FRAME_LOG = 2048

/** Где помним выбранного агента между перезагрузками страницы. */
const SELECTED_KEY = 'aidebug.agent'

/** Заглушка «показывать нечего»: одна на всё приложение, чтобы не плодить объекты в computed. */
const EMPTY = emptyAgent('')

export const useAgent = defineStore('agent', () => {
  // shallowRef: состояние — обычный объект, который правит машина вне Vue. Делать его глубоко
  // реактивным значит платить прокси за каждое сообщение разговора; вместо этого дёргаем ссылку
  // вручную, когда петля говорит, что что-то поменялось.
  const state = shallowRef<DebugViewState>(emptyDebugState())

  const status = ref<ConnectionStatus>('idle')
  const statusDetail = ref<string>('')
  const frames = ref<AgentEventFrame[]>([])
  const resyncs = ref<{ at: number; reason: string }[]>([])

  /** Кто сейчас жив, по данным сервера. Порядок задаёт сервер: ядро первым. */
  const roster = computed(() => state.value.globals.roster)

  /** Процессные хранилища: память, навыки, заметки. Общие на всех агентов. */
  const globals = computed(() => state.value.globals)

  const selected = computed(() => state.value.selected)

  /**
   * Состояние выбранного агента.
   *
   * Пустой агент вместо null — сознательно: вкладки читают из него десятки полей, и каждая
   * проверка на null в шаблоне была бы ещё одним местом, где можно ошибиться. Признак «показывать
   * нечего» — пустой `id`, ровно как раньше им был null в `sessionId`.
   */
  const current = computed<AgentViewState>(() => {
    const id = state.value.selected

    if (!id)
      return EMPTY

    const slice = state.value.agents.get(id)
    return slice?.gate.seeded ? slice.view : EMPTY
  })

  /** Снимок выбранного агента в полёте. Отличается от «агента нет». */
  const loading = computed(() => {
    const id = state.value.selected
    if (!id)
      return false

    return state.value.agents.get(id)?.gate.state === 'seeding'
  })

  const hasSession = computed(() => current.value.id !== '' && !current.value.ended)

  /** Состояние слайса для чипа в шапке: не загружен / снимок / на связи / ушёл. */
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

    // Новый объект состояния: старое принадлежало прошлому соединению, и переиспользовать его
    // значит смешать два процесса в одной ленте.
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
          // Выбор по умолчанию: первый в ростере, то есть ядро. Здесь, а не в петле, потому что
          // это решение интерфейса, а не протокола — петля не знает, на что смотрит человек.
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
