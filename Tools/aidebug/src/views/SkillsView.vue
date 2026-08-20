<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useAgent } from '../stores/agent'
import { useSettings } from '../stores/settings'
import { postCommand } from '../api/client'
import type { AgentCommandResult } from '../api/types'

/** Жёсткий предел на сервере: только `when` попадает в зону 0, и бюджет есть только у него. */
const WHEN_LIMIT = 60

const agent = useAgent()
const settings = useSettings()

const selected = ref<string | null>(null)
const when = ref('')
const body = ref('')
const busy = ref(false)
const result = ref<AgentCommandResult | null>(null)
const error = ref('')

const current = computed(() => agent.globals.skills.find((s) => s.name === selected.value) ?? null)

watch(current, (skill) => {
  when.value = skill?.when ?? ''
  body.value = skill?.body ?? ''
})

const dirty = computed(
  () => current.value !== null && (when.value !== current.value.when || body.value !== current.value.body),
)

async function save(): Promise<void> {
  if (busy.value || !current.value) return

  busy.value = true
  error.value = ''
  result.value = null

  try {
    // Целиком, а не фрагментом: редактор показывает всё тело, значит и отправляет всё.
    result.value = await postCommand(
      { baseUrl: settings.baseUrl, token: settings.token },
      { type: 'skill.change', name: current.value.name, when: when.value, body: body.value },
    )
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="skills">
    <aside>
      <p v-if="!agent.globals.skills.length" class="dim">Библиотека пуста.</p>
      <button
        v-for="skill in agent.globals.skills"
        :key="skill.name"
        class="item"
        :class="{ active: skill.name === selected }"
        @click="selected = skill.name"
      >
        <span class="name mono">{{ skill.name }}</span>
        <span class="when">{{ skill.when }}</span>
      </button>
    </aside>

    <section v-if="current">
      <label>
        когда
        <span class="counter" :class="{ over: when.length > WHEN_LIMIT }">
          {{ when.length }} / {{ WHEN_LIMIT }}
        </span>
      </label>
      <input v-model="when" spellcheck="false" />
      <p class="hint">
        Единственная часть скилла, попадающая в зону 0. По ней модель решает, открывать ли тело
        через <code>skill_view</code>, — поэтому у неё жёсткий лимит, а у тела нет.
      </p>

      <label>тело</label>
      <textarea v-model="body" rows="18" spellcheck="false" class="mono" />

      <div class="actions">
        <button :disabled="busy || !dirty" @click="save()">
          {{ busy ? 'пишу…' : 'Сохранить' }}
        </button>
        <span v-if="error" class="bad">{{ error }}</span>
        <span v-else-if="result?.ok" class="ok">
          {{ result.message }} · модель увидит: {{ result.visible_to_model }}
        </span>
      </div>
    </section>

    <section v-else class="dim pick">Выберите скилл слева.</section>
  </div>
</template>

<style scoped>
.skills {
  display: grid;
  grid-template-columns: 260px 1fr;
  gap: 20px;
  align-items: start;
}

@media (max-width: 900px) {
  .skills {
    grid-template-columns: 1fr;
  }
}

aside {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  text-align: left;
  background: none;
  border: 1px solid transparent;
}

.item.active {
  background: var(--panel);
  border-color: var(--line);
}

.name {
  color: var(--text);
}

.when {
  color: var(--dim);
  font-size: 11.5px;
}

label {
  display: flex;
  justify-content: space-between;
  color: var(--dim);
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin: 10px 0 3px;
}

.counter.over {
  color: var(--bad);
}

input,
textarea {
  width: 100%;
}

.hint {
  color: var(--dim);
  font-size: 12px;
  max-width: 70ch;
  margin: 5px 0 0;
}

.actions {
  display: flex;
  gap: 12px;
  align-items: center;
  margin-top: 10px;
}

.bad {
  color: var(--bad);
}

.ok {
  color: var(--assistant);
}

.dim {
  color: var(--dim);
}

.pick {
  padding-top: 20px;
}
</style>
