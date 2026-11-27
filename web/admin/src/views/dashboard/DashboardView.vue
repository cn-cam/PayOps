<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { Bottom, CircleCheckFilled, CircleCloseFilled, Refresh, Top } from '@element-plus/icons-vue'
import { dashboardApi } from '@/api/dashboard'
import type {
  DashboardStat,
  DashboardSummary,
  DistributionDimension,
  DistributionItem,
  SystemInfo,
  TrendPoint,
} from '@/api/types'
import EChart from '@/components/EChart.vue'
import type { ECOption } from '@/utils/echarts'
import { formatCount, formatRate } from '@/utils/common'
import { formatMoney } from '@/utils/money'
import { formatTime, fromNow, uptime } from '@/utils/time'

const router = useRouter()

// ---------- 概览 ----------
const summary = ref<DashboardSummary | null>(null)
const summaryLoading = ref(false)

interface StatCard {
  key: string
  title: string
  value: string
  yesterday: string
  /** 相对昨日的变化文本，null 表示无法比较 */
  delta: string | null
  /** 1 上升 / -1 下降 / 0 持平 */
  trend: 1 | 0 | -1
  /** 上升是否为正向指标（退款金额上升不是好事） */
  upIsGood: boolean
}

function ratioDelta(today: number, yesterday: number): { delta: string | null; trend: 1 | 0 | -1 } {
  if (!yesterday) return { delta: null, trend: 0 }
  const diff = (today - yesterday) / yesterday
  return { delta: `${Math.abs(diff * 100).toFixed(1)}%`, trend: diff > 0 ? 1 : diff < 0 ? -1 : 0 }
}

const emptyStat: DashboardStat = { orderCount: 0, paidCount: 0, paidAmount: 0, refundAmount: 0, successRate: 0 }

const statCards = computed<StatCard[]>(() => {
  const t = summary.value?.today ?? emptyStat
  const y = summary.value?.yesterday ?? emptyStat
  const rateDiff = (t.successRate - y.successRate) * 100
  return [
    {
      key: 'orderCount',
      title: '今日订单数',
      value: formatCount(t.orderCount),
      yesterday: formatCount(y.orderCount),
      ...ratioDelta(t.orderCount, y.orderCount),
      upIsGood: true,
    },
    {
      key: 'paidCount',
      title: '今日成功笔数',
      value: formatCount(t.paidCount),
      yesterday: formatCount(y.paidCount),
      ...ratioDelta(t.paidCount, y.paidCount),
      upIsGood: true,
    },
    {
      key: 'paidAmount',
      title: '今日成功金额',
      value: formatMoney(t.paidAmount),
      yesterday: formatMoney(y.paidAmount),
      ...ratioDelta(t.paidAmount, y.paidAmount),
      upIsGood: true,
    },
    {
      key: 'successRate',
      title: '今日成功率',
      value: formatRate(t.successRate),
      yesterday: formatRate(y.successRate),
      delta: summary.value && (t.orderCount || y.orderCount) ? `${Math.abs(rateDiff).toFixed(1)} pt` : null,
      trend: rateDiff > 0.05 ? 1 : rateDiff < -0.05 ? -1 : 0,
      upIsGood: true,
    },
    {
      key: 'refundAmount',
      title: '今日退款金额',
      value: formatMoney(t.refundAmount),
      yesterday: formatMoney(y.refundAmount),
      ...ratioDelta(t.refundAmount, y.refundAmount),
      upIsGood: false,
    },
  ]
})

function deltaClass(card: StatCard): string {
  if (card.trend === 0) return 'delta--flat'
  const good = card.trend === 1 ? card.upIsGood : !card.upIsGood
  return good ? 'delta--good' : 'delta--bad'
}

async function loadSummary() {
  summaryLoading.value = true
  try {
    summary.value = await dashboardApi.summary()
  } finally {
    summaryLoading.value = false
  }
}

// ---------- 趋势 ----------
const trendDays = ref(7)
const trend = ref<TrendPoint[]>([])
const trendLoading = ref(false)

async function loadTrend() {
  trendLoading.value = true
  try {
    trend.value = (await dashboardApi.trend(trendDays.value)) ?? []
  } finally {
    trendLoading.value = false
  }
}

watch(trendDays, () => loadTrend())

