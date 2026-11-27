<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, Plus, Refresh, WarningFilled } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { bindingsApi, toBindingInput } from '@/api/bindings'
import { channelsApi } from '@/api/channels'
import { HttpError } from '@/api/http'
import type { BindingItem, ChannelItem, EnableStatus } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import EnumTag from '@/components/EnumTag.vue'
import { confirmAction, formatCount, formatRate } from '@/utils/common'
import { ENABLE_STATUS } from '@/utils/enums'
import { formatLimit, formatMoney } from '@/utils/money'
import BindingDialog from './BindingDialog.vue'
import RouteSimulatePanel from './RouteSimulatePanel.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const meta = useMetaStore()

const channelId = Number(route.params.id)

const channel = ref<ChannelItem | null>(null)
const bindings = ref<BindingItem[]>([])
const loading = ref(false)
const notFound = ref(false)

async function loadChannel() {
  try {
    channel.value = await channelsApi.get(channelId)
  } catch (err) {
    // 业务错误（如渠道不存在）才展示「不存在」，网络类错误保留页面以便重试
    if (!channel.value && err instanceof HttpError && (err.httpStatus === 404 || err.httpStatus === 200)) {
      notFound.value = true
    }
    throw err
  }
}

async function loadBindings() {
  loading.value = true
  try {
    bindings.value = (await bindingsApi.list(channelId)) ?? []
  } finally {
    loading.value = false
  }
}

async function refresh() {
  await Promise.allSettled([loadChannel(), loadBindings()])
}

onMounted(() => {
  meta.loadProviders()
  refresh()
})

// ---------- 分组与占比 ----------
interface BindingRow extends BindingItem {
  /** 绑定与通道均为启用状态，才参与路由 */
  effective: boolean
  /** 同支付类型 + 同优先级组内的权重占比（0~1）；null 表示不参与按权重分流 */
  share: number | null
  shareText: string
  /** 优先级列的合并行数（每个优先级组的首行 > 0，其余为 0） */
  prioritySpan: number
  /** 优先级组序号，用于行底色区分 */
  priorityIndex: number
}

interface ProviderGroup {
  provider: string
  providerName: string
  rows: BindingRow[]
  enabledCount: number
  breakerCount: number
  priorityCount: number
}

const groups = computed<ProviderGroup[]>(() => {
  const byProvider = new Map<string, BindingItem[]>()
  for (const b of bindings.value) {
    const list = byProvider.get(b.provider)
    if (list) list.push(b)
    else byProvider.set(b.provider, [b])
  }

  const result: ProviderGroup[] = []
  for (const [provider, list] of byProvider) {
    const sorted = [...list].sort((a, b) => a.priority - b.priority || a.id - b.id)
    const isEffective = (b: BindingItem) => b.status === 1 && b.passageStatus === 1

    const weightByPriority = new Map<number, number>()
    const countByPriority = new Map<number, number>()
    for (const b of sorted) {
      countByPriority.set(b.priority, (countByPriority.get(b.priority) ?? 0) + 1)
      if (isEffective(b)) weightByPriority.set(b.priority, (weightByPriority.get(b.priority) ?? 0) + b.weight)
    }

    const priorities = [...countByPriority.keys()]
    const rows: BindingRow[] = sorted.map((b, index) => {
      const effective = isEffective(b)
      const total = weightByPriority.get(b.priority) ?? 0
      let share: number | null = null
      let shareText: string
      if (!effective) shareText = '不参与'
      else if (b.weight === 0) shareText = '备用'
      else {
        share = total > 0 ? b.weight / total : null
        shareText = share === null ? '-' : formatRate(share)
      }
      const isFirstOfPriority = index === 0 || sorted[index - 1].priority !== b.priority
      return {
        ...b,
        effective,
        share,
        shareText,
        prioritySpan: isFirstOfPriority ? (countByPriority.get(b.priority) ?? 1) : 0,
        priorityIndex: priorities.indexOf(b.priority),
      }
    })

    result.push({
      provider,
      providerName: list[0].providerName || meta.providerName(provider),
      rows,
      enabledCount: rows.filter((r) => r.effective).length,
      breakerCount: rows.filter((r) => r.runtime?.breakerOpen).length,
      priorityCount: priorities.length,
    })
  }
  return result
})

