<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import dayjs from 'dayjs'
import { Loading, Plus, Refresh, Search, WarningFilled } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { passagesApi } from '@/api/passages'
import { reconApi } from '@/api/recon'
import type {
  PassageBrief,
  ReconBatchItem,
  ReconBatchListQuery,
  ReconDiffStatus,
  ReconStatus,
} from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import BillDateRangePicker, { type BillDateRange } from '@/components/BillDateRangePicker.vue'
import EnumTag from '@/components/EnumTag.vue'
import ProviderSelect from '@/components/ProviderSelect.vue'
import TablePager from '@/components/TablePager.vue'
import { confirmAction, formatCount, validateForm } from '@/utils/common'
import { RECON_STATUS } from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { formatTime } from '@/utils/time'

const emit = defineEmits<{
  /** 查看某个批次的差异；status 省略表示全部 */
  'view-diffs': [filter: { batchId: number; status?: ReconDiffStatus }]
}>()

const auth = useAuthStore()
const meta = useMetaStore()

interface Query {
  passageId: number | undefined
  provider: string | undefined
  status: ReconStatus | undefined
  dateRange: BillDateRange
}

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<ReconBatchItem, Query, ReconBatchListQuery>(
    reconApi.batches,
    () => ({ passageId: undefined, provider: undefined, status: undefined, dateRange: null }),
    (q) => ({
      passageId: q.passageId,
      provider: q.provider,
      status: q.status,
      startDate: q.dateRange?.[0],
      endDate: q.dateRange?.[1],
    }),
  )

// ---------- 通道下拉 ----------
const allPassages = ref<PassageBrief[]>([])

const passageOptions = computed(() =>
  query.provider ? allPassages.value.filter((p) => p.provider === query.provider) : allPassages.value,
)

/** 只有支付类型支持下载对账单（capabilities.bill）的通道才能对账 */
const billPassages = computed(() =>
  allPassages.value.filter((p) => meta.getProvider(p.provider)?.capabilities?.bill === true),
)

onMounted(async () => {
  meta.loadProviders()
  allPassages.value = (await passagesApi.all()) ?? []
})

function onProviderChange() {
  query.passageId = undefined
  search()
}

// ---------- 自动刷新：批次异步执行，触发后轮询到没有「待执行」为止 ----------
const AUTO_REFRESH_INTERVAL = 3000
const AUTO_REFRESH_MAX = 20
const autoRefreshing = ref(false)
let timer: number | undefined
let remaining = 0

function stopAutoRefresh() {
  if (timer) window.clearTimeout(timer)
  timer = undefined
  autoRefreshing.value = false
}

function startAutoRefresh() {
  remaining = AUTO_REFRESH_MAX
  autoRefreshing.value = true
  if (timer) window.clearTimeout(timer)
  timer = window.setTimeout(tick, AUTO_REFRESH_INTERVAL)
}

async function tick() {
  remaining -= 1
  try {
    await load()
  } catch {
    // 错误已由 http 层提示；出错就停止轮询，避免反复弹提示
    stopAutoRefresh()
    return
  }
  const pending = items.value.some((b) => b.status === 0)
  if (pending && remaining > 0) timer = window.setTimeout(tick, AUTO_REFRESH_INTERVAL)
  else stopAutoRefresh()
}

onBeforeUnmount(stopAutoRefresh)

function manualRefresh() {
  stopAutoRefresh()
  return load()
}

defineExpose({ reload: manualRefresh })

// ---------- 发起对账 ----------
const dialogVisible = ref(false)
const saving = ref(false)
const formRef = ref<FormInstance>()
const form = reactive({ passageId: undefined as number | undefined, billDate: '' })

const rules: FormRules = {
  passageId: [{ required: true, message: '请选择通道', trigger: 'change' }],
  billDate: [{ required: true, message: '请选择账单日', trigger: 'change' }],
}

/** 账单日必须早于今天 */
function disabledDate(date: Date): boolean {
  return !dayjs(date).isBefore(dayjs().startOf('day'))
}

function openCreate() {
  form.passageId = query.passageId !== undefined && billPassages.value.some((p) => p.id === query.passageId)
    ? query.passageId
    : undefined
  form.billDate = dayjs().subtract(1, 'day').format('YYYY-MM-DD')
  dialogVisible.value = true
}

async function submit() {
  if (!(await validateForm(formRef.value))) return
  saving.value = true
  try {
    await reconApi.createBatch({ passageId: form.passageId as number, billDate: form.billDate })
    dialogVisible.value = false
    ElMessage.success('已发起对账，批次在后台执行，列表会自动刷新')
    page.value = 1
    await load()
    startAutoRefresh()
  } finally {
    saving.value = false
  }
}

// ---------- 重跑 ----------
const rerunningId = ref<number | null>(null)

async function rerun(row: ReconBatchItem) {
  const tip = `将重新下载「${row.passageName}」${row.billDate} 的账单并重新比对，原有差异会被重新生成。确定重跑吗？`
  if (!(await confirmAction(tip, '重跑对账'))) return
  rerunningId.value = row.id
  try {
    await reconApi.rerunBatch(row.id)
    ElMessage.success('已重新发起，列表会自动刷新')
    await load()
    startAutoRefresh()
  } finally {
    rerunningId.value = null
  }
}

