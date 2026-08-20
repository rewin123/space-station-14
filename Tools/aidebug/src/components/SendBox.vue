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
 * Отправка сообщения агенту. Single-flight, и НИКОГДА не повторяется автоматически.
 *
 * `AgentInbox.Enqueue` на сервере склеивает два сообщения через перевод строки, а не отвергает
 * второе, и ключа идемпотентности на проводе нет. Значит второй клик — или один автоматический
 * повтор запроса, который на самом деле дошёл, — даёт одно сообщение с текстом дважды.
 *
 * Отсюда же индикатор ожидания: между отправкой и появлением сообщения в ленте проходит до целого
 * тика (8-25 секунд). Без обратной связи оператор решит, что не отправилось, и нажмёт снова.
 */
async function send(): Promise<void> {
  if (busy.value || !text.value.trim()) return

  busy.value = true
  bad.value = false
  note.value = ''

  const endpoint = { baseUrl: settings.baseUrl, token: settings.token }

  try {
    // Адресат обязателен: сообщение уходит в ящик конкретного мозга. Без него сервер отвечает
    // 400 — и это правильно, «кому-нибудь» тут быть не должно.
    const result = await postCommand(endpoint, {
      type: 'message.send',
      agent: agent.selected ?? '',
      text: text.value,
    })
    note.value = `${result.message} (${result.applied})`
    text.value = ''

    // Сразу спрашиваем, встало ли оно в очередь: это единственный способ показать, что уехало.
    try {
      const health = await getHealth(endpoint)
      pending.value = health.agents.some((a) => a.id === agent.selected && a.pending_input)
    } catch {
      // Здоровье не критично: сообщение уже принято.
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