const effectiveCount = computed(() => groups.value.reduce((sum, g) => sum + g.enabledCount, 0))
const breakerCount = computed(() => groups.value.reduce((sum, g) => sum + g.breakerCount, 0))

function spanMethod(data: { row: unknown; columnIndex: number }) {
  if (data.columnIndex !== 0) return { rowspan: 1, colspan: 1 }
  const span = (data.row as BindingRow).prioritySpan
  return span > 0 ? { rowspan: span, colspan: 1 } : { rowspan: 0, colspan: 0 }
}

function rowClassName(data: { row: unknown }) {
  const row = data.row as BindingRow
  const classes = [row.priorityIndex % 2 === 1 ? 'row--alt' : '']
  if (!row.effective) classes.push('row--disabled')
  return classes.join(' ')
}

function usagePercent(used: number, limit: number): number {
  if (!limit) return 0
  return Math.min(100, Math.max(0, (used / limit) * 100))
}

function usageColor(used: number, limit: number): string {
  const p = usagePercent(used, limit)
  if (p >= 100) return '#ef4444'
  if (p >= 80) return '#f59e0b'
  return '#2563eb'
}

function timeWindow(row: BindingItem): { text: string; crossDay: boolean } {
  if (!row.startTime || !row.endTime) return { text: '全天', crossDay: false }
  const crossDay = row.startTime > row.endTime
  return { text: `${row.startTime} ~ ${crossDay ? '次日 ' : ''}${row.endTime}`, crossDay }
}

// ---------- 操作 ----------
const dialogVisible = ref(false)
const editing = ref<BindingItem | null>(null)

function openCreate() {
  editing.value = null
  dialogVisible.value = true
}

function openEdit(row: BindingRow) {
  editing.value = bindings.value.find((b) => b.id === row.id) ?? null
  dialogVisible.value = true
}

const busyId = ref<number | null>(null)

async function changeStatus(row: BindingRow, value: EnableStatus) {
  busyId.value = row.id
  try {
    await bindingsApi.update(row.id, { ...toBindingInput(row), status: value })
    ElMessage.success(value === 1 ? '已启用' : '已停用')
    await loadBindings()
  } finally {
    busyId.value = null
  }
}

async function remove(row: BindingRow) {
  if (!(await confirmAction(`确定将通道「${row.passageName}」从该渠道移除吗？通道本身不会被删除。`, '移除通道'))) return
  await bindingsApi.remove(row.id)
  ElMessage.success('已移除')
  await Promise.allSettled([loadBindings(), loadChannel()])
}

async function resetBreaker(row: BindingRow) {
  if (!(await confirmAction(`确定手动恢复「${row.passageName}」的熔断状态吗？恢复后该通道会立即重新参与路由。`, '恢复熔断'))) {
    return
  }
  busyId.value = row.id
  try {
    await bindingsApi.resetBreaker(row.id)
    ElMessage.success('熔断已恢复')
    await loadBindings()
  } finally {
    busyId.value = null
  }
}

function onSaved() {
  Promise.allSettled([loadBindings(), loadChannel()])
}
</script>

