<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

const props = defineProps<{ label: string; content: string }>()
const root = ref<HTMLElement | null>(null)
const open = ref(false)
const hovering = ref(false)
const focused = ref(false)
const tooltipId = `help-tooltip-${Math.random().toString(36).slice(2, 10)}`

function show() { open.value = true }
function close() { if (!hovering.value && !focused.value) open.value = false }
function leave() { hovering.value = false; close() }
function blur() { focused.value = false; close() }
function escape() { hovering.value = false; focused.value = false; open.value = false }
function outside(event: PointerEvent) { if (root.value && event.target instanceof Node && !root.value.contains(event.target)) escape() }

onMounted(() => document.addEventListener('pointerdown', outside))
onBeforeUnmount(() => document.removeEventListener('pointerdown', outside))
</script>

<template>
  <span ref="root" class="help-tooltip" @pointerenter="hovering = true; show()" @pointerleave="leave">
    <button type="button" class="help-tooltip-trigger" :aria-label="`查看${props.label}说明`" :aria-expanded="open" :aria-describedby="open ? tooltipId : undefined" @focus="focused = true; show()" @blur="blur" @click="show" @keydown.esc.prevent="escape">?</button>
    <span v-if="open" :id="tooltipId" role="tooltip" class="help-tooltip-panel">{{ props.content }}</span>
  </span>
</template>

<style scoped>
.help-tooltip{position:relative;display:inline-flex;vertical-align:middle}.help-tooltip-trigger{display:inline-grid;place-items:center;width:17px;height:17px;padding:0;border:1px solid #5f8194;border-radius:50%;background:#102333;color:#c3d9e5;font-size:12px;font-weight:700;line-height:1;cursor:help}.help-tooltip-trigger:hover,.help-tooltip-trigger:focus-visible{border-color:#5bddda;background:#153641;color:#e7ffff;outline:2px solid rgba(91,221,218,.25);outline-offset:2px}.help-tooltip-panel{position:absolute;z-index:20;top:calc(100% + 8px);left:50%;width:min(270px,calc(100vw - 36px));padding:9px 11px;border:1px solid #4a5862;border-radius:5px;background:#3a3d40;color:#f0f4f6;box-shadow:0 10px 24px #0009;font-size:13px;font-weight:500;line-height:1.55;transform:translateX(-15%);white-space:normal}.help-tooltip-panel::before{position:absolute;top:-5px;left:18%;width:9px;height:9px;border-top:1px solid #4a5862;border-left:1px solid #4a5862;background:#3a3d40;content:'';transform:rotate(45deg)}@media(max-width:620px){.help-tooltip-panel{left:auto;right:0;transform:none}.help-tooltip-panel::before{left:auto;right:9px}}
</style>
