<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'

const props = defineProps<{ enabled: boolean; disabled: boolean }>()
const emit = defineEmits<{ change: [boolean] }>()
const trigger = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const open = ref(false)
const active = ref(0)
const position = ref({ left: 0, top: 0, width: 112, maxHeight: 180 })
const id = useId()
const menuId = `slot-status-${id}`
const optionId = (index: number) => `${menuId}-option-${index}`
const options = [{ label: '启用', value: true }, { label: '关闭', value: false }]
const activeId = computed(() => optionId(active.value))

function updatePosition() {
  const rect = trigger.value?.getBoundingClientRect()
  if (!rect) return
  const gap = 4
  const padding = 8
  const desiredHeight = 88
  const below = window.innerHeight - rect.bottom - padding - gap
  const above = rect.top - padding - gap
  const placeAbove = below < desiredHeight && above > below
  const maxHeight = Math.max(0, Math.min(desiredHeight, placeAbove ? above : below))
  const width = Math.min(rect.width, window.innerWidth - padding * 2)
  position.value = {
    left: Math.max(padding, Math.min(rect.left, window.innerWidth - width - padding)),
    top: placeAbove ? rect.top - gap - maxHeight : rect.bottom + gap,
    width,
    maxHeight,
  }
}
function close(restoreFocus = false) {
  open.value = false
  if (restoreFocus && !props.disabled) void nextTick(() => trigger.value?.focus())
}
function show() {
  if (props.disabled) return
  active.value = props.enabled ? 0 : 1
  updatePosition()
  open.value = true
}
function toggle() { if (open.value) close(); else show() }
function choose(index: number) {
  if (props.disabled) return
  const value = options[index].value
  if (value !== props.enabled) emit('change', value)
  close(true)
}
function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Tab') { close(); return }
  if (event.key === 'Escape' && open.value) { event.preventDefault(); event.stopPropagation(); close(true); return }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) show()
    else active.value = event.key === 'ArrowDown' ? Math.min(active.value + 1, 1) : Math.max(active.value - 1, 0)
  } else if (open.value && (event.key === 'Home' || event.key === 'End')) {
    event.preventDefault()
    active.value = event.key === 'Home' ? 0 : 1
  } else if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    if (open.value) choose(active.value); else show()
  }
}
function onPointerDown(event: PointerEvent) {
  const target = event.target
  if (target instanceof Node && !trigger.value?.contains(target) && !menu.value?.contains(target)) close()
}
function onViewportChange() { if (open.value) updatePosition() }
watch(() => props.disabled, (disabled) => { if (disabled) close() })
onMounted(() => {
  document.addEventListener('pointerdown', onPointerDown)
  window.addEventListener('resize', onViewportChange)
  window.addEventListener('scroll', onViewportChange, true)
})
onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onPointerDown)
  window.removeEventListener('resize', onViewportChange)
  window.removeEventListener('scroll', onViewportChange, true)
})
</script>

<template>
  <button ref="trigger" class="status-trigger" type="button" role="combobox" aria-label="定时状态" aria-haspopup="listbox" :aria-expanded="open" :aria-controls="open ? menuId : undefined" :aria-activedescendant="open ? activeId : undefined" :disabled="disabled" @click="toggle" @keydown="onKeydown" @blur="close()">
    <span>{{ enabled ? '启用' : '关闭' }}</span><span class="status-arrow" aria-hidden="true">⌄</span>
  </button>
  <Teleport to="body">
    <div v-if="open" :id="menuId" ref="menu" class="status-menu" role="listbox" aria-label="定时状态" :style="{ left: `${position.left}px`, top: `${position.top}px`, width: `${position.width}px`, maxHeight: `${position.maxHeight}px` }" @pointerdown.prevent>
      <div v-for="(option, index) in options" :id="optionId(index)" :key="option.label" role="option" :aria-selected="option.value === enabled" :class="{ active: index === active, selected: option.value === enabled }" @pointermove="active = index" @click="choose(index)">{{ option.label }}</div>
    </div>
  </Teleport>
</template>

<style scoped>
.status-trigger{display:flex;align-items:center;justify-content:space-between;gap:8px;width:100%;min-width:0;min-height:37px;padding:8px 10px;border:1px solid var(--line-strong);border-radius:6px;background:var(--panel-deep);color:var(--ink);text-align:left}
.status-trigger:hover:not(:disabled){border-color:#60646b;background:#131417}
.status-trigger:focus-visible{border-color:var(--teal);outline:2px solid rgba(22,119,255,.3);outline-offset:1px}
.status-trigger:disabled{opacity:.5;cursor:not-allowed}
.status-arrow{color:#a6adb5;font-size:15px;line-height:1}
.status-menu{position:fixed;z-index:1000;box-sizing:border-box;overflow:auto;padding:4px;border:1px solid #484b51;border-radius:7px;background:#202124;box-shadow:inset 0 1px rgba(255,255,255,.06),0 8px 24px rgba(0,0,0,.5);color:#e1e4e8}
.status-menu [role='option']{min-height:34px;box-sizing:border-box;padding:7px 9px;border-radius:4px;cursor:pointer}
.status-menu [role='option'].selected{color:#cce7ff}
.status-menu [role='option'].active{background:#17324a;color:#eef7ff}
</style>