<template>
  <div class="page">
    <el-card v-if="notFound" shadow="never">
      <el-result icon="warning" title="渠道不存在" sub-title="该渠道可能已被删除">
        <template #extra>
          <el-button type="primary" @click="router.replace('/channels')">返回渠道管理</el-button>
        </template>
      </el-result>
    </el-card>

    <template v-else>
      <!-- 渠道信息 -->
      <el-card shadow="never">
        <div class="channel-head">
          <el-button :icon="ArrowLeft" circle @click="router.push('/channels')" />
          <div class="channel-head__main">
            <div class="channel-head__title">
              <span v-if="channel">{{ channel.appName }} / {{ channel.name }}</span>
              <span v-else>渠道通道配置</span>
              <EnumTag v-if="channel" :options="ENABLE_STATUS" :value="channel.status" />
            </div>
            <div v-if="channel" class="channel-head__meta">
              <span>应用编码 <b class="mono">{{ channel.appCode }}</b></span>
              <span>渠道编码 <b class="mono">{{ channel.code }}</b></span>
              <span>已绑通道 <b>{{ bindings.length }}</b> 条（可用 {{ effectiveCount }}）</span>
              <span>支付类型 <b>{{ groups.length }}</b> 种</span>
              <span v-if="channel.remark">备注：{{ channel.remark }}</span>
            </div>
          </div>
          <div class="channel-head__actions">
            <el-button :icon="Refresh" :loading="loading" @click="refresh">刷新</el-button>
            <el-button v-if="auth.canWrite" type="primary" :icon="Plus" @click="openCreate">添加通道</el-button>
          </div>
        </div>

        <div class="flow">
          <div class="flow__step">
            <span class="flow__no">1</span>
            <div>
              <div class="flow__title">过滤</div>
              <div class="flow__desc">按支付方式、起付金额 / 单笔上限、日限额、可用时段、启停与熔断状态筛出可用通道</div>
            </div>
          </div>
          <div class="flow__step">
            <span class="flow__no">2</span>
            <div>
              <div class="flow__title">优先级</div>
              <div class="flow__desc">取优先级数字最小的一组；该组全部不可用才落到下一组</div>
            </div>
          </div>
          <div class="flow__step">
            <span class="flow__no">3</span>
            <div>
              <div class="flow__title">权重随机</div>
              <div class="flow__desc">组内按权重比例随机选择；权重 0 的通道只做备用</div>
            </div>
          </div>
          <div class="flow__step">
            <span class="flow__no">4</span>
            <div>
              <div class="flow__title">故障切换</div>
              <div class="flow__desc">下单失败自动换下一条通道；连续失败触发熔断，暂时摘除</div>
            </div>
          </div>
        </div>
      </el-card>

      <el-alert
        v-if="channel && channel.status !== 1"
        type="warning"
        show-icon
        :closable="false"
        title="该渠道当前为停用状态，无法下单。"
      />
      <el-alert
        v-if="!loading && bindings.length > 0 && effectiveCount === 0"
        type="error"
        show-icon
        :closable="false"
        title="该渠道下没有任何启用中的通道，所有订单都会下单失败。"
      />
      <el-alert
        v-if="breakerCount > 0"
        type="error"
        show-icon
        :closable="false"
        :title="`有 ${breakerCount} 条通道处于熔断状态，暂时不参与路由。排查上游问题后可点击「恢复」手动恢复。`"
      />

      <!-- 按支付类型分组的绑定列表 -->
      <div v-loading="loading" class="groups">
        <el-card v-if="!loading && bindings.length === 0" shadow="never">
          <el-empty description="该渠道还没有绑定任何通道，无法下单">
            <el-button v-if="auth.canWrite" type="primary" :icon="Plus" @click="openCreate">添加通道</el-button>
          </el-empty>
        </el-card>

        <el-card v-for="group in groups" :key="group.provider" shadow="never" class="group">
          <div class="group__head">
            <div class="group__title">
              <span class="group__name">{{ group.providerName }}</span>
              <span class="mono text-secondary">{{ group.provider }}</span>
            </div>
            <div class="group__stats">
              <el-tag effect="plain" round>{{ group.rows.length }} 条通道</el-tag>
              <el-tag :type="group.enabledCount > 0 ? 'success' : 'danger'" effect="plain" round>
                可用 {{ group.enabledCount }}
              </el-tag>
              <el-tag v-if="group.priorityCount > 1" type="info" effect="plain" round>
                {{ group.priorityCount }} 个优先级组
              </el-tag>
              <el-tag v-if="group.breakerCount > 0" type="danger" effect="dark" round>
                熔断 {{ group.breakerCount }}
              </el-tag>
            </div>
          </div>

          <el-table
            :data="group.rows"
            row-key="id"
            border
            :span-method="spanMethod"
            :row-class-name="rowClassName"
            class="binding-table"
          >
            <el-table-column label="优先级" width="74" align="center">
              <template #header>
                <el-tooltip content="数字越小越优先；同一优先级内按权重分流" placement="top">
                  <span>优先级</span>
                </el-tooltip>
              </template>
              <template #default="{ row }">
                <span class="priority-badge">{{ row.priority }}</span>
              </template>
            </el-table-column>

            <el-table-column label="通道" min-width="150" class-name="dim-col">
              <template #default="{ row }">
                <div class="cell-stack">
                  <span class="passage-name">
                    {{ row.passageName }}
                    <el-tag v-if="row.passageStatus !== 1" type="danger" size="small">通道已停用</el-tag>
                  </span>
                  <span v-if="row.remark" class="text-secondary text-small">{{ row.remark }}</span>
                </div>
              </template>
            </el-table-column>

            <el-table-column label="支付方式" min-width="120" class-name="dim-col">
              <template #default="{ row }">
                <div class="tag-list">
                  <el-tag v-if="!row.methods || row.methods.length === 0" type="info" effect="plain" size="small">
                    全部
                  </el-tag>
                  <el-tooltip v-for="m in row.methods" :key="m" :content="m" placement="top">
                    <el-tag effect="plain" size="small">{{ meta.methodName(m) }}</el-tag>
                  </el-tooltip>
                </div>
              </template>
            </el-table-column>

            <el-table-column label="权重 / 组内占比" width="140" class-name="dim-col">
              <template #header>
                <el-tooltip placement="top">
                  <template #content>
                    占比 = 权重 ÷ 同支付类型、同优先级、已启用通道的权重之和。<br />
                    实际命中还受支付方式、金额、限额、时段、熔断影响，可用下方「路由模拟」验证。
                  </template>
                  <span>权重 / 组内占比</span>
                </el-tooltip>
              </template>
              <template #default="{ row }">
                <div class="weight-cell">
                  <div class="weight-cell__top">
                    <span class="weight-cell__weight">{{ row.weight }}</span>
                    <span
                      class="weight-cell__share"
                      :class="{ 'weight-cell__share--muted': row.share === null }"
                    >
                      {{ row.shareText }}
                    </span>
                  </div>
                  <el-progress
                    :percentage="row.share === null ? 0 : Math.round(row.share * 1000) / 10"
                    :show-text="false"
                    :stroke-width="6"
                  />
                </div>
              </template>
            </el-table-column>

            <el-table-column label="金额 / 时段条件" width="176" class-name="dim-col">
              <template #default="{ row }">
                <div class="usage">
                  <div class="usage__line">
                    <span class="usage__label">起付</span>
                    <span :class="row.minAmount ? 'money' : 'text-secondary'">{{ formatLimit(row.minAmount) }}</span>
                  </div>
                  <div class="usage__line">
                    <span class="usage__label">上限</span>
                    <span :class="row.maxAmount ? 'money' : 'text-secondary'">{{ formatLimit(row.maxAmount) }}</span>
                  </div>
                  <div class="usage__line">
                    <span class="usage__label">时段</span>
                    <span class="nowrap" :class="{ 'text-secondary': timeWindow(row as BindingRow).text === '全天' }">
                      {{ timeWindow(row as BindingRow).text }}
                    </span>
                  </div>
                </div>
              </template>
            </el-table-column>

            <el-table-column label="日限额 / 今日已用" min-width="186" class-name="dim-col">
              <template #default="{ row }">
                <div class="usage">
                  <div class="usage__line">
                    <span class="usage__label">金额</span>
                    <span class="usage__text">
                      {{ formatMoney(row.runtime?.todayAmount ?? 0) }} /
                      {{ formatLimit(row.dailyLimitAmount) }}
                    </span>
                  </div>
                  <el-progress
                    v-if="row.dailyLimitAmount > 0"
                    :percentage="usagePercent(row.runtime?.todayAmount ?? 0, row.dailyLimitAmount)"
                    :color="usageColor(row.runtime?.todayAmount ?? 0, row.dailyLimitAmount)"
                    :show-text="false"
                    :stroke-width="5"
                  />
                  <div class="usage__line">
                    <span class="usage__label">笔数</span>
                    <span class="usage__text">
                      {{ formatCount(row.runtime?.todayCount ?? 0) }} /
                      {{ row.dailyLimitCount ? formatCount(row.dailyLimitCount) : '不限' }}
                    </span>
                  </div>
                  <el-progress
                    v-if="row.dailyLimitCount > 0"
                    :percentage="usagePercent(row.runtime?.todayCount ?? 0, row.dailyLimitCount)"
                    :color="usageColor(row.runtime?.todayCount ?? 0, row.dailyLimitCount)"
                    :show-text="false"
                    :stroke-width="5"
                  />
                </div>
              </template>
            </el-table-column>

            <el-table-column label="熔断" width="96" align="center">
              <template #default="{ row }">
                <div class="breaker">
                  <el-tag v-if="row.runtime?.breakerOpen" type="danger" effect="dark">
                    <el-icon class="breaker__icon"><WarningFilled /></el-icon>已熔断
                  </el-tag>
                  <el-tag v-else type="success" effect="plain">正常</el-tag>
                  <span
                    v-if="(row.runtime?.recentFailures ?? 0) > 0"
                    class="text-small"
                    :class="row.runtime?.breakerOpen ? 'text-danger' : 'text-secondary'"
                  >
                    失败 {{ row.runtime.recentFailures }} 次
                  </span>
                  <el-button
                    v-if="auth.canWrite && row.runtime?.breakerOpen"
                    size="small"
                    type="danger"
                    plain
                    :loading="busyId === row.id"
                    @click="resetBreaker(row as BindingRow)"
                  >
                    恢复
                  </el-button>
                </div>
              </template>
            </el-table-column>

            <el-table-column label="状态" width="76" align="center">
              <template #default="{ row }">
                <el-switch
                  v-if="auth.canWrite"
                  :model-value="row.status"
                  :active-value="1"
                  :inactive-value="0"
                  :loading="busyId === row.id"
                  @change="(v: string | number | boolean) => changeStatus(row as BindingRow, v === 1 ? 1 : 0)"
                />
                <EnumTag v-else :options="ENABLE_STATUS" :value="row.status" />
              </template>
            </el-table-column>

            <el-table-column v-if="auth.canWrite" label="操作" width="110" align="center" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openEdit(row as BindingRow)">编辑</el-button>
                <el-button link type="danger" @click="remove(row as BindingRow)">移除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-card>
      </div>

      <RouteSimulatePanel :channel-id="channelId" :bindings="bindings" />
    </template>

    <BindingDialog
      v-model="dialogVisible"
      :channel-id="channelId"
      :binding="editing"
      :bindings="bindings"
      @saved="onSaved"
    />
  </div>
