<script setup lang="ts">
import { computed, ref } from 'vue'
import JsonBlock from './JsonBlock.vue'
import type { PairedCall } from '../lib/pairing'

const props = defineProps<{ pair: PairedCall }>()

const open = ref(false)

/** A tool result is JSON from ToolResult: ok/error/detail/retry/effect. */
const outcome = computed(() => {
  const content = props.pair.result?.content
  if (!content) return null

  try {
    return JSON.parse(content) as { ok?: boolean; error?: string; detail?: string }
  } catch {
    return null
  }
})

const failed = computed(() => outcome.value?.ok === false)

const summary = computed(() => {
  if (props.pair.state === 'pending') return 'ждёт результата'
  if (!outcome.value) return props.pair.result?.content?.slice(0, 80) ?? ''
  if (outcome.value.ok === false) return `${outcome.value.error ?? 'ошибка'}: ${outcome.value.detail ?? ''}`
  return 'ok'
})

const STATE_LABEL: Record<PairedCall['state'], string> = {
  paired: '',
  pending: 'ждём',
  // Always shown: here the paired view can be lying, and staying silent about it isn't allowed.
  ambiguous: 'сопоставлено по порядку',
}
</script>

<template>
  <div class="call" :class="{ failed, pending: pair.state === 'pending' }">
    <div class="head mono" @click="open = !open">
      <span class="chev">{{ open ? '▾' : '▸' }}</span>
      <span class="name">{{ pair.call.name }}</span>
      <span class="summary">{{ summary }}</span>
      <span v-if="STATE_LABEL[pair.state]" class="flag">{{ STATE_LABEL[pair.state] }}</span>
      <span class="id">{{ pair.call.id || 'без id' }}</span>
    </div>

    <div v-if="open" class="body">
      <div class="label">аргументы</div>
      <JsonBlock :raw="pair.call.arguments" />

      <template v-if="pair.result">
        <div class="label">результат</div>
        <JsonBlock :raw="pair.result.content ?? ''" />
      </template>
      <div v-else class="label dim">результата ещё нет — ход не закончился</div>
    </div>
  </div>
</template>

<style scoped>
.call {
  border: 1px solid var(--line);
  border-left: 3px solid var(--tool);
  border-radius: 4px;
  margin: 4px 0;
  background: var(--panel);
}

.call.failed {
  border-left-color: var(--bad);
}

.call.pending {
  border-left-color: var(--system);
}

.head {
  display: flex;
  gap: 8px;
  align-items: baseline;
  padding: 4px 8px;
  cursor: pointer;
}

.name {
  color: var(--tool);
}

.summary {
  color: var(--dim);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.flag {
  color: var(--bad);
  font-size: 11px;
  border: 1px solid var(--bad);
  border-radius: 3px;
  padding: 0 4px;
}

.id {
  margin-left: auto;
  color: var(--dim);
  opacity: 0.5;
  flex-shrink: 0;
}

.body {
  padding: 4px 8px 8px 20px;
}

.label {
  color: var(--dim);
  font-size: 11px;
  margin: 6px 0 2px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.label.dim {
  text-transform: none;
  letter-spacing: 0;
}
</style>
