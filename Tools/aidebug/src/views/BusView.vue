<script setup lang="ts">
import { computed, ref } from 'vue'
import { useAgent } from '../stores/agent'
import JsonBlock from '../components/JsonBlock.vue'
import type { AgentEventType } from '../api/types'

/**
 * Сырой лог кадров.
 *
 * Сделан первым и намеренно уродливым: это единственный инструмент для отладки самой машины
 * состояний. Данные для него уже есть — без этого экрана клиент разбирал бы каждый кадр и
 * выбрасывал, а когда лента разъедется, смотреть было бы не на что.
 */
const agent = useAgent()

const TYPES: AgentEventType[] = [
  'session.started',
  'session.ended',
  'message.appended',
  'history.replaced',
  'prefix.replaced',
  'memory.updated',
  'skill.updated',
  'skills.reloaded',
  'stats',
]

const hidden = ref(new Set<AgentEventType>())
const expanded = ref(new Set<number>())

function toggleType(type: AgentEventType): void {
  const next = new Set(hidden.value)
  if (next.has(type)) next.delete(type)
  else next.add(type)
  hidden.value = next
}

function toggleFrame(seq: number): void {
  const next = new Set(expanded.value)
  if (next.has(seq)) next.delete(seq)
  else next.add(seq)
  expanded.value = next
}

const visible = computed(() => agent.frames.filter((f) => !hidden.value.has(f.type)).slice().reverse())

/** Пропуск в номерах — либо потерянный кадр, либо вторая петля. Показываем явно. */
function gapBefore(index: number): number {
  const rows = visible.value
  if (index + 1 >= rows.length) return 0
  const newer = rows[index].seq
  const older = rows[index + 1].seq
  return newer - older - 1
}
</script>

<template>
  <div class="bus">
    <div class="filters">
      <button
        v-for="type in TYPES"
        :key="type"
        class="chip mono"
        :class="{ off: hidden.has(type) }"
        @click="toggleType(type)"
      >
        {{ type }}
      </button>
      <span class="count">{{ visible.length }} из {{ agent.frames.length }}</span>
    </div>

    <div v-if="agent.resyncs.length" class="resyncs">
      <div v-for="(r, i) in agent.resyncs.slice(-5)" :key="i" class="resync mono">
        пересинхронизация на seq {{ r.at }}: {{ r.reason }}
      </div>
    </div>

    <p v-if="!agent.frames.length" class="empty">
      Кадров ещё не было. Первый приедет, когда агент сделает ход или кто-нибудь тронет память.
    </p>

    <div v-for="(frame, i) in visible" :key="frame.seq" class="row">
      <div v-if="gapBefore(i) > 0" class="gap mono">
        пропущено {{ gapBefore(i) }} кадров
      </div>

      <div class="head mono" @click="toggleFrame(frame.seq)">
        <span class="seq">{{ frame.seq }}</span>
        <span class="type" :class="frame.type.split('.')[0]">{{ frame.type }}</span>
        <span class="session" :class="{ mine: frame.session === agent.selected }">{{
          frame.session || 'процесс'
        }}</span>
        <span class="chev">{{ expanded.has(frame.seq) ? '▾' : '▸' }}</span>
      </div>

      <JsonBlock v-if="expanded.has(frame.seq)" :value="frame.payload" class="payload" />
    </div>
  </div>
</template>

<style scoped>
.bus {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.filters {
  display: flex;
  flex-wrap: wrap;
  gap: 5px;
  align-items: center;
}

.chip {
  padding: 2px 8px;
  font-size: 11.5px;
}

.chip.off {
  opacity: 0.35;
}

.count {
  margin-left: auto;
  color: var(--dim);
}

.resyncs {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.resync {
  color: var(--tool);
}

.empty {
  color: var(--dim);
}

.row {
  border-bottom: 1px solid var(--line);
}

.gap {
  color: var(--bad);
  padding: 3px 0;
}

.head {
  display: flex;
  gap: 10px;
  padding: 4px 0;
  cursor: pointer;
  align-items: baseline;
}

.head:hover {
  background: var(--panel);
}

.seq {
  color: var(--dim);
  min-width: 52px;
  text-align: right;
}

.type {
  min-width: 150px;
}

.type.message,
.type.history {
  color: var(--assistant);
}

.type.session.mine {
  color: var(--accent, #6ab);
}

.session {
  color: var(--user);
}

.type.memory,
.type.skill,
.type.skills {
  color: var(--tool);
}

.type.prefix {
  color: #a97bd0;
}

.session {
  color: var(--dim);
}

.chev {
  margin-left: auto;
  color: var(--dim);
}

.payload {
  padding: 6px 0 10px 62px;
}
</style>
