<script setup lang="ts">
import { onMounted } from 'vue'
import { useAgent } from './stores/agent'
import { useSettings } from './stores/settings'

const settings = useSettings()
const agent = useAgent()

// Автоподключение, только если токен уже есть — иначе первый же запрос словит 401 и петля
// терминально встанет, а пользователь не поймёт, что от него хотели.
onMounted(() => {
  if (settings.token)
    agent.connect()
})

const STATUS_TEXT: Record<string, string> = {
  idle: 'не подключено',
  connecting: 'снимок…',
  live: 'на связи',
  resyncing: 'пересинхронизация',
  retrying: 'повтор',
  unauthorized: 'неверный токен',
  broken: 'ошибка клиента',
}
</script>

<template>
  <div class="app">
    <header>
      <strong>Отладчик ИИ станции</strong>

      <input v-model="settings.baseUrl" size="24" spellcheck="false" />
      <input v-model="settings.token" type="password" placeholder="ai.debug_token" size="20" />

      <button @click="agent.connect()">Подключиться</button>
      <button :disabled="agent.status === 'idle'" @click="agent.disconnect()">Отключиться</button>

      <span class="status" :class="agent.status">
        {{ STATUS_TEXT[agent.status] ?? agent.status }}
        <template v-if="agent.statusDetail">— {{ agent.statusDetail }}</template>
      </span>

      <span class="cursor mono">
        seq {{ agent.state.seq }} · {{ agent.state.instance || '—' }}
      </span>
    </header>

    <main>
      <p v-if="agent.status === 'unauthorized'" class="hint bad">
        Сервер отверг токен. Он лежит в <code>ai_data/debug.token</code>, а на сервере — в
        <code>ai.debug_token</code>. Повторять запросы бессмысленно, петля остановлена.
      </p>
      <p v-else-if="agent.status === 'idle'" class="hint">
        Введите токен и нажмите «Подключиться».
      </p>

      <!-- Пока это весь UI: сырой дамп. Экраны приходят следующими коммитами, а машина состояний
           должна быть отлажена раньше, чем на неё повесят что-то красивое. -->
      <pre class="dump">{{ JSON.stringify(agent.state, null, 2) }}</pre>
    </main>
  </div>
</template>

<style scoped>
.app {
  display: flex;
  flex-direction: column;
  height: 100vh;
}

header {
  display: flex;
  gap: 10px;
  align-items: center;
  flex-wrap: wrap;
  padding: 10px 14px;
  background: var(--panel);
  border-bottom: 1px solid var(--line);
}

.status {
  color: var(--dim);
}

.status.live {
  color: var(--assistant);
}

.status.unauthorized,
.status.broken {
  color: var(--bad);
}

.status.retrying,
.status.resyncing {
  color: var(--tool);
}

.cursor {
  margin-left: auto;
  color: var(--dim);
}

main {
  flex: 1;
  overflow: auto;
  padding: 14px;
}

.hint {
  color: var(--dim);
}

.hint.bad {
  color: var(--bad);
}

.dump {
  white-space: pre-wrap;
  word-break: break-word;
  color: var(--dim);
}
</style>
