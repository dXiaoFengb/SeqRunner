<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { logLabel } from './logLabels'
const props = defineProps<{ logs: string[] }>()
const list = ref<HTMLElement | null>(null)
const following = ref(true)
function onScroll() { const el = list.value; if (el) following.value = el.scrollHeight - el.scrollTop - el.clientHeight <= 8 }
function toBottom() { const el = list.value; if (el) { el.scrollTop = el.scrollHeight; following.value = true } }
watch(() => props.logs, async () => { if (following.value) { await nextTick(); toBottom() } }, { flush: 'pre' })
</script>
<template>
  <section class="side-live-log" aria-label="本轮日志摘要">
    <div class="side-live-header"><b>本轮日志摘要</b><button v-if="!following" type="button" @click="toBottom">回到底部</button></div>
    <div ref="list" class="side-live-list" tabindex="0" @scroll="onScroll"><span v-if="!logs.length" class="side-live-empty">暂无实时日志</span><ol v-else><li v-for="(line, index) in logs" :key="index">{{ logLabel(line) }}</li></ol></div>
  </section>
</template>
