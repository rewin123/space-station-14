<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useAgent } from './stores/agent'
import { useSettings } from './stores/settings'
import BusView from './views/BusView.vue'
import ConversationView from './views/ConversationView.vue'

const settings = useSettings()
const agent = useAgent()

const TABS = [
  { id: 'conversation', title: 'Разговор' },
  { id: 'bus', title: 'Шина' },
] as const

type TabId = (typeof TABS)[number]['id']
const tab = ref<TabId>('conversation')

// Автоподключение только если токен уже есть: иначе первый запрос словит 401, петля терминально
// встанет, и будет непонятно, чего от тебя хотят.
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

      <input v-model="settings.baseUrl" size="22" spellcheck="false" />
      <input v-model="settings.token" type="password" placeholder="ai.debug_token" size="18" />

      <button @click="agent.connect()">Подключиться</button>
      <button :disabled="agent.status === 'idle'" @click="agent.disconnect()">Отключиться</button>

      <span class="status" :class="agent.status">
        {{ STATUS_TEXT[agent.status] ?? agent.status }}
        <template v-if="agent.statusDetail">— {{ agent.statusDetail }}</template>
      </span>

      <span class="cursor mono">
        <template v-if="agent.state.sessionId">
          раунд {{ agent.state.round }} ·
        </template>
        seq {{ agent.state.seq }} · {{ agent.state.instance || '—' }}
      </span>
    </header>

    <nav>
      <button
        v-for="t in TABS"
        :key="t.id"
        class="tab"
        :class="{ active: tab === t.id }"
        @click="tab = t.id"
      >
        {{ t.title }}
      </button>
    </nav>

    <main>
      <p v-if="agent.status === 'unauthorized'" class="hint bad">
        Сервер отверг токен. Он лежит в <code>ai_data/debug.token</code>, на сервере — в
        <code>ai.debug_token</code>. Повторять бессмысленно, петля остановлена.
      </p>
      <p v-else-if="agent.status === 'idle'" class="hint">
        Введите токен и нажмите «Подключиться».
      </p>

      <ConversationView v-show="tab === 'conversation'" />
      <BusView v-show="tab === 'bus'" />
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

nav {
  display: flex;
  gap: 2px;
  padding: 0 14px;
  background: var(--panel);
  border-bottom: 1px solid var(--line);
}

.tab {
  border: none;
  border-bottom: 2px solid transparent;
  border-radius: 0;
  background: none;
  color: var(--dim);
  padding: 7px 14px;
}

.tab.active {
  color: var(--text);
  border-bottom-color: var(--user);
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
</style>
