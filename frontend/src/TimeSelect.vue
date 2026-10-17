<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'

const props = defineProps<{ value: string; options: string[]; unit: string; label: string; disabled: boolean }>()
const emit = defineEmits<{ change: [string] }>()
const trigger = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const open = ref(false)
const active = ref(0)
const position = ref({ left: 0, top: 0, width: 0, maxHeight: 0 })
const id = `time-select-${useId()}`
function updatePosition() {
  const rect = trigger.value?.getBoundingClientRect()
  if (!rect) return
  const gap = 4, padding = 8, desired = 240
  const below = window.innerHeight - rect.bottom - padding - gap
  const above = rect.top - padding - gap
  const abovePreferred = below < desired && above > below
  const maxHeight = Math.max(0, Math.min(desired, abovePreferred ? above : below))
  const width = Math.min(rect.width, window.innerWidth - padding * 2)
  position.value = { left: Math.max(padding, Math.min(rect.left, window.innerWidth - width - padding)), top: abovePreferred ? rect.top - gap - maxHeight : rect.bottom + gap, width, maxHeight }
}
function reveal() { void nextTick(() => menu.value?.querySelector<HTMLElement>(`[data-index="${active.value}"]`)?.scrollIntoView?.({ block: 'nearest' })) }
function show() { if (props.disabled) return; active.value = Math.max(0, props.options.indexOf(props.value)); updatePosition(); open.value = true; reveal() }
function close(restore = false) { open.value = false; if (restore && !props.disabled) void nextTick(() => trigger.value?.focus()) }
function choose(index: number) { if (props.disabled) return; const value = props.options[index]; if (value !== props.value) emit('change', value); close(true) }
function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Tab') { close(); return }
  if (event.key === 'Escape' && open.value) { event.preventDefault(); event.stopPropagation(); close(true); return }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) show()
    else { active.value = Math.max(0, Math.min(props.options.length - 1, active.value + (event.key === 'ArrowDown' ? 1 : -1))); reveal() }
  } else if (open.value && (event.key === 'Home' || event.key === 'End')) { event.preventDefault(); active.value = event.key === 'Home' ? 0 : props.options.length - 1; reveal() }
  else if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); if (open.value) choose(active.value); else show() }
}
function outside(event: PointerEvent) { const target = event.target; if (target instanceof Node && !trigger.value?.contains(target) && !menu.value?.contains(target)) close() }
function reposition() { if (open.value) updatePosition() }
watch(() => props.disabled, (disabled) => { if (disabled) close() })
onMounted(() => { document.addEventListener('pointerdown', outside); window.addEventListener('resize', reposition); window.addEventListener('scroll', reposition, true) })
onBeforeUnmount(() => { document.removeEventListener('pointerdown', outside); window.removeEventListener('resize', reposition); window.removeEventListener('scroll', reposition, true) })
</script>

<template>
  <button ref="trigger" class="time-trigger" type="button" role="combobox" :aria-label="label" aria-haspopup="listbox" :aria-expanded="open" :aria-controls="open ? id : undefined" :aria-activedescendant="open ? `${id}-${active}` : undefined" :disabled="disabled" @click="open ? close() : show()" @keydown="onKeydown" @blur="close()"><span>{{ value }} {{ unit }}</span><span aria-hidden="true">⌄</span></button>
  <Teleport to="body">
    <div v-if="open" :id="id" ref="menu" class="time-menu" role="listbox" :aria-label="label" :style="{ left: `${position.left}px`, top: `${position.top}px`, width: `${position.width}px`, maxHeight: `${position.maxHeight}px` }" @pointerdown.prevent>
      <div v-for="(option, index) in options" :id="`${id}-${index}`" :key="option" :data-index="index" role="option" :aria-selected="option === value" :class="{ active: index === active, selected: option === value }" @pointermove="active = index" @click="choose(index)">{{ option }} {{ unit }}</div>
    </div>
  </Teleport>
</template>

<style scoped>
.time-trigger{display:flex;align-items:center;justify-content:space-between;gap:8px;min-width:92px;min-height:37px;padding:8px 10px;border:1px solid var(--line-strong);border-radius:6px;background:var(--panel-deep);color:var(--ink);text-align:left}
.time-trigger:hover:not(:disabled){border-color:#60646b;background:#131417}.time-trigger:focus-visible{border-color:var(--teal);outline:2px solid rgba(22,119,255,.3)}.time-trigger:disabled{opacity:.5;cursor:not-allowed}
.time-menu{position:fixed;z-index:1000;box-sizing:border-box;overflow:auto;padding:4px;border:1px solid #484b51;border-radius:7px;background:#202124;box-shadow:0 8px 24px #0008;color:#e1e4e8}
.time-menu [role='option']{min-height:34px;box-sizing:border-box;padding:7px 9px;border-radius:4px;cursor:pointer}.time-menu [role='option'].selected{color:#cce7ff}.time-menu [role='option'].active{background:#17324a;color:#eef7ff}
</style>
