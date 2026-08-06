import { describe, expect, it } from 'vitest'
import { apply, emptyState, seed } from '../src/stream/apply'
import type { AgentEventFrame, AgentStateSnapshot, AgentStats } from '../src/api/types'

/**
 * Машина состояний клиента.
 *
 * Тот же приём, которым сервер доказывает свою шину в `BusReplayTests`: прогнать поток кадров и
 * сверить итог. Здесь важнее не счастливый путь, а моменты, когда применить кадр честно нельзя —
 * потому что молча разъехавшаяся лента выглядит совершенно правдоподобно.
 */

function stats(turn: number, extra: Partial<AgentStats> = {}): AgentStats {
  return {
    turns: turn,
    conv_turns: turn,
    untooled_replies: 0,
    consecutive_failures: 0,
    broken_promises: 0,
    compactions: 0,
    compaction_armed: true,
    last_prompt_tokens: 9000,
    chars_per_token: 3,
    body_chars: 400,
    context_limit: 0,
    cache_last_ratio: 0.98,
    cache_mean_ratio: 0.98,
    cache_alarms: 0,
    queue_depth: 0,
    mode: 'Core',
    last_error: null,
    volatile_tail: null,
    ...extra,
  }
}

function snapshot(overrides: Partial<AgentStateSnapshot> = {}): AgentStateSnapshot {
  return {
    instance: 'proc-1',
    seq: 10,
    session: {
      id: 'current',
      brain: 50611,
      round: 22,
      prefix_hash: 'HASH1',
      system_prompt: 'ПРОМПТ',
      tools_json: '[]',
      body_epoch: 0,
      messages: [{ index: 0, role: 'user', content: 'наблюдение', tool_calls: null, tool_call_id: null }],
      stats: stats(1),
      last_turn: null,
    },
    memory: {
      memory_live: ['запись'],
      memory_frozen: 'заморожено',
      memory_limit: 4000,
      crew_live: [],
      crew_frozen: '',
      crew_limit: 2000,
    },
    skills: [{ name: 'alpha', when: 'когда', body: 'тело' }],
    ...overrides,
  }
}

function frame(seq: number, type: AgentEventFrame['type'], payload: unknown, session = 'current'): AgentEventFrame {
  return { seq, type, session, payload }
}

