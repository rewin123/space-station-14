import type { AgentStats } from '../api/types'

export interface StatsSample {
  turn: number
  cacheRatio: number
  promptTokens: number
  bodyChars: number
}

export interface StatsSeries {
  /** The key is the turn number, not the arrival time. */
  samples: Map<number, StatsSample>
  /** Turn numbers we know for certain were skipped. */
  gaps: Set<number>
  lastTurn: number
}

/** No need for more than the server's event ring: beyond that the client gets a resync anyway. */
const MAX_SAMPLES = 500

export function emptySeries(): StatsSeries {
  return { samples: new Map(), gaps: new Set(), lastTurn: -1 }
}

export function resetSeries(series: StatsSeries): void {
  series.samples.clear()
  series.gaps.clear()
  series.lastTurn = -1
}

/**
 * Adds a data point.
 *
 * The key is `stats.turns`, and that's not cosmetic. Arrival time is meaningless here: after a
 * resync, the accumulated batch of `stats` arrives in a single burst, and by time they'd all
 * land in the same second. The turn number, on the other hand, comes from inside the event
 * itself — `AgentStatsDto.Turns` is incremented BEFORE the turn, while the sample is published
 * in a `finally` afterward, so every frame carries the number of the turn it describes.
 *
 * A jump greater than one is a gap (the ring overwrote it, the tab was asleep), and it's
 * remembered so the chart draws a BREAK. A straight line through a gap reads as a smooth trend
 * that never existed — and the entire value of these two lines is noticing when the cache
 * dropped.
 *
 * A turn smaller than the previous one means either a new session (the counter starts over) or
 * the last sample of a dying session from the zombie window. Either way it means "the series is
 * over".
 */
export function pushSample(series: StatsSeries, stats: AgentStats): void {
  const turn = stats.turns

  if (series.lastTurn >= 0 && turn < series.lastTurn) {
    resetSeries(series)
  } else if (series.lastTurn >= 0 && turn > series.lastTurn + 1) {
    for (let missing = series.lastTurn + 1; missing < turn; missing++)
      series.gaps.add(missing)
  }

  series.samples.set(turn, {
    turn,
    cacheRatio: stats.cache_last_ratio,
    promptTokens: stats.last_prompt_tokens,
    bodyChars: stats.body_chars,
  })

  series.lastTurn = Math.max(series.lastTurn, turn)

  if (series.samples.size > MAX_SAMPLES) {
    const oldest = Math.min(...series.samples.keys())
    series.samples.delete(oldest)
    series.gaps.delete(oldest)
  }
}

/** Points in ascending turn order, ready to be drawn. */
export function ordered(series: StatsSeries): StatsSample[] {
  return [...series.samples.values()].sort((a, b) => a.turn - b.turn)
}

/**
 * Splits the series into contiguous segments.
 *
 * Each segment is drawn as its own polyline, so a gap shows up as a hole, not as a straight line.
 */
export function segments(series: StatsSeries): StatsSample[][] {
  const points = ordered(series)
  const result: StatsSample[][] = []
  let current: StatsSample[] = []

  for (const point of points) {
    if (current.length > 0 && point.turn !== current[current.length - 1].turn + 1) {
      result.push(current)
      current = []
    }
    current.push(point)
  }

  if (current.length > 0)
    result.push(current)

  return result
}