const trendOption = computed<ECOption>(() => ({
  color: ['#2563eb', '#10b981', '#f59e0b'],
  tooltip: { trigger: 'axis' },
  legend: { data: ['订单数', '成功笔数', '成功金额'], top: 0 },
  grid: { left: 16, right: 16, top: 44, bottom: 8, containLabel: true },
  xAxis: {
    type: 'category',
    boundaryGap: false,
    data: trend.value.map((p) => p.date.slice(5)),
    axisLine: { lineStyle: { color: '#cbd5e1' } },
    axisLabel: { color: '#64748b' },
  },
  yAxis: [
    {
      type: 'value',
      name: '笔数',
      minInterval: 1,
      axisLabel: { color: '#64748b' },
      splitLine: { lineStyle: { color: '#eef2f7' } },
    },
    {
      type: 'value',
      name: '金额（元）',
      axisLabel: { color: '#64748b' },
      splitLine: { show: false },
    },
  ],
  series: [
    {
      name: '订单数',
      type: 'line',
      smooth: true,
      symbolSize: 6,
      data: trend.value.map((p) => p.orderCount),
    },
    {
      name: '成功笔数',
      type: 'line',
      smooth: true,
      symbolSize: 6,
      data: trend.value.map((p) => p.paidCount),
    },
    {
      name: '成功金额',
      type: 'line',
      smooth: true,
      symbolSize: 6,
      yAxisIndex: 1,
      areaStyle: { opacity: 0.08 },
      data: trend.value.map((p) => p.paidAmount / 100),
      tooltip: { valueFormatter: (value) => formatMoney(Math.round(Number(value) * 100)) },
    },
  ],
}))

// ---------- 分布 ----------
const dimension = ref<DistributionDimension>('channel')
const distDays = ref(1)
const distMetric = ref<'paidAmount' | 'orderCount'>('paidAmount')
const distChart = ref<'pie' | 'bar'>('pie')
const distribution = ref<DistributionItem[]>([])
const distLoading = ref(false)

const dimensionOptions: { value: DistributionDimension; label: string }[] = [
  { value: 'channel', label: '按渠道' },
  { value: 'passage', label: '按通道' },
  { value: 'provider', label: '按支付类型' },
]

const dimensionLabel = computed(
  () => ({ channel: '渠道', passage: '通道', provider: '支付类型' })[dimension.value],
)

async function loadDistribution() {
  distLoading.value = true
  try {
    distribution.value = (await dashboardApi.distribution(dimension.value, distDays.value)) ?? []
  } finally {
    distLoading.value = false
  }
}

watch([dimension, distDays], () => loadDistribution())

const distOption = computed<ECOption>(() => {
  const isAmount = distMetric.value === 'paidAmount'
  const metricName = isAmount ? '成功金额' : '订单数'
  const valueOf = (item: DistributionItem) => (isAmount ? item.paidAmount / 100 : item.orderCount)
  const valueFormatter = (value: unknown) =>
    isAmount ? formatMoney(Math.round(Number(value) * 100)) : `${formatCount(Number(value))} 笔`
  const rows = [...distribution.value].sort((a, b) => valueOf(b) - valueOf(a))

  if (distChart.value === 'bar') {
    return {
      color: ['#2563eb'],
      tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' }, valueFormatter },
      grid: { left: 12, right: 24, top: 16, bottom: 8, containLabel: true },
      xAxis: { type: 'value', axisLabel: { color: '#64748b' }, splitLine: { lineStyle: { color: '#eef2f7' } } },
      yAxis: {
        type: 'category',
        inverse: true,
        data: rows.map((r) => r.name || r.key),
        axisLabel: { color: '#475569', width: 110, overflow: 'truncate' },
      },
      series: [{ name: metricName, type: 'bar', barMaxWidth: 22, data: rows.map(valueOf) }],
    }
  }

  return {
    tooltip: { trigger: 'item', valueFormatter },
    legend: { type: 'scroll', bottom: 0 },
    series: [
      {
        name: metricName,
        type: 'pie',
        radius: ['42%', '68%'],
        center: ['50%', '45%'],
        avoidLabelOverlap: true,
        itemStyle: { borderColor: '#fff', borderWidth: 2 },
        label: { formatter: '{b}\n{d}%' },
        data: rows.map((r) => ({ name: r.name || r.key, value: valueOf(r) })),
      },
    ],
  }
})

