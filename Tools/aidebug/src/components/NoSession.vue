<script setup lang="ts">
import { useAgent } from '../stores/agent'

/**
 * Nothing to show — and there are four different reasons for that, not one.
 *
 * Between rounds, bodies are unoccupied; the selected agent might not be loaded yet (histories
 * for all brains aren't downloaded at once, deliberately); a snapshot might be in flight; the
 * agent might have left. All four are normal states, and they need to be distinguished: "not
 * loaded" is fixed by a click, "left" isn't.
 *
 * In any of these cases, memory and skills keep working: they belong to the process, not to the
 * agent.
 */
const agent = useAgent()
</script>

<template>
  <div class="none">
    <p v-if="agent.status === 'idle'">Не подключено.</p>
    <p v-else-if="agent.loading">Снимок агента в пути…</p>
    <p v-else-if="agent.selected && agent.sliceState(agent.selected) === 'ended'">
      Агент «{{ agent.selected }}» ушёл. Разговор остался, новые кадры к нему не применяются.
    </p>
    <p v-else-if="agent.selected && agent.sliceState(agent.selected) === 'absent'">
      Агент «{{ agent.selected }}» не загружен — выбери его в шапке.
    </p>
    <template v-else>
      <p>Тела никем не заняты.</p>
      <p class="dim">
        Между раундами это нормально. Память и скиллы на своих вкладках работают: они принадлежат
        процессу, а не агенту, и переживают раунд.
      </p>
    </template>
  </div>
</template>

<style scoped>
.none {
  color: var(--text);
  padding: 20px 0;
}

.dim {
  color: var(--dim);
  max-width: 60ch;
}
</style>
