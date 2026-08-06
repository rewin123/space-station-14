import { describe, expect, it } from 'vitest'
import { pairConversation } from '../src/lib/pairing'
import type { AgentMessage, AgentToolCall } from '../src/api/types'

function user(index: number, content: string): AgentMessage {
  return { index, role: 'user', content, tool_calls: null, tool_call_id: null }
}

function assistant(index: number, calls: AgentToolCall[]): AgentMessage {
  return { index, role: 'assistant', content: null, tool_calls: calls, tool_call_id: null }
}

function tool(index: number, id: string, content: string): AgentMessage {
  return { index, role: 'tool', content, tool_calls: null, tool_call_id: id }
}

function call(id: string, name: string, args = '{}'): AgentToolCall {
  return { id, name, arguments: args }
}

describe('pairConversation', () => {
  it('связывает результат с вызовом по идентификатору', () => {
    const rows = pairConversation(
      [
        user(0, 'наблюдение'),
        assistant(1, [call('call_1', 'look')]),
        tool(2, 'call_1', '{"ok":true}'),
      ],
      0,
    )

    expect(rows).toHaveLength(2)
    expect(rows[1].calls[0].state).toBe('paired')
    expect(rows[1].calls[0].result?.content).toBe('{"ok":true}')
  })

  it('вызов без результата — «ждём», а не ошибка', () => {
    const rows = pairConversation([user(0, 'н'), assistant(1, [call('call_1', 'radio')])], 0)

    expect(rows[1].calls[0].state).toBe('pending')
    expect(rows[1].calls[0].result).toBeNull()
  })

  it('пустые идентификаторы раскладываются по порядку и помечаются неоднозначными', () => {
    // Настоящий случай: сервер модели не прислал id, и все вызовы хода получили пустую строку.
    // Глобальная карта по id связала бы все три результата с первым вызовом и молча спрятала
    // два — ровно та поломка, ради которой отладчик и открывают.
    const rows = pairConversation(
      [
        user(0, 'н'),
        assistant(1, [call('', 'look'), call('', 'inspect'), call('', 'radio')]),
        tool(2, '', '{"n":1}'),
        tool(3, '', '{"n":2}'),
        tool(4, '', '{"n":3}'),
      ],
      0,
    )

    const calls = rows[1].calls
    expect(calls).toHaveLength(3)
    expect(calls.map((c) => c.result?.content)).toEqual(['{"n":1}', '{"n":2}', '{"n":3}'])
    expect(calls.every((c) => c.state === 'ambiguous')).toBe(true)
  })

  it('повторяющиеся идентификаторы не съедают друг друга', () => {
    // После восстановления из снапшота счётчик вызовов начинается заново, а тело возвращается со
    // старыми номерами — так в одной ленте появляются два call_1.
    const rows = pairConversation(
      [
        user(0, 'н'),
        assistant(1, [call('call_1', 'look'), call('call_1', 'inspect')]),
        tool(2, 'call_1', '{"первый":true}'),
        tool(3, 'call_1', '{"второй":true}'),
      ],
      0,
    )

    const calls = rows[1].calls
    expect(calls[0].result?.content).toBe('{"первый":true}')
    expect(calls[1].result?.content).toBe('{"второй":true}')
  })

  it('синтетический результат турного бюджета встаёт на своё место', () => {
    // CloseTurn дописывает их через тот же путь, что и настоящие результаты, с идентификатором
    // висящего вызова. Вложенным он читается как «этот вызов не успел», а линейно — как ошибка.
    const rows = pairConversation(
      [
        user(0, 'н'),
        assistant(1, [call('call_9', 'device_action')]),
        tool(2, 'call_9', '{"ok":false,"error":"turn_budget"}'),
      ],
      0,
    )

    expect(rows[1].calls[0].state).toBe('paired')
    expect(rows[1].calls[0].result?.content).toContain('turn_budget')
  })

  it('результат, который никто не забрал, остаётся видимым', () => {
    const rows = pairConversation([user(0, 'н'), tool(1, 'call_нет', '{"ok":true}')], 0)

    const orphan = rows.find((r) => r.orphanResult)
    expect(orphan).toBeDefined()
    expect(orphan!.message.content).toBe('{"ok":true}')
  })

  it('ключ строки включает эпоху тела', () => {
    // Позиция в массиве ключом быть не может: history.replaced перенумеровывает всё, и узлы,
    // привязанные к позиции, начнут переиспользоваться под чужие сообщения.
    const rows = pairConversation([user(0, 'н')], 3)
    expect(rows[0].key).toBe('3:0')
  })

  it('результат не утекает через границу хода', () => {
    const rows = pairConversation(
      [
        user(0, 'ход один'),
        assistant(1, [call('call_1', 'look')]),
        tool(2, 'call_1', '{"первый":true}'),
        user(3, 'ход два'),
        tool(4, 'call_потерянный', '{"чужой":true}'),
      ],
      0,
    )

    expect(rows.find((r) => r.orphanResult)?.message.index).toBe(4)
  })
})
