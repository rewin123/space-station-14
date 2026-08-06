/**
 * Разбор замороженного блока памяти.
 *
 * Живая и замороженная стороны НЕ сравниваются напрямую, хотя показываются рядом: живая — массив
 * строк, а замороженная — отрендеренный текст, который лёг в системный промпт. Формат задан
 * `MemoryStore.RenderBlock`: черта из 46 символов `═`, строка заголовка с процентом заполнения,
 * ещё черта, и записи через `\n§\n`. Разбирать это в шаблоне — верный способ обнаружить формат
 * поздно и не там.
 */

/** Разделитель записей. Ровно он, а не одинокая `§`: запись может содержать её сама. */
const DELIMITER = '\n§\n'

export interface FrozenBlock {
  /** Строка заголовка вида «ПАМЯТЬ (…) [5% — 210/4000 символов]», или пусто. */
  header: string
  entries: string[]
}

export function parseFrozen(text: string): FrozenBlock {
  if (!text.trim())
    return { header: '', entries: [] }

  const lines = text.split('\n')

  // Шапка — это ровно три строки: черта, заголовок, черта. Ищем вторую черту, а не считаем до
  // трёх: если формат однажды поменяется, лучше отдать текст целиком, чем срезать пол-записи.
  let bodyStart = 0
  let header = ''
  let bars = 0

  for (let i = 0; i < lines.length && i < 8; i++) {
    if (/^═+$/.test(lines[i].trim())) {
      bars++
      if (bars === 2) {
        bodyStart = i + 1
        break
      }
    } else if (bars === 1 && !header) {
      header = lines[i].trim()
    }
  }

  const body = lines.slice(bodyStart).join('\n')

  return {
    header,
    entries: body
      .split(DELIMITER)
      .map((e) => e.trim())
      .filter((e) => e.length > 0),
  }
}

/** Сколько символов занимают записи — той же меркой, какой считает сервер. */
export function usedChars(entries: string[]): number {
  return entries.join(DELIMITER).length
}

/**
 * Записи, которые есть живьём, но которых модель ещё не видит.
 *
 * Это и есть весь смысл экрана: правка ложится на диск сразу, а в системный промпт попадает
 * только со следующей перестройкой префикса.
 */
export function pendingEntries(live: string[], frozen: string[]): Set<string> {
  const seen = new Set(frozen)
  return new Set(live.filter((e) => !seen.has(e)))
}
