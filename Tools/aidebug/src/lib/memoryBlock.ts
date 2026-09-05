/**
 * Parsing of the frozen memory block.
 *
 * The live and frozen sides are NOT compared directly, even though they're shown side by side:
 * the live side is an array of strings, while the frozen side is the rendered text that went
 * into the system prompt. The format is set by `MemoryStore.RenderBlock`: a rule of 46 `═`
 * characters, a header line with the fill percentage, another rule, and entries joined by
 * `\n§\n`. Parsing this in the template is a sure way to discover a format change late and in
 * the wrong place.
 */

/** Entry separator. Exactly this one, not a lone `§`: an entry may contain it itself. */
const DELIMITER = '\n§\n'

export interface FrozenBlock {
  /** Header line like "ПАМЯТЬ (…) [5% — 210/4000 символов]", or empty. */
  header: string
  entries: string[]
}

export function parseFrozen(text: string): FrozenBlock {
  if (!text.trim())
    return { header: '', entries: [] }

  const lines = text.split('\n')

  // The header is exactly three lines: rule, header, rule. We look for the second rule rather
  // than counting to three: if the format ever changes, better to return the whole text than to
  // cut an entry in half.
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

/** How many characters the entries occupy — by the same measure the server uses. */
export function usedChars(entries: string[]): number {
  return entries.join(DELIMITER).length
}

/**
 * Entries that exist live but that the model doesn't see yet.
 *
 * This is the whole point of the screen: an edit lands on disk immediately, but only reaches the
 * system prompt on the next prefix rebuild.
 */
export function pendingEntries(live: string[], frozen: string[]): Set<string> {
  const seen = new Set(frozen)
  return new Set(live.filter((e) => !seen.has(e)))
}
