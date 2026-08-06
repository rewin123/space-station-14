import type { AgentMessage, AgentToolCall } from '../api/types'

/**
 * Состояние сопоставления вызова с результатом.
 *
 * Три, а не два, и «неоднозначно» рисуется явно: именно там парный вид врёт, и именно тогда
 * отладчик и нужен.
 */
export type PairState = 'paired' | 'pending' | 'ambiguous'

export interface PairedCall {
  call: AgentToolCall
  /** Сообщение с результатом, если оно уже пришло. */
  result: AgentMessage | null
  state: PairState
}

export interface ConversationRow {
  key: string
  message: AgentMessage
  /** Вызовы этого assistant-сообщения вместе с их результатами. */
  calls: PairedCall[]
  /** Результат, который не забрал ни один вызов, — протокольная странность, прятать нельзя. */
  orphanResult: boolean
}

/**
 * Собрать ленту, вложив результаты под их вызовы.
 *
 * <b>Сопоставление обратным сканированием, а НЕ глобальной картой по id.</b> Это не стилистика:
 * `NextCallId` в сервере используется только в тестах, а настоящие идентификаторы приходят от
 * модели (`LlamaClient`, `idEl.GetString() ?? ""`). Если сервер модели их не прислал, все вызовы
 * хода получат пустую строку — и глобальная карта свяжет все результаты с первым вызовом, молча
 * спрятав остальные. Плюс идентификаторы повторяются после восстановления из снапшота: счётчик
 * вызовов не сохраняется, а тело возвращается со старыми номерами.
 *
 * Поэтому id — только подсказка. От результата идём назад к ближайшему assistant-сообщению со
 * свободным слотом и берём слот по id, а если он пуст или уже занят — по порядку, помечая
 * `ambiguous`.
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
        // Вызов без результата — норма посреди хода, а не ошибка. Отдельное состояние, чтобы
        // «ждём» не выглядело как «сломалось».
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

  // Результаты показываются вложенными, поэтому из плоской ленты их надо убрать — кроме сирот,
  // которые иначе исчезли бы совсем.
  return rows.filter((row) => row.message.role !== 'tool' || row.orphanResult)
}

interface Slot {
  pair: PairedCall
  /** Нашли по идентификатору, а не позиционно. */
  byId: boolean
}

/** Ближайший назад assistant со свободным слотом. */
function findSlot(rows: ConversationRow[], result: AgentMessage): Slot | null {
  const at = rows.findIndex((r) => r.message.index === result.index)

  for (let i = at - 1; i >= 0; i--) {
    const row = rows[i]

    // Дошли до предыдущего user-сообщения — это граница хода, дальше искать нечего.
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

    // Идентификатор пуст, повторяется или уже израсходован — падаем на порядок внутри хода.
    const free = row.calls.find((c) => c.result === null)
    if (free)
      return { pair: free, byId: false }
  }

  return null
}
