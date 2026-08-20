import { describe, expect, it } from 'vitest'
import { emptySeries, ordered, pushSample, segments } from '../src/lib/series'
import type { AgentStats } from '../src/api/types'

function stats(turn: number, cache = 0.98, tokens = 9000): AgentStats {
  return {
    turns: turn,
    conv_turns: turn,
    untooled_replies: 0,
    idle_turns: 0,
    consecutive_failures: 0,
    broken_promises: 0,
    compactions: 0,
    last_prompt_tokens: tokens,
    chars_per_token: 3,
    body_chars: 400,
    context_limit: 0,
    cache_last_ratio: cache,
    cache_mean_ratio: cache,
    cache_alarms: 0,
    queue_depth: 0,
    mode: 'Core',
    last_error: null,
    volatile_tail: null,
  }
}

describe('series', () => {
  it('копит точки по номеру хода', () => {
    const series = emptySeries()
    pushSample(series, stats(1, 0.9))
    pushSample(series, stats(2, 0.95))

    expect(ordered(series).map((s) => s.turn)).toEqual([1, 2])
    expect(ordered(series)[1].cacheRatio).toBe(0.95)
  })

  it('пропуск запоминается и рвёт линию', () => {
    // Прямая через пропуск читается как плавный тренд, которого не было, — а вся ценность этих
    // линий в том, чтобы заметить просевший кэш.
    const series = emptySeries()
    pushSample(series, stats(1))
    pushSample(series, stats(2))
    pushSample(series, stats(9))

    expect(series.gaps.has(5)).toBe(true)

    const parts = segments(series)
    expect(parts).toHaveLength(2)
    expect(parts[0].map((s) => s.turn)).toEqual([1, 2])
    expect(parts[1].map((s) => s.turn)).toEqual([9])
  })

  it('ход назад сбрасывает ряд', () => {
    // Новая сессия начинает счёт заново; последний сэмпл умирающей приходит из окна зомби. И то,
    // и другое означает, что предыдущий ряд кончился.
    const series = emptySeries()
    pushSample(series, stats(7))
    pushSample(series, stats(8))
    pushSample(series, stats(1))

    expect(ordered(series).map((s) => s.turn)).toEqual([1])
    expect(series.gaps.size).toBe(0)
  })

  it('повторный сэмпл того же хода не задваивает точку', () => {
    const series = emptySeries()
    pushSample(series, stats(3, 0.5))
    pushSample(series, stats(3, 0.7))

    expect(ordered(series)).toHaveLength(1)
    expect(ordered(series)[0].cacheRatio).toBe(0.7)
  })

  it('непрерывный ряд — один отрезок', () => {
    const series = emptySeries()
    for (let turn = 1; turn <= 20; turn++) pushSample(series, stats(turn))

    expect(segments(series)).toHaveLength(1)
  })
})