</template>

<style scoped>
.channel-head {
  display: flex;
  align-items: center;
  gap: 16px;
}
.channel-head__main {
  flex: 1;
  min-width: 0;
}
.channel-head__title {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 18px;
  font-weight: 700;
}
.channel-head__meta {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 22px;
  margin-top: 6px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.channel-head__meta b {
  color: var(--el-text-color-primary);
}
.channel-head__actions {
  display: flex;
  gap: 8px;
  flex-shrink: 0;
}
.flow {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
  margin-top: 16px;
}
.flow__step {
  display: flex;
  gap: 10px;
  padding: 10px 12px;
  border-radius: 8px;
  background: #f8fafc;
}
.flow__no {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: 50%;
  background: var(--el-color-primary);
  color: #fff;
  font-size: 12px;
  font-weight: 700;
  flex-shrink: 0;
}
.flow__title {
  font-weight: 600;
  line-height: 22px;
}
.flow__desc {
  margin-top: 2px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}
.groups {
  display: flex;
  flex-direction: column;
  gap: 16px;
  min-height: 80px;
}
.group__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}
.group__title {
  display: flex;
  align-items: baseline;
  gap: 10px;
  padding-left: 10px;
  border-left: 4px solid var(--el-color-primary);
}
.group__name {
  font-size: 16px;
  font-weight: 700;
}
.group__stats {
  display: flex;
  gap: 8px;
}
.priority-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 30px;
  height: 30px;
  padding: 0 8px;
  border-radius: 15px;
  background: var(--el-color-primary-light-9);
  color: var(--el-color-primary);
  font-weight: 700;
}
.passage-name {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 6px;
  font-weight: 600;
}
.tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
}
.weight-cell__top {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  margin-bottom: 4px;
}
.weight-cell__weight {
  font-size: 16px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}
.weight-cell__share {
  color: var(--el-color-primary);
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.weight-cell__share--muted {
  color: var(--el-text-color-secondary);
  font-weight: 400;
}
.usage {
  display: flex;
  flex-direction: column;
  gap: 3px;
}
.usage__line {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12.5px;
  line-height: 1.5;
}
.usage__label {
  color: var(--el-text-color-secondary);
  flex-shrink: 0;
}
.usage__text {
  font-variant-numeric: tabular-nums;
}
.nowrap {
  white-space: nowrap;
}
.breaker {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
}
.breaker__icon {
  margin-right: 3px;
  vertical-align: -2px;
}
.binding-table :deep(.row--alt > td.el-table__cell) {
  background: #fafbfd;
}
.binding-table :deep(.row--disabled > td.dim-col > .cell) {
  opacity: 0.55;
}
@media (max-width: 1440px) {
  .flow {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}
</style>
