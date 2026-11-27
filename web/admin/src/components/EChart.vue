<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import type { EChartsType } from 'echarts/core'
import { echarts, type ECOption } from '@/utils/echarts'

const props = withDefaults(
  defineProps<{
    option: ECOption
    height?: string
  }>(),
  { height: '320px' },
)

const el = ref<HTMLDivElement>()
const chart = shallowRef<EChartsType>()
let observer: ResizeObserver | undefined

onMounted(() => {
  if (!el.value) return
  chart.value = echarts.init(el.value)
  chart.value.setOption(props.option, true)
  observer = new ResizeObserver(() => chart.value?.resize())
  observer.observe(el.value)
})

watch(
  () => props.option,
  (option) => chart.value?.setOption(option, true),
)

onBeforeUnmount(() => {
  observer?.disconnect()
  chart.value?.dispose()
  chart.value = undefined
})
</script>

<template>
  <div ref="el" class="echart" :style="{ height }" />
</template>

<style scoped>
.echart {
  width: 100%;
}
</style>
