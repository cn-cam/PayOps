<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { Aim } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { bindingsApi } from '@/api/bindings'
import type { BindingItem, RouteSimulateResult } from '@/api/types'
import { useMetaStore } from '@/stores/meta'
import MoneyInput from '@/components/MoneyInput.vue'
import { formatCount, formatRate } from '@/utils/common'
import { formatMoney } from '@/utils/money'

const props = defineProps<{
  channelId: number
  bindings: BindingItem[]
}>()

const meta = useMetaStore()

const payMethod = ref('')
const amount = ref(100)
const times = ref(1000)
const loading = ref(false)
const result = ref<RouteSimulateResult | null>(null)
/** 模拟之后绑定配置又发生了变化 */
const stale = ref(false)

/** 可选支付方式：当前渠道已绑定的支付类型所支持的全部方式，按支付类型分组 */
const methodGroups = computed(() => {
  const providers = [...new Set(props.bindings.map((b) => b.provider))]
  return providers
    .map((code) => {
      const p = meta.getProvider(code)
      return {
        provider: code,
        providerName: p?.name ?? code,
        methods: p?.methods ?? [],
      }
    })
    .filter((g) => g.methods.length > 0)
})

watch(
  methodGroups,
  (groups) => {
    const all = groups.flatMap((g) => g.methods.map((m) => m.code))
    if (!all.includes(payMethod.value)) payMethod.value = all[0] ?? ''
  },
  { immediate: true },
)

watch(
  () => props.bindings,
  () => {
    if (result.value) stale.value = true
  },
)

async function run() {
  if (!payMethod.value) {
    ElMessage.warning('请选择支付方式')
    return
  }
  if (amount.value <= 0) {
    ElMessage.warning('请输入大于 0 的订单金额')
    return
  }
  loading.value = true
  try {
    result.value = await bindingsApi.simulate(props.channelId, {
      payMethod: payMethod.value,
      amount: amount.value,
      times: times.value || 1000,
    })
    stale.value = false
  } finally {
    loading.value = false
  }
}

const eligibleCount = computed(() => result.value?.items.filter((i) => i.eligible).length ?? 0)
const maxRate = computed(() => Math.max(0, ...(result.value?.items.map((i) => i.hitRate) ?? [])))

const quickAmounts = [100, 1000, 5000, 10000, 50000, 100000]
</script>

