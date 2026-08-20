import { describe, expect, it } from 'vitest'
import {
  applyAgent,
  applyGlobal,
  emptyAgent,
  emptyGlobals,
  seedAgent,
  seedGlobals,
} from '../src/stream/apply'
import type { AgentEventFrame } from '../src/api/types'
import { frame, session, snapshot, stats } from './fixtures'

/**
 * Машина состояний клиента.
 *
 * Тот же приём, которым сервер доказывает свою шину в `BusReplayTests`: прогнать поток кадров и
 * сверить итог. Здесь важнее не счастливый путь, а моменты, когда применить кадр честно нельзя —
 * потому что молча разъехавшаяся лента выглядит совершенно правдоподобно.
 *
 * С разделением на агента и процессные хранилища тестов стало два набора, и это не формальность:
 * кадры памяти обязаны применяться независимо от того, кто выбран, а пересев одного агента —
 * не задевать соседей.
 */

function seeded(id = 'core') {
  const view = emptyAgent(id)
  seedAgent(view, session(id))
  return view
}

function globals() {
  const g = emptyGlobals()
  seedGlobals(g, snapshot())
  return g
}

describe('applyAgent', () => {
  it('дописывает сообщение, когда индекс и эпоха сходятся', () => {
    const view = seeded()

    const outcome = applyAgent(
      view,
      frame(11, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'assistant', content: 'ответ', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(outcome).toBe('ok')
    expect(view.messages).toHaveLength(2)
    expect(view.messages[1].content).toBe('ответ')
  })

  it('требует снимка, когда индекс разъехался', () => {
    // Это ловит и потерянное сообщение, и задвоенное, и вторую петлю опроса. Единственное
    // неидемпотентное событие — и единственное, у которого есть эта проверка.
    const view = seeded()

    const outcome = applyAgent(
      view,
      frame(11, 'message.appended', {
        body_epoch: 0,
        index: 5,
        message: { index: 5, role: 'assistant', content: 'из будущего', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(outcome).toBe('resync')
    expect(view.messages).toHaveLength(1)
  })

  it('требует снимка, когда сменилась эпоха тела', () => {
    const view = seeded()

    const outcome = applyAgent(
      view,
      frame(11, 'message.appended', {
        body_epoch: 3,
        index: 1,
        message: { index: 1, role: 'user', content: 'после компакции', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(outcome).toBe('resync')
  })

  it('заменяет историю целиком и запоминает новую эпоху', () => {
    const view = seeded()

    applyAgent(
      view,
      frame(11, 'history.replaced', {
        body_epoch: 1,
        messages: [{ index: 0, role: 'system', content: 'сжато', tool_calls: null, tool_call_id: null }],
      }),
    )

    expect(view.bodyEpoch).toBe(1)
    expect(view.messages).toHaveLength(1)
    expect(view.messages[0].content).toBe('сжато')
  })

  it('после конца сессии не применяет её кадры', () => {
    // Окно зомби: Release публикует session.ended, затем отменяет токен и уходит, а `finally`
    // петли ещё допишет синтетические результаты и последний stats. Отличить их можно только по
    // тому, что конец уже видели.
    const view = seeded()

    applyAgent(view, frame(11, 'session.ended', { brain: 50611, reason: 'раунд кончился' }))
    applyAgent(
      view,
      frame(12, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'tool', content: 'хвост', tool_calls: null, tool_call_id: 'x' },
      }),
    )

    expect(view.ended).toBe(true)
    expect(view.messages).toHaveLength(1)
  })

  it('начало сессии требует снимка, потому что в кадре нет состояния', () => {
    const view = seeded()

    const outcome = applyAgent(view, frame(11, 'session.started', {
      brain: 777,
      round: 23,
      prefix_hash: 'HASH2',
    }))

    expect(outcome).toBe('resync')
    expect(view.brain).toBe(777)
    expect(view.startedSeq).toBe(11)
  })

  it('смена префикса применяется НА МЕСТЕ и снимка не требует', () => {
    // Изменение против прежнего поведения, и оно про масштаб. На одном агенте пересев по каждой
    // компакции был терпим; на четырёх компакции случаются вчетверо чаще, и прежнее правило дало
    // бы отладчик, который непрерывно моргает. Payload несёт всё, что нужно.
    const view = seeded()

    const outcome = applyAgent(view, frame(11, 'prefix.replaced', {
      prefix_hash: 'HASH2',
      system_prompt: 'НОВЫЙ ПРОМПТ',
      tools_json: '[{}]',
    }))

    expect(outcome).toBe('ok')
    expect(view.prefixHash).toBe('HASH2')
    expect(view.systemPrompt).toBe('НОВЫЙ ПРОМПТ')
    expect(view.toolsJson).toBe('[{}]')
  })

  it('конец сессии одного агента не глушит другого', () => {
    const core = seeded('core')
    const borg = seeded('combat-1')

    applyAgent(core, frame(11, 'session.ended', { brain: 1, reason: 'освобождён' }))
    applyAgent(
      borg,
      frame(12, 'message.appended', {
        body_epoch: 0,
        index: 1,
        message: { index: 1, role: 'assistant', content: 'иду', tool_calls: null, tool_call_id: null },
      }),
    )

    expect(core.ended).toBe(true)
    expect(borg.ended).toBe(false)
    expect(borg.messages).toHaveLength(2)
  })
})

describe('applyGlobal', () => {
  it('кадры памяти и скиллов применяются, кто бы ни был выбран', () => {
    const g = globals()

    applyGlobal(g, frame(11, 'memory.updated', { entries: ['новая'] }, ''))
    applyGlobal(g, frame(12, 'skill.updated', { name: 'beta', when: 'к', body: 'т' }, ''))

    expect(g.memory?.memory_live).toEqual(['новая'])
    expect(g.skills.map((s) => s.name)).toEqual(['alpha', 'beta'])
  })

  it('перечитывание библиотеки убирает пропавшие скиллы', () => {
    const g = globals()

    applyGlobal(g, frame(11, 'skills.reloaded', { skills: [{ name: 'gamma', when: 'к', body: 'т' }] }, ''))

    expect(g.skills.map((s) => s.name)).toEqual(['gamma'])
  })

  it('снимок со старого сервера, ещё не знающего о заметках, не роняет клиент', () => {
    // Страница и сервер выкатываются РАЗНЫМИ шагами, и между ними всегда есть окно, когда свежий
    // клиент говорит со старым сервером. Отладчик, падающий в этом окне, отнимает ровно тот
    // инструмент, которым разбираются, что случилось.
    const g = emptyGlobals()
    const old = snapshot()
    delete (old as { notes?: unknown }).notes
    delete (old as { note_limit?: unknown }).note_limit

    seedGlobals(g, old)

    expect(g.notes).toEqual([])
    expect(g.noteLimit).toBe(0)
  })

  it('заметка о человеке обновляется и добавляется в порядке слага', () => {
    const g = globals()

    applyGlobal(g, frame(11, 'note.updated', {
      slug: 'aaron-ward',
      name: 'Aaron Ward',
      entries: ['[раунд 8] просил доступ'],
    }, ''))

    expect(g.notes.map((n) => n.slug)).toEqual(['aaron-ward', 'autumn-treeby'])
  })

  it('пустой список записей закрывает заметку, а не рисует пустого человека', () => {
    const g = globals()

    applyGlobal(g, frame(11, 'note.updated', {
      slug: 'autumn-treeby',
      name: 'Autumn Treeby',
      entries: [],
    }, ''))

    expect(g.notes).toEqual([])
  })

  it('перечитывание заметок убирает пропавшие', () => {
    const g = globals()

    applyGlobal(g, frame(11, 'notes.reloaded', { notes: [] }, ''))

    expect(g.notes).toEqual([])
  })

  it('повторное применение ничего не портит', () => {
    // Снимок на сервере не атомарен: seq читается первым, поэтому изменение, проехавшее посреди
    // снятия, приедет и в данных, и в потоке. Безопасно это ровно потому, что каждое событие
    // несёт новое значение целиком.
    const g = globals()
    const view = seeded()

    const globalFrames: AgentEventFrame[] = [
      frame(11, 'memory.updated', { entries: ['одна'] }, ''),
      frame(12, 'skill.updated', { name: 'alpha', when: 'к2', body: 'т2' }, ''),
    ]

    for (const f of globalFrames) applyGlobal(g, f)
    applyAgent(view, frame(13, 'stats', stats(2)))
    const once = JSON.stringify({ m: g.memory, s: g.skills, st: view.stats })

    for (const f of globalFrames) applyGlobal(g, f)
    applyAgent(view, frame(13, 'stats', stats(2)))
    const twice = JSON.stringify({ m: g.memory, s: g.skills, st: view.stats })

    expect(twice).toBe(once)
  })
})

describe('seedAgent', () => {
  it('смена мозга сбрасывает ряд графиков', () => {
    const view = emptyAgent('core')
    seedAgent(view, session())
    applyAgent(view, frame(11, 'stats', stats(2)))
    expect(view.series.samples.size).toBe(2)

    seedAgent(view, session('core', { brain: 999 }))
    expect(view.series.samples.size).toBe(1)
  })
})
