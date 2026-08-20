<script setup lang="ts">
import { computed, ref } from 'vue'
import { useAgent } from '../stores/agent'
import JsonBlock from '../components/JsonBlock.vue'
import NoSession from '../components/NoSession.vue'

const agent = useAgent()
const tab = ref<'zone0' | 'tools' | 'full'>('zone0')

/**
 * Полный промпт, собранный так же, как `ConversationState.Build()`: системное сообщение, тело,
 * и хвост зоны 2 последним.
 *
 * Этого артефакта не видит никто. Зона 2 при этом закопана в статистике, хотя она — часть каждого
 * запроса, и однажды её утечка стоила постоянного налога на кэш: она ставилась и не очищалась,
 * а поскольку всегда идёт ПОСЛЕ тела, каждое новое наблюдение сдвигало её и заставляло сервер
 * пересчитывать всё с её позиции.
 */
const assembled = computed(() => {
  const parts: string[] = [`=== СИСТЕМНОЕ СООБЩЕНИЕ (зона 0) ===\n${agent.current.systemPrompt}`]

  for (const m of agent.current.messages) {
    const calls = (m.tool_calls ?? []).map((c) => `${c.name}(${c.arguments})`).join(' ')
    parts.push(`--- ${m.role}${m.tool_call_id ? ` [${m.tool_call_id}]` : ''} ---\n${m.content ?? ''}${calls ? '\n' + calls : ''}`)
  }

  const tail = agent.current.stats?.volatile_tail
  if (tail)
    parts.push(`=== ХВОСТ (зона 2) ===\n${tail}`)

  return parts.join('\n\n')
})

const chars = computed(() => assembled.value.length)
</script>

<template>
  <NoSession v-if="!agent.current.id" />

  <div v-else class="prompt">
    <div class="bar">
      <button :class="{ active: tab === 'zone0' }" @click="tab = 'zone0'">Зона 0</button>
      <button :class="{ active: tab === 'tools' }" @click="tab = 'tools'">Схемы тулов</button>
      <button :class="{ active: tab === 'full' }" @click="tab = 'full'">Полный промпт</button>

      <span class="hash mono">
        префикс {{ agent.current.prefixHash }}
        · {{ agent.current.stats?.last_prompt_tokens ?? 0 }}т
      </span>
    </div>

    <div v-if="agent.current.stats?.volatile_tail" class="tail">
      <div class="label">зона 2 — временный хвост, уезжает вместе с ходом, который его отправил</div>
      <div class="text">{{ agent.current.stats.volatile_tail }}</div>
    </div>

    <pre v-if="tab === 'zone0'" class="text mono">{{ agent.current.systemPrompt }}</pre>
    <JsonBlock v-else-if="tab === 'tools'" :raw="agent.current.toolsJson" />
    <template v-else>
      <p class="note">
        Собран так же, как это делает <code>ConversationState.Build()</code>: зона 0, тело,
        хвост последним. {{ chars }} символов.
      </p>
      <pre class="text mono">{{ assembled }}</pre>
    </template>
  </div>
</template>

<style scoped>
.bar {
  display: flex;
  gap: 6px;
  align-items: center;
  margin-bottom: 12px;
}

button.active {
  border-color: var(--user);
}

.hash {
  margin-left: auto;
  color: var(--dim);
}

.tail {
  border: 1px solid var(--tool);
  border-radius: 4px;
  padding: 8px 10px;
  margin-bottom: 14px;
}

.label {
  color: var(--tool);
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  margin-bottom: 4px;
}

.text {
  white-space: pre-wrap;
  word-break: break-word;
  margin: 0;
}

.note {
  color: var(--dim);
}
</style>
