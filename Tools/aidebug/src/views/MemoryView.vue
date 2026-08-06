<script setup lang="ts">
import { computed, ref } from 'vue'
import { useAgent } from '../stores/agent'
import { useSettings } from '../stores/settings'
import { postCommand } from '../api/client'
import { parseFrozen, pendingEntries, usedChars } from '../lib/memoryBlock'
import type { AgentCommandResult } from '../api/types'

const agent = useAgent()
const settings = useSettings()

const target = ref<'memory' | 'crew'>('memory')
const draft = ref('')
const busy = ref(false)
const result = ref<AgentCommandResult | null>(null)
const error = ref('')

const live = computed(() =>
  target.value === 'memory' ? (agent.state.memory?.memory_live ?? []) : (agent.state.memory?.crew_live ?? []),
)

const frozenText = computed(() =>
  target.value === 'memory' ? (agent.state.memory?.memory_frozen ?? '') : (agent.state.memory?.crew_frozen ?? ''),
)

const limit = computed(() =>
  target.value === 'memory' ? (agent.state.memory?.memory_limit ?? 0) : (agent.state.memory?.crew_limit ?? 0),
)

const frozen = computed(() => parseFrozen(frozenText.value))
const pending = computed(() => pendingEntries(live.value, frozen.value.entries))
const used = computed(() => usedChars(live.value))

/** Единственная кнопка записи — и она single-flight. */
async function send(action: 'add' | 'remove', match?: string): Promise<void> {
  if (busy.value) return

  busy.value = true
  error.value = ''
  result.value = null

  try {
    result.value = await postCommand(
      { baseUrl: settings.baseUrl, token: settings.token },
      action === 'add'
        ? { type: 'memory.change', target: target.value, action: 'add', content: draft.value }
        : { type: 'memory.change', target: target.value, action: 'remove', match: match ?? '' },
    )
    if (action === 'add') draft.value = ''
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div v-if="!agent.state.memory" class="empty">Память ещё не загружена.</div>

  <div v-else class="memory">
    <div class="bar">
      <button :class="{ active: target === 'memory' }" @click="target = 'memory'">Станция</button>
      <button :class="{ active: target === 'crew' }" @click="target = 'crew'">Экипаж</button>

      <span class="gauge mono">
        {{ used }} / {{ limit }} символов
        <span class="track"><span class="fill" :style="{ width: Math.min(100, (used / limit) * 100) + '%' }" /></span>
      </span>
    </div>

    <p class="note">
      Правка ложится на диск сразу, а модель продолжает читать замороженный текст до следующей
      перестройки префикса. Записи, которых модель ещё не видит, помечены.
    </p>

    <div class="columns">
      <section>
        <h3>Живые записи <span class="dim">— то, что на диске</span></h3>
        <p v-if="!live.length" class="dim">Пусто.</p>
        <div v-for="entry in live" :key="entry" class="entry" :class="{ pending: pending.has(entry) }">
          <div class="text">{{ entry }}</div>
          <div class="side">
            <span v-if="pending.has(entry)" class="tag">модель не видит</span>
            <button :disabled="busy" @click="send('remove', entry.slice(0, 40))">удалить</button>
          </div>
        </div>
      </section>

      <section>
        <h3>Замороженный текст <span class="dim">— то, что читает модель</span></h3>
        <div v-if="frozen.header" class="header mono">{{ frozen.header }}</div>
        <p v-if="!frozen.entries.length" class="dim">Пусто.</p>
        <div v-for="entry in frozen.entries" :key="entry" class="entry frozen">
          <div class="text">{{ entry }}</div>
        </div>
      </section>
    </div>

    <div class="add">
      <textarea v-model="draft" rows="2" placeholder="новая запись" />
      <button :disabled="busy || !draft.trim()" @click="send('add')">
        {{ busy ? 'пишу…' : 'Добавить' }}
      </button>
    </div>

    <p v-if="error" class="bad">{{ error }}</p>
    <p v-else-if="result?.ok" class="ok">
      {{ result.message }} · {{ result.usage }} · модель увидит: {{ result.visible_to_model }}
    </p>
  </div>
</template>

<style scoped>
.bar {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-bottom: 10px;
}

button.active {
  border-color: var(--user);
  color: var(--text);
}

.gauge {
  margin-left: auto;
  color: var(--dim);
  display: flex;
  gap: 8px;
  align-items: center;
}

.track {
  display: inline-block;
  width: 120px;
  height: 6px;
  background: var(--panel-2);
  border-radius: 3px;
  overflow: hidden;
}

.fill {
  display: block;
  height: 100%;
  background: var(--user);
}

.note {
  color: var(--dim);
  max-width: 80ch;
  margin: 0 0 14px;
}

.columns {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 20px;
}

@media (max-width: 900px) {
  .columns {
    grid-template-columns: 1fr;
  }
}

h3 {
  font-size: 13px;
  font-weight: 600;
  margin: 0 0 8px;
}

.dim {
  color: var(--dim);
  font-weight: 400;
}

.header {
  color: var(--dim);
  margin-bottom: 8px;
}

.entry {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 6px 8px;
  border: 1px solid var(--line);
  border-radius: 4px;
  margin-bottom: 6px;
  background: var(--panel);
}

.entry.pending {
  border-color: var(--tool);
}

.entry.frozen {
  opacity: 0.75;
}

.text {
  flex: 1;
  white-space: pre-wrap;
  word-break: break-word;
}

.side {
  display: flex;
  gap: 6px;
  align-items: center;
  flex-shrink: 0;
}

.tag {
  color: var(--tool);
  font-size: 11px;
}

.add {
  display: flex;
  gap: 8px;
  margin-top: 16px;
}

.add textarea {
  flex: 1;
  resize: vertical;
}

.empty,
.bad {
  color: var(--bad);
}

.ok {
  color: var(--assistant);
}
</style>