// ---------- 展示 ----------
/** 只有执行完成的批次才比较两侧数字，待执行 / 失败时上游数字还不可信 */
function compared(row: ReconBatchItem): boolean {
  return row.status === 1 || row.status === 2
}

function payMismatch(row: ReconBatchItem): boolean {
  return (
    compared(row) &&
    (row.upstreamPayCount !== row.localPayCount || row.upstreamPayAmount !== row.localPayAmount)
  )
}

function refundMismatch(row: ReconBatchItem): boolean {
  return (
    compared(row) &&
    (row.upstreamRefundCount !== row.localRefundCount || row.upstreamRefundAmount !== row.localRefundAmount)
  )
}
</script>

<template>
  <div class="panel">
    <el-form inline class="panel__filters" @submit.prevent="search">
      <el-form-item label="支付类型">
        <ProviderSelect v-model="query.provider" @change="onProviderChange" />
      </el-form-item>
      <el-form-item label="通道">
        <el-select
          v-model="query.passageId"
          placeholder="全部通道"
          clearable
          filterable
          style="width: 220px"
          @change="search"
        >
          <el-option v-for="p in passageOptions" :key="p.id" :value="p.id" :label="p.name" />
        </el-select>
      </el-form-item>
      <el-form-item label="状态">
        <el-select v-model="query.status" placeholder="全部" clearable style="width: 130px" @change="search">
          <el-option v-for="s in RECON_STATUS" :key="s.value" :value="s.value" :label="s.label" />
        </el-select>
      </el-form-item>
      <el-form-item label="账单日">
        <BillDateRangePicker v-model="query.dateRange" @change="search" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :icon="Search" @click="search">查询</el-button>
        <el-button :icon="Refresh" @click="reset">重置</el-button>
      </el-form-item>
    </el-form>

    <div class="toolbar">
      <div class="toolbar__title">
        对账批次
        <span class="toolbar__sub">每个「通道 × 账单日」一个批次；系统每天自动对昨日账单，也可以手动发起或重跑</span>
      </div>
      <div class="toolbar__actions">
        <span v-if="autoRefreshing" class="auto-hint">
          <el-icon class="is-loading"><Loading /></el-icon>
          批次执行中，自动刷新…
        </span>
        <el-button :icon="Refresh" :loading="loading && !autoRefreshing" @click="manualRefresh">刷新</el-button>
        <el-button v-if="auth.canWrite" type="primary" :icon="Plus" @click="openCreate">发起对账</el-button>
      </div>
    </div>

    <el-table v-loading="loading && !autoRefreshing" :data="items" row-key="id">
      <el-table-column label="账单日" width="110">
        <template #default="{ row }">
          <span class="bill-date">{{ row.billDate }}</span>
        </template>
      </el-table-column>
      <el-table-column label="通道 / 支付类型" min-width="160">
        <template #default="{ row }">
          <div class="cell-stack">
            <span class="ellipsis" :title="row.passageName">{{ row.passageName }}</span>
            <span class="text-secondary text-small">{{ row.providerName || meta.providerName(row.provider) }}</span>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="状态" width="120">
        <template #default="{ row }">
          <div class="cell-stack cell-stack--tight">
            <EnumTag :options="RECON_STATUS" :value="row.status" />
            <el-tooltip v-if="row.errorMsg" placement="top" :content="row.errorMsg" popper-class="recon-error-tip">
              <span class="text-danger text-small error-line">
                <el-icon><WarningFilled /></el-icon>
                <span class="ellipsis">{{ row.errorMsg }}</span>
              </span>
            </el-tooltip>
            <span v-if="(row.status === 0 || row.status === 3) && row.nextRunAt" class="text-secondary text-small">
              下次 {{ formatTime(row.nextRunAt).slice(5, 16) }}
            </span>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="上游账单" min-width="180">
        <template #default="{ row }">
          <div class="sides" :class="{ 'sides--muted': !compared(row as ReconBatchItem) }">
            <div class="sides__line" :class="{ 'sides__line--diff': payMismatch(row as ReconBatchItem) }">
              <span class="sides__label">支付</span>
              <span>{{ formatCount(row.upstreamPayCount) }} 笔</span>
              <span class="money">{{ formatMoney(row.upstreamPayAmount) }}</span>
            </div>
            <div class="sides__line" :class="{ 'sides__line--diff': refundMismatch(row as ReconBatchItem) }">
              <span class="sides__label">退款</span>
              <span>{{ formatCount(row.upstreamRefundCount) }} 笔</span>
              <span class="money">{{ formatMoney(row.upstreamRefundAmount) }}</span>
            </div>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="本地记录" min-width="196">
        <template #default="{ row }">
          <div class="sides" :class="{ 'sides--muted': !compared(row as ReconBatchItem) }">
            <div class="sides__line" :class="{ 'sides__line--diff': payMismatch(row as ReconBatchItem) }">
              <span class="sides__label">支付</span>
              <span>{{ formatCount(row.localPayCount) }} 笔</span>
              <span class="money">{{ formatMoney(row.localPayAmount) }}</span>
              <el-tooltip v-if="payMismatch(row as ReconBatchItem)" content="支付笔数或金额与上游账单不一致" placement="top">
                <el-icon class="sides__warn"><WarningFilled /></el-icon>
              </el-tooltip>
            </div>
            <div class="sides__line" :class="{ 'sides__line--diff': refundMismatch(row as ReconBatchItem) }">
              <span class="sides__label">退款</span>
              <span>{{ formatCount(row.localRefundCount) }} 笔</span>
              <span class="money">{{ formatMoney(row.localRefundAmount) }}</span>
              <el-tooltip
                v-if="refundMismatch(row as ReconBatchItem)"
                content="退款笔数或金额与上游账单不一致"
                placement="top"
              >
                <el-icon class="sides__warn"><WarningFilled /></el-icon>
              </el-tooltip>
            </div>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="差异 未处理/总数" width="140" align="center">
        <template #default="{ row }">
          <span v-if="!row.diffCount" class="text-secondary">{{ row.status === 1 ? '无差异' : '-' }}</span>
          <span v-else class="diff-links">
            <el-link
              :type="row.openDiffCount > 0 ? 'danger' : 'info'"
              underline="never"
              @click="emit('view-diffs', { batchId: row.id, status: 0 })"
            >
              {{ row.openDiffCount }}
            </el-link>
            <span class="text-secondary">/</span>
            <el-link type="primary" underline="never" @click="emit('view-diffs', { batchId: row.id })">
              {{ row.diffCount }}
            </el-link>
          </span>
        </template>
      </el-table-column>
      <el-table-column label="完成时间" width="165">
        <template #default="{ row }">
          <span v-if="row.finishedAt">{{ formatTime(row.finishedAt) }}</span>
          <span v-else class="text-secondary">{{ row.status === 0 ? '执行中 / 排队中' : '-' }}</span>
        </template>
      </el-table-column>
      <el-table-column v-if="auth.canWrite" label="操作" width="70" fixed="right">
        <template #default="{ row }">
          <el-button
            link
            type="primary"
            :loading="rerunningId === row.id"
            @click="rerun(row as ReconBatchItem)"
          >
            重跑
          </el-button>
        </template>
      </el-table-column>
    </el-table>

    <TablePager
      :total="total"
      :page="page"
      :page-size="pageSize"
      @page-change="onPageChange"
      @size-change="onSizeChange"
    />

    <!-- 发起对账 -->
    <el-dialog v-model="dialogVisible" title="发起对账" width="520px" :close-on-click-modal="false" destroy-on-close>
      <el-form ref="formRef" :model="form" :rules="rules" label-width="80px" @submit.prevent>
        <el-form-item label="通道" prop="passageId">
          <el-select v-model="form.passageId" placeholder="请选择通道" filterable style="width: 100%">
            <el-option v-for="p in billPassages" :key="p.id" :value="p.id" :label="p.name">
              <span>{{ p.name }}</span>
              <span class="option-sub">{{ p.providerName || meta.providerName(p.provider) }}</span>
              <el-tag v-if="p.status !== 1" type="info" size="small" class="option-tag">已停用</el-tag>
            </el-option>
            <template #empty>
              <div class="select-empty">没有可对账的通道（需要支付类型支持下载对账单）</div>
            </template>
          </el-select>
          <div class="form-help">只列出支付类型支持下载对账单的通道。</div>
        </el-form-item>
        <el-form-item label="账单日" prop="billDate">
          <el-date-picker
            v-model="form.billDate"
            type="date"
            value-format="YYYY-MM-DD"
            placeholder="选择账单日"
            :disabled-date="disabledDate"
            :clearable="false"
            style="width: 100%"
          />
          <div class="form-help">只能选择今天之前的日期（上游账单通常次日上午才生成）。</div>
        </el-form-item>
      </el-form>
      <el-alert type="info" :closable="false" show-icon>
        该通道这一天的批次已存在时，会重置并重新执行。对账只产出差异，不会自动改账。
      </el-alert>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">发起</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.panel__filters :deep(.el-form-item) {
  margin-right: 16px;
  margin-bottom: 16px;
}
.auto-hint {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--el-color-primary);
  font-size: 13px;
}
.bill-date {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.error-line {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  cursor: help;
}
.sides {
  display: flex;
  flex-direction: column;
  gap: 2px;
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}
.sides--muted {
  color: var(--el-text-color-secondary);
}
.sides__line {
  display: flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}
.sides__label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.sides__line--diff {
  color: var(--el-color-danger);
  font-weight: 600;
}
.sides__line--diff .sides__label {
  color: var(--el-color-danger);
}
.sides__warn {
  color: var(--el-color-danger);
  cursor: help;
}
.diff-links {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.option-sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.option-tag {
  margin-left: 8px;
}
.select-empty {
  padding: 14px;
  text-align: center;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
</style>

<style>
.recon-error-tip {
  max-width: 420px;
  line-height: 1.6;
  word-break: break-all;
}
</style>
