<script setup lang="ts">
import { computed } from 'vue'
import type { EnumOption } from '@/utils/enums'

const props = withDefaults(
  defineProps<{
    options: readonly EnumOption[]
    value: unknown
    effect?: 'light' | 'dark' | 'plain'
    size?: 'large' | 'default' | 'small'
  }>(),
  { effect: 'light', size: 'default' },
)

const matched = computed(() => props.options.find((o) => o.value === props.value))
const text = computed(() => {
  if (matched.value) return matched.value.label
  return props.value === null || props.value === undefined ? '-' : String(props.value)
})
</script>

<template>
  <el-tag :type="matched?.type ?? 'info'" :effect="effect" :size="size" disable-transitions>
    {{ text }}
  </el-tag>
</template>
