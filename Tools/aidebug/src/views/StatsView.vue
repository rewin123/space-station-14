<script setup lang="ts">
import { computed } from 'vue'
import { useAgent } from '../stores/agent'
import Sparkline from '../components/Sparkline.vue'
import NoSession from '../components/NoSession.vue'

const agent = useAgent()
const stats = computed(() => agent.current.stats)
const turn = computed(() => agent.current.lastTurn)

const percent = (v: number) => `${(v * 100).toFixed(1)}%`

/**
 * Три счётчика ходов различаются, и когда они расходятся — это и есть диагноз.
 * `turns` считает ходы петли, `conv_turns` — добавленные user-сообщения (подталкивания тоже),
 * `compactions` — свёртки.
 */
const ROWS: { key: keyof NonNullable<typeof stats.value>; title: string; hint?: string }[] = [
  { key: 'turns', title: 'ходов петли' },
  { key: 'conv_turns', title: 'user-сообщений', hint: 'больше ходов — значит были подталкивания' },
  { key: 'compactions', title: 'свёрток' },
  { key: 'untooled_replies', title: 'ответов прозой', hint: 'должно оставаться около нуля' },
  {
    key: 'idle_turns',
    title: 'ходов с noop',
    hint: 'молчал по решению; ноль при молчащем ИИ — значит молчит не по своей воле',
  },
  { key: 'broken_promises', title: 'обещал и не сделал' },
  { key: 'consecutive_failures', title: 'ошибок подряд' },
  { key: 'cache_alarms', title: 'тревог кэша' },
  { key: 'last_prompt_tokens', title: 'токенов в промпте' },
  { key: 'body_chars', title: 'символов в теле' },
  { key: 'chars_per_token', title: 'символов на токен' },
  { key: 'context_limit', title: 'окно модели', hint: '0 — сервер модели его не сообщил' },
  { key: 'queue_depth', title: 'наблюдений в очереди' },
]
</script>

<template>
  <NoSession v-if="!stats" />

  <div v-else class="stats">
    <div class="charts">
      <Sparkline
        :series="agent.current.series"
        :pick="(s) => s.cacheRatio"
        :max="1"
        :format="percent"
        label="доля префикс-кэша по ходам"
      />
      <Sparkline
        :series="agent.current.series"
        :pick="(s) => s.promptTokens"
        label="токенов в промпте по ходам"
      />
    </div>

    <p class="note">
      Ряд копится клиентом: сервер истории не хранит, снимок даёт одну точку. Пропуски рисуются
      разрывом, а не прямой — прямая через пропуск читается как плавный тренд, которого не было.
    </p>

    <div class="grid">
      <div class="card">
        <div class="k">режим</div>
        <div class="v">{{ stats.mode }}</div>
      </div>
      <div class="card">
        <div class="k">кэш последний / средний</div>
        <div class="v mono">{{ percent(stats.cache_last_ratio) }} / {{ percent(stats.cache_mean_ratio) }}</div>
      </div>
      <div v-for="row in ROWS" :key="row.key" class="card">
        <div class="k">{{ row.title }}</div>
        <div class="v mono">{{ stats[row.key] }}</div>
        <div v-if="row.hint" class="h">{{ row.hint }}</div>
      </div>
    </div>

    <p v-if="stats.last_error" class="bad">последняя ошибка: {{ stats.last_error }}</p>

    <section v-if="turn" class="turn">
      <h3>Последний ход</h3>
      <div class="grid">
        <div class="card"><div class="k">номер</div><div class="v mono">{{ turn.index }}</div></div>
        <div class="card"><div class="k">фаза</div><div class="v mono">{{ turn.phase }}</div></div>
        <div class="card"><div class="k">выход</div><div class="v mono">{{ turn.exit }}</div></div>
        <div class="card"><div class="k">доставка</div><div class="v mono">{{ turn.delivery }}</div></div>
        <div class="card"><div class="k">шагов</div><div class="v mono">{{ turn.step }}</div></div>
        <div class="card"><div class="k">вызовов</div><div class="v mono">{{ turn.tool_calls }}</div></div>
        <div class="card"><div class="k">говорил</div><div class="v">{{ turn.spoke ? 'да' : 'нет' }}</div></div>
        <div class="card"><div class="k">подтолкнули</div><div class="v">{{ turn.nudged ? 'да' : 'нет' }}</div></div>
        <div class="card"><div class="k">к нему обращались</div><div class="v">{{ turn.addressed ? 'да' : 'нет' }}</div></div>
        <div class="card"><div class="k">принудительный</div><div class="v">{{ turn.forced ? 'да' : 'нет' }}</div></div>
      </div>
      <p v-if="turn.promised" class="promise">обещал: {{ turn.promised }}</p>
    </section>
  </div>
</template>

<style scoped>
.charts {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 12px;
}

@media (max-width: 900px) {
  .charts {
    grid-template-columns: 1fr;
  }
}

.note {
  color: var(--dim);
  max-width: 80ch;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 8px;
}

.card {
  border: 1px solid var(--line);
  border-radius: 4px;
  padding: 7px 10px;
  background: var(--panel);
}

.k {
  color: var(--dim);
  font-size: 11px;
}

.v {
  font-size: 16px;
  margin-top: 2px;
}

.h {
  color: var(--dim);
  font-size: 10.5px;
  margin-top: 3px;
}

.turn {
  margin-top: 20px;
}

h3 {
  font-size: 13px;
  margin: 0 0 8px;
}

.bad {
  color: var(--bad);
}

.promise {
  color: var(--tool);
}
</style>
