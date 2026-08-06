import type { AgentStats } from '../api/types'

export interface StatsSample {
  turn: number
  cacheRatio: number
  promptTokens: number
  bodyChars: number
}

export interface StatsSeries {
  /** Ключ — номер хода, а не время прихода. */
  samples: Map<number, StatsSample>
  /** Номера ходов, про которые точно известно, что они пропущены. */
  gaps: Set<number>
  lastTurn: number
}

/** Больше кольца событий на сервере не надо: дальше клиент всё равно получит resync. */
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
 * Добавить точку.
 *
 * Ключ — `stats.turns`, и это не косметика. Время прихода здесь бессмысленно: после resync пачка
 * накопившихся `stats` приезжает одним залпом, и по времени они лягут в одну секунду. Номер хода
 * же приходит внутри самого события — `AgentStatsDto.Turns` инкрементится ДО хода, а сэмпл
 * публикуется в `finally` после, так что каждый кадр несёт номер того хода, который описывает.
 *
 * Скачок больше единицы — это пропуск (кольцо перезаписало, вкладка спала), и он запоминается,
 * чтобы график нарисовал РАЗРЫВ. Прямая через пропуск читается как плавный тренд, которого не
 * было, — а вся ценность этих двух линий в том, чтобы заметить, когда кэш просел.
 *
 * Ход меньше предыдущего — это либо новая сессия (счётчик начинается заново), либо последний
 * сэмпл умирающей сессии из окна зомби. И то и другое означает «ряд кончился».
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

/** Точки по возрастанию хода, готовые к отрисовке. */
export function ordered(series: StatsSeries): StatsSample[] {
  return [...series.samples.values()].sort((a, b) => a.turn - b.turn)
}

/**
 * Разбить ряд на непрерывные отрезки.
 *
 * Каждый отрезок рисуется своей ломаной, поэтому пропуск виден как дыра, а не как прямая.
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
