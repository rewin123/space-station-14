<script setup lang="ts">
import { useAgent } from '../stores/agent'

/**
 * Отсутствие сессии — штатное состояние, а не ошибка: между раундами ядро никем не занято, и
 * сервер честно отдаёт `session: null`. Разговор, промпт и статистика в этот момент пусты, а вот
 * память и скиллы продолжают работать — они процессные и переживают раунд.
 */
const agent = useAgent()
</script>

<template>
  <div class="none">
    <p v-if="agent.status === 'idle'">Не подключено.</p>
    <template v-else>
      <p>Ядро никем не занято.</p>
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