describe('apply', () => {
  it('дописывает сообщение, когда индекс и эпоха сходятся', () => {
    const state = emptyState()
    seed(state, snapshot())

    const outcome = apply(
      state,
      frame(11, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'assistant', content: 'ответ', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(outcome).toBe('ok')
    expect(state.messages).toHaveLength(2)
    expect(state.messages[1].content).toBe('ответ')
  })

  it('требует снимка, когда индекс разъехался', () => {
    // Это ловит и потерянное сообщение, и задвоенное, и вторую петлю опроса. Единственное
    // неидемпотентное событие — и единственное, у которого есть эта проверка.
    const state = emptyState()
    seed(state, snapshot())

    const outcome = apply(
      state,
      frame(11, 'message.appended', {
        body_epoch: 0,
        index: 5,
        message: { index: 5, role: 'assistant', content: 'из будущего', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(outcome).toBe('resync')
    expect(state.messages).toHaveLength(1)
  })

  it('требует снимка, когда сменилась эпоха тела', () => {
    const state = emptyState()
    seed(state, snapshot())

    const outcome = apply(
      state,
      frame(11, 'message.appended', {
        body_epoch: 3,
        index: 1,
        message: { index: 1, role: 'user', content: 'после компакции', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(outcome).toBe('resync')
  })

  it('заменяет историю целиком и запоминает новую эпоху', () => {
    const state = emptyState()
    seed(state, snapshot())

    apply(
      state,
      frame(11, 'history.replaced', {
        body_epoch: 1,
        messages: [
          { index: 0, role: 'user', content: 'СВОДКА', tool_calls: null, tool_call_id: null },
          { index: 1, role: 'user', content: 'хвост', tool_calls: null, tool_call_id: null },
        ],
      }),
    )

    expect(state.bodyEpoch).toBe(1)
    expect(state.messages).toHaveLength(2)

    // И следующий append обязан считаться уже от новой эпохи.
    const outcome = apply(
      state,
      frame(12, 'message.appended', {
        body_epoch: 1,
        index: 2,
        message: { index: 2, role: 'assistant', content: 'дальше', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(outcome).toBe('ok')
    expect(state.messages).toHaveLength(3)
  })

  it('после конца сессии не применяет её кадры', () => {
    // Окно зомби: Release публикует session.ended, потом отменяет токен и уходит, а `finally`
    // петли ещё допишет синтетические результаты и последний stats. Session id — константа
    // "current", так что отличить их можно только по тому, что конец мы уже видели.
    const state = emptyState()
    seed(state, snapshot())

    apply(state, frame(11, 'session.ended', { brain: 50611, reason: 'убит' }))

    const outcome = apply(
      state,
      frame(12, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'tool', content: '{"ok":false}', tool_calls: null, tool_call_id: 'x' },
      }),
    )

    expect(outcome).toBe('ok')
    expect(state.messages).toHaveLength(1)
    expect(state.sessionGone).toBe(true)
  })

  it('начало сессии требует снимка, потому что в кадре нет состояния', () => {
    const state = emptyState()
    seed(state, snapshot())

    const outcome = apply(state, frame(11, 'session.started', { brain: 777, round: 23, prefix_hash: 'HASH2' }))

    expect(outcome).toBe('resync')
    expect(state.sessionGone).toBe(false)
    expect(state.round).toBe(23)
  })

  it('смена префикса требует снимка: за ней догоняют память и скиллы', () => {
    const state = emptyState()
    seed(state, snapshot())

    const outcome = apply(
      state,
      frame(11, 'prefix.replaced', { prefix_hash: 'HASH2', system_prompt: 'НОВЫЙ', tools_json: '[]' }),
    )

    expect(outcome).toBe('resync')
    expect(state.prefixHash).toBe('HASH2')
    expect(state.systemPrompt).toBe('НОВЫЙ')
  })

  it('кадры памяти и скиллов применяются и без живой сессии', () => {
    // Сторы процессные: они переживают раунд, и между раундами это единственное, что показывать.
    const state = emptyState()
    seed(state, snapshot({ session: null }))

    apply(state, frame(11, 'memory.updated', { target: 'crew', entries: ['Иван Петров'] }, ''))
    apply(state, frame(12, 'skills.reloaded', { skills: [{ name: 'beta', when: 'к', body: 'т' }] }, ''))

    expect(state.memory?.crew_live).toEqual(['Иван Петров'])
    expect(state.skills.map((s) => s.name)).toEqual(['beta'])
  })

  it('перечитывание библиотеки убирает пропавшие скиллы', () => {
    const state = emptyState()
    seed(state, snapshot())

    apply(state, frame(11, 'skill.updated', { name: 'beta', when: 'к', body: 'т' }, ''))
    expect(state.skills.map((s) => s.name)).toEqual(['alpha', 'beta'])

    apply(state, frame(12, 'skills.reloaded', { skills: [{ name: 'alpha', when: 'когда', body: 'тело' }] }, ''))
    expect(state.skills.map((s) => s.name)).toEqual(['alpha'])
  })

  it('повторное применение всего, кроме append, ничего не портит', () => {
    // Снимок на сервере не атомарен: seq читается первым, поэтому изменение, проехавшее посреди
    // снятия, приедет и в данных, и в потоке. Безопасно это ровно потому, что каждое событие
    // несёт новое значение целиком.
    const state = emptyState()
    seed(state, snapshot())

    const frames: AgentEventFrame[] = [
      frame(11, 'memory.updated', { target: 'memory', entries: ['одна'] }, ''),
      frame(12, 'skill.updated', { name: 'alpha', when: 'к2', body: 'т2' }, ''),
      frame(13, 'stats', stats(2)),
    ]

    for (const f of frames) apply(state, f)
    const once = JSON.stringify({ m: state.memory, s: state.skills, st: state.stats })

    for (const f of frames) apply(state, f)
    const twice = JSON.stringify({ m: state.memory, s: state.skills, st: state.stats })

    expect(twice).toBe(once)
  })
})

describe('seed', () => {
  it('пустая сессия — не ошибка', () => {
    const state = emptyState()
    seed(state, snapshot({ session: null }))

    expect(state.sessionId).toBeNull()
    expect(state.messages).toEqual([])
    expect(state.memory).not.toBeNull()
  })

  it('смена процесса сбрасывает ряд графиков', () => {
    const state = emptyState()
    seed(state, snapshot())
    apply(state, frame(11, 'stats', stats(2)))
    expect(state.series.samples.size).toBe(2)

    seed(state, snapshot({ instance: 'proc-2' }))
    expect(state.series.samples.size).toBe(1)
  })
})
