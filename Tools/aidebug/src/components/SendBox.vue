<script setup lang="ts">
import { ref } from 'vue'
import { useAgent } from '../stores/agent'
import { useSettings } from '../stores/settings'
import { getHealth, postCommand } from '../api/client'

const agent = useAgent()
const settings = useSettings()

const text = ref('')
const busy = ref(false)
const note = ref('')
const bad = ref(false)
const pending = ref(false)

/**
 * Sends a message to the agent. Single-flight, and NEVER retried automatically.
 *
 * `AgentInbox.Enqueue` on the server glues two messages together with a newline instead of
 * rejecting the second one, and there's no idempotency key on the wire. So a second click — or
 * one automatic retry of a request that actually got through — produces one message with the
 * text doubled.
 *
 * That's also where the pending indicator comes from: up to a full tick (8-25 seconds) passes
 * between sending and the message showing up in the stream. Without feedback the operator would
 * conclude it didn't send and click again.
 */
async function send(): Promise<void> {
  if (busy.value || !text.value.trim()) return

  busy.value = true
  bad.value = false
  note.value = ''

  const endpoint = { baseUrl: settings.baseUrl, token: settings.token }

  try {
    // A recipient is mandatory: the message goes into a specific brain's inbox. Without one the
    // server answers 400 — and rightly so, "to whoever" has no business here.
    const result = await postCommand(endpoint, {
      type: 'message.send',
      agent: agent.selected ?? '',
      text: text.value,
    })
    note.value = `${result.message} (${result.applied})`
    text.value = ''

    // Immediately ask whether it landed in the queue: it's the only way to show that it went out.
    try {
      const health = await getHealth(endpoint)
      pending.value = health.agents.some((a) => a.id === agent.selected && a.pending_input)
    } catch {
      // Health isn't critical here: the message has already been accepted.
    }
  } catch (e) {
    bad.value = true
    note.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="send">
    <input
      v-model="text"
      :disabled="!agent.hasSession"
      :placeholder="agent.hasSession ? 'сообщение агенту — приедет следующим ходом' : 'нет активного агента'"
      @keydown.enter="send()"
    />
    <button :disabled="busy || !agent.hasSession || !text.trim()" @click="send()">
      {{ busy ? 'шлю…' : 'Отправить' }}
    </button>

    <span v-if="pending" class="pending">в очереди</span>
    <span v-if="note" class="note" :class="{ bad }">{{ note }}</span>
  </div>
</template>

<style scoped>
.send {
  display: flex;
  gap: 8px;
  align-items: center;
  padding: 10px 14px;
  background: var(--panel);
  border-top: 1px solid var(--line);
}

input {
  flex: 1;
}

.pending {
  color: var(--tool);
  font-size: 12px;
}

.note {
  color: var(--assistant);
  font-size: 12px;
}

.note.bad {
  color: var(--bad);
}
</style>
