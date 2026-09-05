<script setup lang="ts">
import { computed } from 'vue'
import { segments, type StatsSeries } from '../lib/series'

const props = defineProps<{
  series: StatsSeries
  pick: (sample: { cacheRatio: number; promptTokens: number; bodyChars: number }) => number
  label: string
  format?: (value: number) => string
  /** Upper bound, if it's known ahead of time (the cache ratio is always 0..1). */
  max?: number
}>()

const W = 420
const H = 60

const parts = computed(() => segments(props.series))

const bounds = computed(() => {
  const values = parts.value.flat().map((s) => props.pick(s))
  if (!values.length) return { min: 0, max: 1, lo: 0, hi: 1 }

  const lo = Math.min(...parts.value.flat().map((s) => s.turn))
  const hi = Math.max(...parts.value.flat().map((s) => s.turn))
  // Top of the scale: the given one (the cache ratio is always 0..1) or a margin above the
  // maximum. The 1 is a safeguard against an all-zero series, otherwise we'd divide by zero.
  const max = props.max ?? (Math.max(...values) * 1.1 || 1)
  return { min: 0, max, lo, hi }
})

/** Each segment is its own polyline, so a gap shows up as a hole, not a straight line across it. */
const polylines = computed(() =>
  parts.value.map((segment) =>
    segment
      .map((sample) => {
        const { lo, hi, max } = bounds.value
        const x = hi === lo ? W / 2 : ((sample.turn - lo) / (hi - lo)) * W
        const y = H - (Math.min(props.pick(sample), max) / max) * H
        return `${x.toFixed(1)},${y.toFixed(1)}`
      })
      .join(' '),
  ),
)

const last = computed(() => {
  const flat = parts.value.flat()
  return flat.length ? props.pick(flat[flat.length - 1]) : null
})

const fmt = (v: number) => (props.format ? props.format(v) : String(Math.round(v)))
</script>

<template>
  <div class="spark">
    <div class="head">
      <span class="label">{{ label }}</span>
      <span v-if="last !== null" class="value mono">{{ fmt(last) }}</span>
      <span v-if="series.gaps.size" class="gaps mono">пропущено ходов: {{ series.gaps.size }}</span>
    </div>

    <svg :viewBox="`0 0 ${W} ${H}`" preserveAspectRatio="none">
      <polyline v-for="(points, i) in polylines" :key="i" :points="points" />
    </svg>

    <div class="axis mono">
      <span>ход {{ bounds.lo }}</span>
      <span>ход {{ bounds.hi }}</span>
    </div>
  </div>
</template>

<style scoped>
.spark {
  border: 1px solid var(--line);
  border-radius: 4px;
  padding: 8px 10px;
  background: var(--panel);
}

.head {
  display: flex;
  gap: 10px;
  align-items: baseline;
  margin-bottom: 4px;
}

.label {
  color: var(--dim);
  font-size: 12px;
}

.value {
  color: var(--text);
}

.gaps {
  margin-left: auto;
  color: var(--tool);
  font-size: 11px;
}

svg {
  width: 100%;
  height: 60px;
  display: block;
}

polyline {
  fill: none;
  stroke: var(--user);
  stroke-width: 1.5;
  vector-effect: non-scaling-stroke;
}

.axis {
  display: flex;
  justify-content: space-between;
  color: var(--dim);
  font-size: 10.5px;
  margin-top: 2px;
}
</style>
