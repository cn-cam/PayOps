<script setup lang="ts">
import { computed } from 'vue'
import { fenToYuan, yuanToFen } from '@/utils/money'

/** 金额输入：界面输入「元」（两位小数），v-model 绑定的是「分」（整数） */
const props = withDefaults(
  defineProps<{
    modelValue: number
    /** 上限（分） */
    max?: number
    /** 下限（分） */
    min?: number
    placeholder?: string
    disabled?: boolean
    width?: string
  }>(),
  { min: 0, max: 100_000_000_00, placeholder: '0.00', disabled: false, width: '180px' },
)

const emit = defineEmits<{
  'update:modelValue': [value: number]
  change: [value: number]
}>()

const yuan = computed(() => fenToYuan(props.modelValue))

function onInput(value: number | null | undefined) {
  const fen = yuanToFen(value ?? 0)
  emit('update:modelValue', fen)
  emit('change', fen)
}
</script>

<template>
  <div class="money-input">
    <el-input-number
      :model-value="yuan"
      :min="fenToYuan(min)"
      :max="fenToYuan(max)"
      :precision="2"
      :step="1"
      :controls="false"
      :disabled="disabled"
      :placeholder="placeholder"
      :style="{ width }"
      class="money-input__inner"
      @update:model-value="onInput"
    />
    <span class="money-input__unit">元</span>
  </div>
</template>

<style scoped>
.money-input {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}
.money-input__inner :deep(.el-input__inner) {
  text-align: left;
}
.money-input__unit {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
</style>
