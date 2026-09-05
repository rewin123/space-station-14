<script setup lang="ts">
import { computed, ref } from 'vue'
import { useAgent } from '../stores/agent'
import { pairConversation } from '../lib/pairing'
import MessageBlock from '../components/MessageBlock.vue'
import NoSession from '../components/NoSession.vue'

const agent = useAgent()

/**
 * The paired view is better in the vast majority of cases, but when the model didn't send call
 * identifiers, it lays out results by order and can get it wrong. The linear view is exactly
 * what actually arrived over the wire, without a single guess.
 */
const paired = ref(true)

const rows = computed(() =>
  paired.value
    ? pairConversation(agent.current.messages, agent.current.bodyEpoch)
    : agent.current.messages.map((message) => ({
        key: `${agent.current.bodyEpoch}:${message.index}`,
        message,
        calls: (message.tool_calls ?? []).map((call) => ({ call, result: null, state: 'pending' as const })),
        orphanResult: message.role === 'tool',
      })),
)

const ambiguous = computed(() =>
  rows.value.some((r) => r.calls.some((c) => c.state === 'ambiguous')),
)
</script>

<template>
  <NoSession v-if="!agent.current.id" />

  <div v-else class="conversation">
    <div class="bar">
      <label class="toggle">
        <input v-model="paired" type="checkbox" />
        вкладывать результаты в вызовы
      </label>

      <span v-if="paired && ambiguous" class="warn">
        часть результатов сопоставлена по порядку — модель не прислала идентификаторы
      </span>

      <span class="meta mono">
        эпоха {{ agent.current.bodyEpoch }} · {{ agent.current.messages.length }} сообщений
      </span>
    </div>

    <div v-if="agent.current.ended" class="ended">
      Сессия завершена. Это её последнее состояние; новые кадры к ней не применяются.
    </div>

    <p v-if="!rows.length" class="empty">
      Разговор пуст — агент ещё не сделал ни одного хода.
    </p>

    <MessageBlock v-for="row in rows" :key="row.key" :row="row" :paired="paired" />

    <div v-if="agent.current.lastTurn" class="turn mono">
      ход {{ agent.current.lastTurn.index }} ·
      {{ agent.current.lastTurn.exit }} / {{ agent.current.lastTurn.delivery }} ·
      шагов {{ agent.current.lastTurn.step }} ·
      вызовов {{ agent.current.lastTurn.tool_calls }}
      <template v-if="agent.current.lastTurn.forced">· принудительный</template>
      <template v-if="agent.current.lastTurn.promised">
        · обещал: {{ agent.current.lastTurn.promised }}
      </template>
    </div>
  </div>
</template>

<style scoped>
.bar {
  display: flex;
  gap: 14px;
  align-items: center;
  margin-bottom: 12px;
  flex-wrap: wrap;
}

.toggle {
  display: flex;
  gap: 6px;
  align-items: center;
  color: var(--dim);
  cursor: pointer;
}

.warn {
  color: var(--bad);
  font-size: 12px;
}

.meta {
  margin-left: auto;
  color: var(--dim);
}

.ended {
  color: var(--tool);
  border: 1px solid var(--tool);
  border-radius: 4px;
  padding: 6px 10px;
  margin-bottom: 12px;
}

.empty {
  color: var(--dim);
}

.turn {
  margin-top: 14px;
  padding-top: 8px;
  border-top: 1px solid var(--line);
  color: var(--dim);
}
</style>