// ---------- 集群 ----------
const system = ref<SystemInfo | null>(null)
const systemLoading = ref(false)

async function loadSystem() {
  systemLoading.value = true
  try {
    system.value = await dashboardApi.systemInfo()
  } finally {
    systemLoading.value = false
  }
}

const allChecksOk = computed(() => !!system.value && (system.value.checks ?? []).every((c) => c.ok))

// ---------- 刷新 ----------
const refreshing = ref(false)
let timer: number | undefined

async function refreshAll() {
  refreshing.value = true
  await Promise.allSettled([loadSummary(), loadTrend(), loadDistribution(), loadSystem()])
  refreshing.value = false
}

onMounted(() => {
  refreshAll()
  // 概览与集群状态每 30 秒自动刷新
  timer = window.setInterval(() => {
    if (document.hidden) return
    Promise.allSettled([loadSummary(), loadSystem()])
  }, 30_000)
})

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
})

function go(path: string, query: Record<string, string>) {
  router.push({ path, query })
}
</script>

<template>
  <div class="page dashboard">
    <!-- 今日 vs 昨日 -->
    <div v-loading="summaryLoading && !summary" class="stat-grid">
      <div v-for="card in statCards" :key="card.key" class="stat-card">
        <div class="stat-card__title">{{ card.title }}</div>
        <div class="stat-card__value">{{ card.value }}</div>
        <div class="stat-card__foot">
          <span class="text-secondary">昨日 {{ card.yesterday }}</span>
          <span v-if="card.delta" class="delta" :class="deltaClass(card)">
            <el-icon v-if="card.trend === 1"><Top /></el-icon>
            <el-icon v-else-if="card.trend === -1"><Bottom /></el-icon>
            {{ card.delta }}
          </span>
          <span v-else class="delta delta--flat">--</span>
        </div>
      </div>
    </div>

    <!-- 待处理 -->
    <div class="todo-grid">
      <div class="todo-card" @click="go('/notify-tasks', { status: '0' })">
        <div class="todo-card__dot todo-card__dot--warning" />
        <div class="todo-card__body">
          <div class="todo-card__label">待通知业务方</div>
          <div class="todo-card__hint">等待发送或重试中的业务通知</div>
        </div>
        <div class="todo-card__num">{{ formatCount(summary?.pendingNotify ?? 0) }}</div>
      </div>
      <div class="todo-card" @click="go('/notify-tasks', { status: '2' })">
        <div class="todo-card__dot todo-card__dot--danger" />
        <div class="todo-card__body">
          <div class="todo-card__label">通知失败</div>
          <div class="todo-card__hint">重试耗尽，需要人工重发</div>
        </div>
        <div class="todo-card__num" :class="{ 'text-danger': (summary?.failedNotify ?? 0) > 0 }">
          {{ formatCount(summary?.failedNotify ?? 0) }}
        </div>
      </div>
      <div class="todo-card" @click="go('/refunds', { status: '0' })">
        <div class="todo-card__dot todo-card__dot--primary" />
        <div class="todo-card__body">
          <div class="todo-card__label">处理中退款</div>
          <div class="todo-card__hint">已提交上游、尚未返回结果的退款</div>
        </div>
        <div class="todo-card__num">{{ formatCount(summary?.processingRefund ?? 0) }}</div>
      </div>
    </div>

    <!-- 趋势 -->
    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">交易趋势</div>
        <div class="toolbar__actions">
          <el-radio-group v-model="trendDays" size="small">
            <el-radio-button :value="7">近 7 天</el-radio-button>
            <el-radio-button :value="30">近 30 天</el-radio-button>
          </el-radio-group>
          <el-button size="small" :icon="Refresh" :loading="refreshing" @click="refreshAll">刷新</el-button>
        </div>
      </div>
      <div v-loading="trendLoading">
        <EChart :option="trendOption" height="320px" />
      </div>
    </el-card>

    <!-- 分布 -->
    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">交易分布</div>
        <div class="toolbar__actions">
          <el-radio-group v-model="dimension" size="small">
            <el-radio-button v-for="d in dimensionOptions" :key="d.value" :value="d.value">
              {{ d.label }}
            </el-radio-button>
          </el-radio-group>
          <el-select v-model="distDays" size="small" style="width: 110px">
            <el-option :value="1" label="今天" />
            <el-option :value="7" label="近 7 天" />
            <el-option :value="30" label="近 30 天" />
          </el-select>
        </div>
      </div>
      <el-row v-loading="distLoading" :gutter="20">
        <el-col :span="14">
          <el-table :data="distribution" size="small" max-height="340" empty-text="暂无交易数据">
            <el-table-column :label="dimensionLabel" min-width="130" show-overflow-tooltip>
              <template #default="{ row }">
                {{ row.name || row.key }}
              </template>
            </el-table-column>
            <el-table-column label="订单数" prop="orderCount" width="92" align="right" sortable>
              <template #default="{ row }">{{ formatCount(row.orderCount) }}</template>
            </el-table-column>
            <el-table-column label="成功笔数" prop="paidCount" width="104" align="right" sortable>
              <template #default="{ row }">{{ formatCount(row.paidCount) }}</template>
            </el-table-column>
            <el-table-column label="成功金额" prop="paidAmount" width="128" align="right" sortable>
              <template #default="{ row }">
                <span class="money">{{ formatMoney(row.paidAmount) }}</span>
              </template>
            </el-table-column>
            <el-table-column label="成功率" prop="successRate" min-width="130" sortable>
              <template #default="{ row }">
                <div class="rate-cell">
                  <el-progress
                    :percentage="Math.min(100, Math.max(0, row.successRate * 100))"
                    :show-text="false"
                    :stroke-width="6"
                    :status="row.successRate >= 0.5 ? 'success' : 'warning'"
                  />
                  <span>{{ formatRate(row.successRate) }}</span>
                </div>
              </template>
            </el-table-column>
          </el-table>
        </el-col>
        <el-col :span="10">
          <div class="dist-chart-bar">
            <el-radio-group v-model="distMetric" size="small">
              <el-radio-button value="paidAmount">成功金额</el-radio-button>
              <el-radio-button value="orderCount">订单数</el-radio-button>
            </el-radio-group>
            <el-radio-group v-model="distChart" size="small">
              <el-radio-button value="pie">饼图</el-radio-button>
              <el-radio-button value="bar">柱状图</el-radio-button>
            </el-radio-group>
          </div>
          <EChart v-if="distribution.length" :option="distOption" height="300px" />
          <el-empty v-else description="暂无交易数据" :image-size="80" />
        </el-col>
      </el-row>
    </el-card>

    <!-- 集群 -->
    <el-card v-loading="systemLoading && !system" shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          集群状态
          <span class="toolbar__sub">多副本部署：每个节点定期上报心跳，本页请求由其中任意一个节点响应</span>
        </div>
        <div class="toolbar__actions">
          <el-tag v-if="system" :type="allChecksOk ? 'success' : 'danger'" effect="dark" round>
            {{ allChecksOk ? '依赖正常' : '依赖异常' }}
          </el-tag>
          <el-tag v-if="system" type="primary" effect="plain" round>
            在线节点 {{ system.nodes?.length ?? 0 }}
          </el-tag>
        </div>
      </div>

      <template v-if="system">
        <div class="check-grid">
          <div
            v-for="check in system.checks"
            :key="check.name"
            class="check-item"
            :class="check.ok ? 'check-item--ok' : 'check-item--fail'"
          >
            <el-icon class="check-item__icon">
              <CircleCheckFilled v-if="check.ok" />
              <CircleCloseFilled v-else />
            </el-icon>
            <div class="check-item__body">
              <div class="check-item__name">{{ check.name }}</div>
              <div class="check-item__msg" :title="check.message">
                {{ check.message || (check.ok ? '正常' : '异常') }}
              </div>
            </div>
          </div>
        </div>

        <el-descriptions :column="5" size="small" border class="node-desc" title="当前响应节点">
          <el-descriptions-item label="实例 ID">
            <span class="mono">{{ system.instanceId }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="角色">
            <el-tag v-for="r in system.roles" :key="r" size="small" class="role-tag">{{ r }}</el-tag>
          </el-descriptions-item>
          <el-descriptions-item label="WorkerId">{{ system.workerId }}</el-descriptions-item>
          <el-descriptions-item label="版本">{{ system.version }}</el-descriptions-item>
          <el-descriptions-item label="启动时间">{{ formatTime(system.startedAt) }}</el-descriptions-item>
        </el-descriptions>

        <el-table :data="system.nodes" size="small" empty-text="暂无在线节点">
          <el-table-column label="实例 ID" min-width="200">
            <template #default="{ row }">
              <span class="mono">{{ row.instanceId }}</span>
              <el-tag v-if="row.instanceId === system.instanceId" size="small" type="success" class="role-tag">
                当前
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="主机" prop="host" min-width="150" show-overflow-tooltip />
          <el-table-column label="角色" min-width="160">
            <template #default="{ row }">
              <el-tag v-for="r in row.roles" :key="r" size="small" effect="plain" class="role-tag">{{ r }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="WorkerId" prop="workerId" width="100" align="center" />
          <el-table-column label="版本" prop="version" width="120" />
          <el-table-column label="启动时间" width="190">
            <template #default="{ row }">
              <div class="cell-stack">
                <span>{{ formatTime(row.startedAt) }}</span>
                <span class="text-secondary text-small">已运行 {{ uptime(row.startedAt) }}</span>
              </div>
            </template>
          </el-table-column>
          <el-table-column label="最近心跳" width="190">
            <template #default="{ row }">
              <div class="cell-stack">
                <span>{{ formatTime(row.lastSeen) }}</span>
                <span class="text-secondary text-small">{{ fromNow(row.lastSeen) }}</span>
              </div>
            </template>
          </el-table-column>
        </el-table>
      </template>
      <el-empty v-else-if="!systemLoading" description="无法获取集群信息" :image-size="80" />
    </el-card>
  </div>
</template>

<style scoped>
.stat-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 16px;
}
.stat-card {
  padding: 18px 20px;
  border-radius: 8px;
  background: #fff;
}
.stat-card__title {
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.stat-card__value {
  margin: 10px 0 12px;
  font-size: 26px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
  line-height: 1.2;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.stat-card__foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  font-size: 12px;
}
.delta {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-weight: 600;
  white-space: nowrap;
}
.delta--good {
  color: var(--el-color-success);
}
.delta--bad {
  color: var(--el-color-danger);
}
.delta--flat {
  color: var(--el-text-color-placeholder);
}
.todo-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 16px;
}
.todo-card {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 14px 20px;
  border-radius: 8px;
  background: #fff;
  cursor: pointer;
  transition: box-shadow 0.2s;
}
.todo-card:hover {
  box-shadow: 0 4px 14px rgba(15, 23, 42, 0.08);
}
.todo-card__dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}
.todo-card__dot--warning {
  background: var(--el-color-warning);
}
.todo-card__dot--danger {
  background: var(--el-color-danger);
}
.todo-card__dot--primary {
  background: var(--el-color-primary);
}
.todo-card__body {
  flex: 1;
  min-width: 0;
}
.todo-card__label {
  font-weight: 600;
}
.todo-card__hint {
  margin-top: 2px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.todo-card__num {
  font-size: 24px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}
.rate-cell {
  display: grid;
  grid-template-columns: 1fr 52px;
  align-items: center;
  gap: 8px;
}
.dist-chart-bar {
  display: flex;
  justify-content: space-between;
  margin-bottom: 8px;
}
.check-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: 12px;
  margin-bottom: 16px;
}
.check-item {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
}
.check-item--ok {
  background: #f0fdf4;
  border-color: #bbf7d0;
}
.check-item--fail {
  background: #fef2f2;
  border-color: #fecaca;
}
.check-item__icon {
  font-size: 24px;
}
.check-item--ok .check-item__icon {
  color: var(--el-color-success);
}
.check-item--fail .check-item__icon {
  color: var(--el-color-danger);
}
.check-item__body {
  min-width: 0;
}
.check-item__name {
  font-weight: 600;
}
.check-item__msg {
  color: var(--el-text-color-secondary);
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.node-desc {
  margin-bottom: 16px;
}
.role-tag {
  margin-right: 6px;
}
.mono + .role-tag {
  margin-left: 8px;
}
@media (max-width: 1440px) {
  .stat-card__value {
    font-size: 22px;
  }
}
</style>
