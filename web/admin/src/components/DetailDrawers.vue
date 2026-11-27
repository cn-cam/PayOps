<script setup lang="ts">
import { ref } from 'vue'
import OrderDetailDrawer from './OrderDetailDrawer.vue'
import SubscriptionDetailDrawer from './SubscriptionDetailDrawer.vue'

/**
 * 订单详情 + 订阅详情两个抽屉的组合：订单里点订阅号、订阅里点订单号可以互相跳转。
 * 页面通过 ref 调用 openOrder / openSubscription，同一时间只显示其中一个。
 */
const emit = defineEmits<{
  /** 订单或订阅发生了变更，列表页可据此刷新 */
  changed: [target: { kind: 'order' | 'subscription'; no: string }]
}>()

const orderVisible = ref(false)
const orderNo = ref('')
const subVisible = ref(false)
const subNo = ref('')

function openOrder(no: string | null | undefined) {
  if (!no) return
  subVisible.value = false
  orderNo.value = no
  orderVisible.value = true
}

function openSubscription(no: string | null | undefined) {
  if (!no) return
  orderVisible.value = false
  subNo.value = no
  subVisible.value = true
}

defineExpose({ openOrder, openSubscription })
</script>

<template>
  <OrderDetailDrawer
    v-model="orderVisible"
    :order-no="orderNo"
    @changed="emit('changed', { kind: 'order', no: orderNo })"
    @open-subscription="openSubscription"
  />
  <SubscriptionDetailDrawer
    v-model="subVisible"
    :sub-no="subNo"
    @changed="emit('changed', { kind: 'subscription', no: subNo })"
    @open-order="openOrder"
  />
</template>
