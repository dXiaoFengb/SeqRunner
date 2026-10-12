<script setup lang="ts">
defineProps<{ state: string; detail?: string | null }>()
</script>
<template><span class="task-status" :class="`status-${state}`">{{ detail || state }}</span></template>
