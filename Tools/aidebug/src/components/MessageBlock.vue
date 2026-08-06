<script setup lang="ts">
import { computed } from 'vue'
import ToolCallBlock from './ToolCallBlock.vue'
import JsonBlock from './JsonBlock.vue'
import type { ConversationRow } from '../lib/pairing'

const props = defineProps<{ row: ConversationRow; paired: boolean }>()

const ROLE_LABEL: Record<string, string> = {
  user: 'наблюдение',
  assistant: 'ИИ',
  tool: 'результат',
  system: 'система',
}

/**
 * Наблюдение приходит одним куском, где первая строка — это SELF: где глаз, есть ли питание,
 * какая тревога. Это первое, что спрашивают, когда агент ведёт себя странно, поэтому она
 * вытаскивается в отдельную строку, а не тонет в тексте.
 */
const selfLine = computed(() => {
  if (props.row.message.role !== 'user') return null
  const line = (props.row.message.content ?? '').split('\n').find((l) => l.startsWith('SELF '))
  return line ?? null
})

const bodyText = computed(() => {
  const content = props.row.message.content ?? ''
  if (!selfLine.value) return content
  return content
    .split('\n')
    .filter((l) => !l.startsWith('SELF '))
    .join('\n')
    .trim()
})
</script>

<template>
  <div class="msg" :class="row.message.role">
    <div class="head mono">
      <span class="role">{{ ROLE_LABEL[row.message.role] ?? row.message.role }}</span>
      <span class="idx">#{{ row.message.index }}</span>
      <span v-if="row.orphanResult" class="orphan">результат без вызова</span>
    </div>

    <div v-if="selfLine" class="self mono">{{ selfLine }}</div>

    <div v-if="bodyText" class="text">{{ bodyText }}</div>

    <!-- Результат сироты — это JSON, а не проза: печатаем как JSON. -->
    <JsonBlock v-if="row.orphanResult" :raw="row.message.content ?? ''" />

    <template v-if="paired">
      <ToolCallBlock v-for="(pair, i) in row.calls" :key="i" :pair="pair" />
    </template>
    <template v-else>
      <div v-for="(pair, i) in row.calls" :key="i" class="linear">
        <div class="label mono">{{ pair.call.name }}</div>
        <JsonBlock :raw="pair.call.arguments" />
      </div>
    </template>
  </div>
</template>

<style scoped>
.msg {
  /* Блоки во всю ширину, а не «пузыри»: JSON инструментов требует горизонтали. */
  border-left: 3px solid var(--system);
  padding: 6px 0 6px 10px;
  margin-bottom: 10px;
}

.msg.user {
  border-left-color: var(--user);
}

.msg.assistant {
  border-left-color: var(--assistant);
}

.msg.tool {
  border-left-color: var(--tool);
}

.head {
  display: flex;
  gap: 8px;
  align-items: baseline;
  color: var(--dim);
  font-size: 11px;
  margin-bottom: 3px;
}

.role {
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.orphan {
  color: var(--bad);
}

.self {
  color: var(--user);
  opacity: 0.85;
  margin-bottom: 4px;
  word-break: break-word;
}

.text {
  white-space: pre-wrap;
  word-break: break-word;
}

.linear {
  margin: 4px 0;
}

.label {
  color: var(--tool);
  font-size: 11.5px;
}
</style>
