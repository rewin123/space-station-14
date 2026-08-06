import { defineStore } from 'pinia'
import { computed, ref, shallowRef, triggerRef } from 'vue'
import { emptyState, type AgentViewState } from '../stream/apply'
import { connection, type ConnectionStatus } from '../stream/connection'
import type { AgentEventFrame } from '../api/types'
import { useSettings } from './settings'

/** Сколько кадров держим в сыром логе «Шины». Столько же, сколько кольцо на сервере. */
const FRAME_LOG = 512

export const useAgent = defineStore('agent', () => {
  // shallowRef: состояние — обычный объект, который правит машина вне Vue. Делать его глубоко
  // реактивным значит платить прокси за каждое сообщение разговора; вместо этого дёргаем ссылку
  // вручную, когда петля говорит, что что-то поменялось.
  const state = shallowRef<AgentViewState>(emptyState())

  const status = ref<ConnectionStatus>('idle')
  const statusDetail = ref<string>('')
  const frames = ref<AgentEventFrame[]>([])
  const resyncs = ref<{ at: number; reason: string }[]>([])

  const session = computed(() => state.value.sessionId)
  const hasSession = computed(() => state.value.sessionId !== null && !state.value.sessionGone)

  function connect(): void {
    const settings = useSettings()

    // Новый объект состояния: старое принадлежало прошлому соединению, и переиспользовать его
    // значит смешать два процесса в одной ленте.
    state.value = emptyState()
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
          triggerRef(state)
        },
      },
    )
  }

  function disconnect(): void {
    connection.stop()
    status.value = 'idle'
  }

  return { state, status, statusDetail, frames, resyncs, session, hasSession, connect, disconnect }
})
