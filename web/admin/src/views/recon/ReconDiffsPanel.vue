<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRoute } from 'vue-router'
import { QuestionFilled, Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { passagesApi } from '@/api/passages'
import { reconApi } from '@/api/recon'
import type {
  PassageBrief,
  ReconBizType,
  ReconDiffItem,
  ReconDiffListQuery,
  ReconDiffStatus,
  ReconDiffType,
} from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import BillDateRangePicker, { type BillDateRange } from '@/components/BillDateRangePicker.vue'
import DetailDrawers from '@/components/DetailDrawers.vue'
import EnumTag from '@/components/EnumTag.vue'
import TablePager from '@/components/TablePager.vue'
import { validateForm } from '@/utils/common'
import {
  RECON_BIZ_TYPE,
  RECON_DIFF_HINT,
  RECON_DIFF_STATUS,
  RECON_DIFF_TYPE,
  enumLabel,
} from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { formatTime } from '@/utils/time'

const emit = defineEmits<{
  /** 差异被标记为已处理，批次列表的未处理数需要刷新 */
  handled: []
}>()

const route = useRoute()
const auth = useAuthStore()
const meta = useMetaStore()

interface Query {
  /** 输入框里是文本，提交时转成数字 */
  batchId: string
  passageId: number | undefined
  status: ReconDiffStatus | undefined
  diffType: ReconDiffType | undefined
  bizType: ReconBizType | undefined
  orderNo: string
  dateRange: BillDateRange
}

function queryString(key: string): string {
  const v = route.query[key]
  return typeof v === 'string' ? v : ''
}

/** 默认只看待处理；地址栏 status=all 表示全部 */
function initialStatus(): ReconDiffStatus | undefined {
  const v = queryString('status')
  if (v === 'all') return undefined
  return v === '1' ? 1 : 0
}

let firstQuery = true

const { query, items, total, page, pageSize, loading, load, search, onPageChange, onSizeChange } =
  usePagedTable<ReconDiffItem, Query, ReconDiffListQuery>(
    reconApi.diffs,
    () => {
      // 仅首次使用地址栏带入的条件（来自仪表盘或批次列表的跳转）
      const first = firstQuery
      firstQuery = false
      return {
        batchId: first ? queryString('batchId') : '',
        passageId: undefined,
        status: first ? initialStatus() : 0,
        diffType: undefined,
        bizType: undefined,
        orderNo: first ? queryString('orderNo') : '',
        dateRange: null,
      }
    },
    (q) => ({
      batchId: /^\d+$/.test(q.batchId.trim()) ? Number(q.batchId.trim()) : undefined,
      passageId: q.passageId,
      status: q.status,
      diffType: q.diffType,
      bizType: q.bizType,
      orderNo: q.orderNo,
      startDate: q.dateRange?.[0],
      endDate: q.dateRange?.[1],
    }),
  )

function resetQuery() {
  Object.assign(query, {
    batchId: '',
    passageId: undefined,
    status: 0,
    diffType: undefined,
    bizType: undefined,
    orderNo: '',
    dateRange: null,
  } satisfies Query)
  return search()
}

/** 供父级调用：从批次列表点击差异数时，按批次（与处理状态）筛选 */
function applyFilter(filter: { batchId: number; status?: ReconDiffStatus }) {
  Object.assign(query, {
    batchId: String(filter.batchId),
    passageId: undefined,
    status: filter.status,
    diffType: undefined,
    bizType: undefined,
    orderNo: '',
    dateRange: null,
  } satisfies Query)
  return search()
}

defineExpose({ applyFilter, reload: load })

const allPassages = ref<PassageBrief[]>([])

onMounted(async () => {
  meta.loadProviders()
  allPassages.value = (await passagesApi.all()) ?? []
})

// ---------- 展示 ----------
/**
 * 订单号是否是本系统的订单：本地一侧存在（有本地金额），或差异类型本身就意味着本地有记录。
 * 「上游有本地无」且本地金额为空时，订单号来自上游账单，本地查不到。
 */
function isLocalOrder(row: ReconDiffItem): boolean {
  if (!row.orderNo) return false
  return row.diffType !== 1 || row.localAmount !== null
}

function amountText(value: number | null | undefined): string {
  return value === null || value === undefined ? '-' : formatMoney(value)
}

const drawers = ref<InstanceType<typeof DetailDrawers>>()

function openOrder(orderNo: string | null) {
  drawers.value?.openOrder(orderNo)
}

// ---------- 标记已处理 ----------
const dialogVisible = ref(false)
const saving = ref(false)
const formRef = ref<FormInstance>()
const target = ref<ReconDiffItem | null>(null)
const form = reactive({ remark: '' })

const rules: FormRules = {
  remark: [{ required: true, whitespace: true, message: '请填写处理说明', trigger: 'blur' }],
}

const targetHint = computed(() => (target.value ? RECON_DIFF_HINT[target.value.diffType] : undefined))

function openHandle(row: ReconDiffItem) {
  target.value = row
  form.remark = ''
  dialogVisible.value = true
}

async function submit() {
  if (!target.value) return
  if (!(await validateForm(formRef.value))) return
  saving.value = true
  try {
    await reconApi.handleDiff(target.value.id, { remark: form.remark.trim() })
    dialogVisible.value = false
    ElMessage.success('已标记为已处理')
    emit('handled')
    await load()
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div class="panel">
    <el-form inline class="panel__filters" @submit.prevent="search">
      <el-form-item label="批次 ID">
        <el-input v-model="query.batchId" placeholder="全部批次" clearable style="width: 120px" @keyup.enter="search" @clear="search" />
      </el-form-item>
      <el-form-item label="通道">
        <el-select
          v-model="query.passageId"
          placeholder="全部通道"
          clearable
          filterable
          style="width: 200px"
          @change="search"
        >
          <el-option v-for="p in allPassages" :key="p.id" :value="p.id" :label="p.name" />
        </el-select>
      </el-form-item>
      <el-form-item label="处理状态">
        <el-select v-model="query.status" placeholder="全部" clearable style="width: 120px" @change="search">
          <el-option v-for="s in RECON_DIFF_STATUS" :key="s.value" :value="s.value" :label="s.label" />
        </el-select>
      </el-form-item>
      <el-form-item label="差异类型">
        <el-select v-model="query.diffType" placeholder="全部" clearable style="width: 150px" @change="search">
          <el-option v-for="s in RECON_DIFF_TYPE" :key="s.value" :value="s.value" :label="s.label" />
        </el-select>
      </el-form-item>
      <el-form-item label="业务类型">
        <el-select v-model="query.bizType" placeholder="全部" clearable style="width: 110px" @change="search">
          <el-option v-for="s in RECON_BIZ_TYPE" :key="s.value" :value="s.value" :label="s.label" />
        </el-select>
      </el-form-item>
      <el-form-item label="订单号">
        <el-input v-model="query.orderNo" placeholder="平台订单号" clearable style="width: 200px" @keyup.enter="search" />
      </el-form-item>
      <el-form-item label="账单日">
        <BillDateRangePicker v-model="query.dateRange" @change="search" />
      </el-form-item>
      <el-form-item>
        <el-button type="primary" :icon="Search" @click="search">查询</el-button>
        <el-button :icon="Refresh" @click="resetQuery">重置</el-button>
      </el-form-item>
    </el-form>

    <div class="hints">
      <div v-for="t in RECON_DIFF_TYPE" :key="t.value" class="hints__item">
        <el-tag :type="t.type" effect="light" size="small">{{ t.label }}</el-tag>
        <span>{{ RECON_DIFF_HINT[t.value].detail }}</span>
      </div>
    </div>

    <div class="toolbar">
      <div class="toolbar__title">
        差异明细
        <span class="toolbar__sub">共 {{ total }} 条；对账只产出差异，不会自动改账，核实并处理后请标记为已处理</span>
      </div>
      <div class="toolbar__actions">
        <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <el-table v-loading="loading" :data="items" row-key="id">
      <el-table-column label="账单日 / 批次" width="112">
        <template #default="{ row }">
          <div class="cell-stack">
            <span class="bill-date">{{ row.billDate }}</span>
            <span class="text-secondary text-small">批次 #{{ row.batchId }}</span>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="通道" min-width="150">
        <template #default="{ row }">
          <div class="cell-stack">
            <span class="ellipsis" :title="row.passageName">{{ row.passageName }}</span>
            <span class="text-secondary text-small">{{ meta.providerName(row.provider) }}</span>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="差异" min-width="200">
        <template #default="{ row }">
          <div class="cell-stack cell-stack--tight">
            <span class="diff-tags">
              <EnumTag :options="RECON_BIZ_TYPE" :value="row.bizType" size="small" effect="plain" />
              <EnumTag :options="RECON_DIFF_TYPE" :value="row.diffType" size="small" />
              <el-tooltip
                v-if="RECON_DIFF_HINT[row.diffType as ReconDiffType]"
                placement="top"
                popper-class="recon-hint-tip"
                :content="RECON_DIFF_HINT[row.diffType as ReconDiffType].detail"
              >
                <el-icon class="diff-help"><QuestionFilled /></el-icon>
              </el-tooltip>
            </span>
            <span class="text-secondary text-small diff-short">
              {{ RECON_DIFF_HINT[row.diffType as ReconDiffType]?.short }}
            </span>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="订单号 / 退款单号 / 上游交易号" min-width="236">
        <template #default="{ row }">
          <div class="nos">
            <div class="nos__line">
              <span class="nos__label">订单</span>
              <el-link
                v-if="isLocalOrder(row as ReconDiffItem)"
                type="primary"
                underline="never"
                class="mono"
                @click="openOrder(row.orderNo)"
              >
                {{ row.orderNo }}
              </el-link>
              <span v-else class="mono">{{ row.orderNo || '-' }}</span>
            </div>
            <div v-if="row.refundNo || row.bizType === 2" class="nos__line">
              <span class="nos__label">退款</span>
              <span class="mono">{{ row.refundNo || '-' }}</span>
            </div>
            <div class="nos__line">
              <span class="nos__label">上游</span>
              <span class="mono ellipsis" :title="row.upstreamTradeNo">{{ row.upstreamTradeNo || '-' }}</span>
            </div>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="本地 vs 上游" min-width="184">
        <template #default="{ row }">
          <div class="nos" :class="{ 'nos--mismatch': row.diffType === 3 }">
            <div class="nos__line">
              <span class="nos__label">本地</span>
              <span class="money">{{ amountText(row.localAmount) }}</span>
              <span v-if="row.localStatus" class="text-secondary text-small ellipsis" :title="row.localStatus">
                {{ row.localStatus }}
              </span>
            </div>
            <div class="nos__line">
              <span class="nos__label">上游</span>
              <span class="money">{{ amountText(row.upstreamAmount) }}</span>
            </div>
          </div>
        </template>
      </el-table-column>
      <el-table-column label="处理" min-width="160">
        <template #default="{ row }">
          <div class="cell-stack cell-stack--tight">
            <EnumTag :options="RECON_DIFF_STATUS" :value="row.status" size="small" />
            <template v-if="row.status === 1">
              <span class="text-secondary text-small">
                {{ row.handledBy || '-' }} · {{ formatTime(row.handledAt).slice(5, 16) }}
              </span>
              <span class="text-small ellipsis" :title="row.remark">{{ row.remark }}</span>
            </template>
          </div>
        </template>
      </el-table-column>
      <el-table-column v-if="auth.canWrite" label="操作" width="100" fixed="right">
        <template #default="{ row }">
          <el-button v-if="row.status === 0" link type="primary" @click="openHandle(row as ReconDiffItem)">
            标记已处理
          </el-button>
          <span v-else class="text-secondary">-</span>
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

    <!-- 标记已处理 -->
    <el-dialog v-model="dialogVisible" title="标记差异为已处理" width="600px" :close-on-click-modal="false" destroy-on-close>
      <template v-if="target">
        <el-descriptions :column="2" border size="small" class="handle-desc">
          <el-descriptions-item label="账单日">{{ target.billDate }}</el-descriptions-item>
          <el-descriptions-item label="通道">{{ target.passageName }}</el-descriptions-item>
          <el-descriptions-item label="差异类型">
            {{ enumLabel(RECON_BIZ_TYPE, target.bizType) }} · {{ enumLabel(RECON_DIFF_TYPE, target.diffType) }}
          </el-descriptions-item>
          <el-descriptions-item label="本地状态">{{ target.localStatus || '-' }}</el-descriptions-item>
          <el-descriptions-item label="本地金额">{{ amountText(target.localAmount) }}</el-descriptions-item>
          <el-descriptions-item label="上游金额">{{ amountText(target.upstreamAmount) }}</el-descriptions-item>
          <el-descriptions-item label="订单号" :span="2">
            <span class="mono">{{ target.orderNo || '-' }}</span>
            <el-button
              v-if="isLocalOrder(target)"
              link
              type="primary"
              class="open-order"
              @click="openOrder(target.orderNo)"
            >
              打开订单详情
            </el-button>
          </el-descriptions-item>
        </el-descriptions>
        <el-alert v-if="targetHint" type="info" show-icon :closable="false" class="handle-hint">
          {{ targetHint.detail }}
        </el-alert>
        <el-form ref="formRef" :model="form" :rules="rules" label-position="top" @submit.prevent>
          <el-form-item label="处理说明" prop="remark">
            <el-input
              v-model="form.remark"
              type="textarea"
              :rows="3"
              maxlength="255"
              show-word-limit
              placeholder="写清楚核实结论与处理方式，例如：已在订单详情同步补单，订单变为支付成功"
            />
          </el-form-item>
        </el-form>
      </template>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">确认已处理</el-button>
      </template>
    </el-dialog>

    <DetailDrawers ref="drawers" />
  </div>
</template>

<style scoped>
.panel__filters :deep(.el-form-item) {
  margin-right: 16px;
  margin-bottom: 16px;
}
.hints {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 16px;
  padding: 10px 14px;
  border-radius: 8px;
  background: #f8fafc;
  color: #475569;
  font-size: 12.5px;
  line-height: 1.6;
}
.hints__item {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}
.hints__item .el-tag {
  flex-shrink: 0;
  min-width: 96px;
  margin-top: 1px;
}
.bill-date {
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}
.diff-tags {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.diff-help {
  color: var(--el-text-color-placeholder);
  cursor: help;
}
.diff-short {
  white-space: normal;
  line-height: 1.4;
}
.nos {
  display: flex;
  flex-direction: column;
  gap: 1px;
  min-width: 0;
  font-size: 13px;
}
.nos__line {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  line-height: 1.5;
}
.nos__label {
  flex-shrink: 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.nos--mismatch .money {
  color: var(--el-color-danger);
  font-weight: 600;
}
.handle-desc {
  margin-bottom: 12px;
}
.handle-hint {
  margin-bottom: 14px;
  line-height: 1.6;
}
.open-order {
  margin-left: 10px;
}
</style>

<style>
.recon-hint-tip {
  max-width: 360px;
  line-height: 1.6;
}
</style>
