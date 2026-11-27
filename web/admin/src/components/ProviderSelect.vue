<script setup lang="ts">
import { onMounted } from 'vue'
import { useMetaStore } from '@/stores/meta'

withDefaults(
  defineProps<{
    modelValue: string | undefined
    placeholder?: string
    clearable?: boolean
    disabled?: boolean
    width?: string
  }>(),
  { placeholder: '全部支付类型', clearable: true, disabled: false, width: '180px' },
)

const emit = defineEmits<{
  'update:modelValue': [value: string | undefined]
  change: [value: string | undefined]
}>()

const meta = useMetaStore()

onMounted(() => meta.loadProviders())

function onChange(value: string | undefined | null) {
  const v = value ? String(value) : undefined
  emit('update:modelValue', v)
  emit('change', v)
}
</script>

<template>
  <el-select
    :model-value="modelValue"
    :placeholder="placeholder"
    :clearable="clearable"
    :disabled="disabled"
    filterable
    :style="{ width }"
    @update:model-value="onChange"
  >
    <el-option v-for="p in meta.providers" :key="p.code" :value="p.code" :label="p.name">
      <span>{{ p.name }}</span>
      <span class="option-sub">{{ p.code }}</span>
    </el-option>
  </el-select>
</template>

<style scoped>
.option-sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