<template>
  <el-card shadow="never" class="simulate">
    <div class="toolbar">
      <div class="toolbar__title">
        路由模拟
        <span class="toolbar__sub">按当前配置模拟 N 次下单，验证权重分流、起付金额、限额、时段等规则是否符合预期（不会产生真实订单）</span>
      </div>
    </div>

    <el-empty v-if="bindings.length === 0" description="请先添加通道" :image-size="70" />

    <template v-else>
      <el-form inline class="simulate__form" @submit.prevent="run">
        <el-form-item label="支付方式">
          <el-select v-model="payMethod" placeholder="请选择" filterable style="width: 240px">
            <el-option-group v-for="g in methodGroups" :key="g.provider" :label="g.providerName">
              <el-option v-for="m in g.methods" :key="m.code" :value="m.code" :label="m.name">
                <span>{{ m.name }}</span>
                <span class="option-sub">{{ m.code }}</span>
              </el-option>
            </el-option-group>
          </el-select>
        </el-form-item>
        <el-form-item label="订单金额">
          <MoneyInput v-model="amount" width="140px" />
        </el-form-item>
        <el-form-item label="模拟次数">
          <el-input-number
            v-model="times"
            :min="1"
            :max="100000"
            :step="1000"
            :precision="0"
            controls-position="right"
            style="width: 150px"
          />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Aim" :loading="loading" @click="run">开始模拟</el-button>
        </el-form-item>
      </el-form>
      <div class="simulate__quick">
        <span class="text-secondary text-small">常用金额：</span>
        <el-button v-for="a in quickAmounts" :key="a" size="small" round @click="amount = a">
          {{ formatMoney(a) }}
        </el-button>
      </div>

      <template v-if="result">
        <el-alert
          v-if="stale"
          type="warning"
          show-icon
          :closable="false"
          title="通道配置已变更，下面是变更前的模拟结果，请重新模拟。"
          class="simulate__alert"
        />
        <el-alert
          v-else-if="eligibleCount === 0"
          type="error"
          show-icon
          :closable="false"
          title="没有任何通道满足条件：这样的订单会下单失败，请检查支付方式、起付金额 / 单笔上限、限额、时段与熔断状态。"
          class="simulate__alert"
        />
        <div class="simulate__summary">
          支付方式 <b>{{ meta.methodName(result.payMethod) }}</b>
          <span class="mono text-secondary">（{{ result.payMethod }}）</span>
          ，订单金额 <b>{{ formatMoney(result.amount) }}</b>，模拟 <b>{{ formatCount(result.times) }}</b> 次，可用通道
          <b>{{ eligibleCount }}</b> / {{ result.items.length }}
        </div>

        <el-table :data="result.items" row-key="bindingId" size="small" :row-class-name="({ row }) => (row.eligible ? '' : 'row--muted')">
          <el-table-column label="通道" min-width="180" show-overflow-tooltip>
            <template #default="{ row }">
              <span class="passage-name">{{ row.passageName }}</span>
            </template>
          </el-table-column>
          <el-table-column label="支付类型" width="120">
            <template #default="{ row }">{{ meta.providerName(row.provider) }}</template>
          </el-table-column>
          <el-table-column label="优先级" prop="priority" width="80" align="center" />
          <el-table-column label="权重" prop="weight" width="80" align="center" />
          <el-table-column label="是否可用" width="100" align="center">
            <template #default="{ row }">
              <el-tag :type="row.eligible ? 'success' : 'info'" effect="light">
                {{ row.eligible ? '可用' : '不可用' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="不可用原因" min-width="200" show-overflow-tooltip>
            <template #default="{ row }">
              <span v-if="!row.eligible" class="text-danger">{{ row.reason || '不满足路由条件' }}</span>
              <span v-else class="text-secondary">-</span>
            </template>
          </el-table-column>
          <el-table-column label="命中次数" width="110" align="right">
            <template #default="{ row }">{{ formatCount(row.hitCount) }}</template>
          </el-table-column>
          <el-table-column label="命中率" min-width="240">
            <template #default="{ row }">
              <div class="rate-bar">
                <div class="rate-bar__track">
                  <div
                    class="rate-bar__fill"
                    :class="{ 'rate-bar__fill--top': row.hitRate > 0 && row.hitRate === maxRate }"
                    :style="{ width: `${Math.min(100, Math.max(0, row.hitRate * 100))}%` }"
                  />
                </div>
                <span class="rate-bar__text">{{ formatRate(row.hitRate, 2) }}</span>
              </div>
            </template>
          </el-table-column>
        </el-table>
        <div class="form-help">
          命中次数 = 该通道被选为首选通道的次数。权重为 0 的备用通道、低优先级组的通道在有更优通道可用时命中为 0 属于正常现象。
        </div>
      </template>
    </template>
  </el-card>
</template>

<style scoped>
.simulate__form :deep(.el-form-item) {
  margin-bottom: 10px;
  margin-right: 20px;
}
.simulate__quick {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  margin-bottom: 14px;
}
.simulate__quick .el-button + .el-button {
  margin-left: 0;
}
.simulate__alert {
  margin-bottom: 12px;
}
.simulate__summary {
  margin-bottom: 10px;
  padding: 10px 14px;
  border-radius: 6px;
  background: #f8fafc;
  color: #475569;
  font-size: 13px;
}
.simulate__summary b {
  color: var(--el-text-color-primary);
}
.option-sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.passage-name {
  font-weight: 600;
}
.rate-bar {
  display: grid;
  grid-template-columns: 1fr 64px;
  align-items: center;
  gap: 10px;
}
.rate-bar__track {
  height: 10px;
  border-radius: 5px;
  background: #eef2f7;
  overflow: hidden;
}
.rate-bar__fill {
  height: 100%;
  border-radius: 5px;
  background: #93b4f5;
  transition: width 0.3s;
}
.rate-bar__fill--top {
  background: var(--el-color-primary);
}
.rate-bar__text {
  font-variant-numeric: tabular-nums;
  text-align: right;
}
.simulate :deep(.row--muted) {
  color: var(--el-text-color-secondary);
  background: #fafafa;
}
</style>
