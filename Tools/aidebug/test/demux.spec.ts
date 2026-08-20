import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest'
import { connection, emptyDebugState, type DebugViewState } from '../src/stream/connection'
import * as client from '../src/api/client'
import { frame, roster, session, snapshot } from './fixtures'
import type { AgentEventFrame } from '../src/api/types'

/**
 * Демультиплексор: как одна лента раскладывается по нескольким мозгам.
 *
 * Тесты гоняют настоящую петлю с подменёнными запросами. Проверяется ровно то, чего не проверить
 * ни в `apply`, ни в `SeedGate`: что кадры попадают тому агенту, которому адресованы, что
 * процессные доезжают до всех, и что пересев одного не задевает соседей и общий курсор.
 */

const ENDPOINT = { baseUrl: 'http://x', token: 't' }

/** Кадры, которые отдаст следующий ответ ленты. Дальше петля висит на пустых ответах. */
let queued: AgentEventFrame[][] = []
let state: DebugViewState

function hooks() {
  return {
    onStatus: () => {},
    onFrame: () => {},
    onResync: () => {},
    onChanged: () => {},
  }
}

/**
 * Дождаться, пока петля переварит всё, что ей подложили.
 *
 * Настоящий длинный опрос держит ответ до 25 секунд, и петля большую часть времени спит в нём.
 * Подделка обязана хоть немного ждать: мгновенный ответ превращает `while` в горячий цикл, и
 * тест съедает всю память процесса вместо того, чтобы что-то проверить.
 */
async function settle(ms = 120): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms))
}

beforeEach(() => {
  queued = []
  state = emptyDebugState()

  vi.spyOn(client, 'getState').mockImplementation(async () =>
    snapshot({ agents: roster('core', 'combat-1') }),
  )

  vi.spyOn(client, 'getSession').mockImplementation(async (_e, agent) => ({
    instance: 'proc-1',
    seq: 10,
    agent: session(agent),
  }))

  vi.spyOn(client, 'getEvents').mockImplementation(async () => {
    await new Promise((resolve) => setTimeout(resolve, 2))

    const events = queued.shift() ?? []
    const last = events.length ? events[events.length - 1].seq : state.seq
    return { instance: 'proc-1', seq: last, resync: false, agents: roster('core', 'combat-1'), events }
  })
})

afterEach(() => {
  connection.stop()
  vi.restoreAllMocks()
})

describe('демультиплексор', () => {
  it('кадр незагруженного агента отбрасывается и не заводит слайс', async () => {
    state.selected = 'core'
    queued.push([
      frame(11, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'assistant', content: 'не мне', tool_calls: null, tool_call_id: null },
      }, 'combat-1'),
    ])

    connection.start(ENDPOINT, state, hooks())
    await settle()

    expect(state.agents.has('combat-1')).toBe(false)
  })

  it('кадры двух агентов не смешиваются', async () => {
    state.selected = 'core'
    connection.start(ENDPOINT, state, hooks())
    await settle()

    connection.select('combat-1')
    await settle()

    queued.push([
      frame(11, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'assistant', content: 'ядро', tool_calls: null, tool_call_id: null },
      }, 'core'),
      frame(12, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'assistant', content: 'робот', tool_calls: null, tool_call_id: null },
      }, 'combat-1'),
    ])

    await settle()

    expect(state.agents.get('core')!.view.messages.map((m) => m.content))
      .toEqual(['наблюдение', 'ядро'])
    expect(state.agents.get('combat-1')!.view.messages.map((m) => m.content))
      .toEqual(['наблюдение', 'робот'])
  })

  it('процессный кадр применяется независимо от того, кто выбран', async () => {
    state.selected = 'core'
    connection.start(ENDPOINT, state, hooks())
    await settle()

    queued.push([frame(11, 'memory.updated', { entries: ['общая'] }, '')])
    await settle()

    expect(state.globals.memory?.memory_live).toEqual(['общая'])
  })

  it('пересев одного агента не двигает общий курсор', async () => {
    state.selected = 'core'
    connection.start(ENDPOINT, state, hooks())
    await settle()

    connection.select('combat-1')
    await settle()

    // Разъехавшийся индекс у ядра: его слайс уйдёт за снимком, а лента обязана идти дальше.
    queued.push([
      frame(20, 'message.appended', {
        body_epoch: 0,
        index: 9,
        message: { index: 9, role: 'assistant', content: 'из будущего', tool_calls: null, tool_call_id: null },
      }, 'core'),
    ])

    await settle()

    expect(state.seq).toBe(20)
    expect(state.agents.get('combat-1')!.gate.seeded).toBe(true)
  })

  it('ростер из ленты доезжает до состояния', async () => {
    state.selected = 'core'
    connection.start(ENDPOINT, state, hooks())
    await settle()

    expect(state.globals.roster.map((r) => r.id)).toEqual(['core', 'combat-1'])
  })

  it('агент, ушедший пока летел снимок, возвращает слайс в исходное', async () => {
    // Штатная гонка, а не ошибка: сервер отвечает 200 с agent: null. Обрабатывать её как поломку
    // нельзя — 404 здесь остановил бы петлю навсегда, потому и выбран такой ответ.
    vi.mocked(client.getSession).mockResolvedValue({ instance: 'proc-1', seq: 10, agent: null })

    state.selected = 'core'
    connection.start(ENDPOINT, state, hooks())
    await settle()

    expect(state.agents.get('core')!.gate.state).toBe('absent')
  })
})
