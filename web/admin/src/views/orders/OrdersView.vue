<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { channelsApi } from '@/api/channels'
import { ordersApi } from '@/api/orders'
import { passagesApi } from '@/api/passages'
import type { ChannelBrief, OrderItem, OrderListQuery, OrderStatus, OrderType, PassageBrief } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import AppSelect from '@/components/AppSelect.vue'
import EnumTag from '@/components/EnumTag.vue'
import DetailDrawers from '@/components/DetailDrawers.vue'
import ProviderSelect from '@/components/ProviderSelect.vue'
import TablePager from '@/components/TablePager.vue'
import TimeRangePicker from '@/components/TimeRangePicker.vue'
import { ORDER_STATUS, ORDER_TYPE, enumLabel } from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { formatTime, rangeToQuery, type DateRange } from '@/utils/time'

const route = useRoute()
const auth = useAuthStore()
const meta = useMetaStore()

interface Query {
  orderNo: string
  mchOrderNo: string
  upstreamTradeNo: string
  appId: number | undefined
  channelId: number | undefined
  passageId: number | undefined
  provider: string | undefined
  status: OrderStatus | undefined
  orderType: OrderType | undefined
  subNo: string
  userId: string
  timeRange: DateRange
}

function queryString(key: string): string {
  const v = route.query[key]
  return typeof v === 'string' ? v : ''
}

function queryNumber(key: string): number | undefined {
  const text = queryString(key)
  if (text === '') return undefined
  const n = Number(text)
  return Number.isFinite(n) ? n : undefined
}

let firstQuery = true

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<OrderItem, Query, OrderListQuery>(
    ordersApi.list,
    () => {
      // 仅首次使用地址栏带入的条件（来自其它页面的跳转）
      const first = firstQuery
      firstQuery = false
      return {
        orderNo: first ? queryString('orderNo') : '',
        mchOrderNo: '',
        upstreamTradeNo: '',
        appId: first ? queryNumber('appId') : undefined,
        channelId: undefined,
        passageId: first ? queryNumber('passageId') : undefined,
        provider: first ? queryString('provider') || undefined : undefined,
        status: first ? (queryNumber('status') as OrderStatus | undefined) : undefined,
        orderType: first ? (queryNumber('orderType') as OrderType | undefined) : undefined,
        subNo: first ? queryString('subNo') : '',
        userId: '',
        timeRange: null,
      }
    },
    (q) => ({
      orderNo: q.orderNo,
      mchOrderNo: q.mchOrderNo,
      upstreamTradeNo: q.upstreamTradeNo,
      appId: q.appId,
      channelId: q.channelId,
      passageId: q.passageId,
      provider: q.provider,
      status: q.status,
      orderType: q.orderType,
      subNo: q.subNo,
      userId: q.userId,
      ...rangeToQuery(q.timeRange),
    }),
  )

// ---------- 联动下拉 ----------
const channels = ref<ChannelBrief[]>([])
const passages = ref<PassageBrief[]>([])

async function loadChannels() {
  channels.value = []
  if (query.appId === undefined) return
  const appId = query.appId
  const list = await channelsApi.all(appId)
  if (query.appId === appId) channels.value = list ?? []
}

async function loadPassages() {
  const provider = query.provider
  const list = await passagesApi.all(provider)
  if (query.provider === provider) passages.value = list ?? []
}

function onAppChange() {
  query.channelId = undefined
  loadChannels()
  search()
}

function onProviderChange() {
  query.passageId = undefined
  loadPassages()
  search()
}

function onReset() {
  channels.value = []
  reset()
  loadPassages()
}

onMounted(() => {
  meta.loadProviders()
  loadChannels()
  loadPassages()
})

// ---------- 详情 / 快捷操作 ----------
const drawers = ref<InstanceType<typeof DetailDrawers>>()

function openDetail(orderNo: string) {
  drawers.value?.openOrder(orderNo)
}

function openSubscription(subNo: string) {
  drawers.value?.openSubscription(subNo)
}

const syncingNo = ref('')

async function quickSync(row: OrderItem) {
  syncingNo.value = row.orderNo
  try {
    const res = await ordersApi.sync(row.orderNo)
    const text = res.message || (res.changed ? '订单状态已更新' : '订单状态无变化')
    if (res.changed) {
      ElMessage.success(`${text}（当前：${enumLabel(ORDER_STATUS, res.status)}）`)
      load()
    } else {
      ElMessage.info(text)
    }
  } finally {
    syncingNo.value = ''
  }
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline label-width="84px" @submit.prevent="search">
        <el-form-item label="订单号">
          <el-input v-model="query.orderNo" placeholder="平台订单号" clearable style="width: 220px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="商户订单号">
          <el-input
            v-model="query.mchOrderNo"
            placeholder="业务方订单号"
            clearable
            style="width: 220px"
            @keyup.enter="search"
          />
        </el-form-item>
        <el-form-item label="上游交易号">
          <el-input
            v-model="query.upstreamTradeNo"
            placeholder="微信 / 支付宝等交易号"
            clearable
            style="width: 220px"
            @keyup.enter="search"
          />
        </el-form-item>
        <el-form-item label="用户 ID">
          <el-input v-model="query.userId" placeholder="userId" clearable style="width: 220px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="应用">
          <AppSelect v-model="query.appId" width="220px" @change="onAppChange" />
        </el-form-item>
        <el-form-item label="渠道">
          <el-select
            v-model="query.channelId"
            :placeholder="query.appId === undefined ? '请先选择应用' : '全部渠道'"
            :disabled="query.appId === undefined"
            clearable
            filterable
            style="width: 220px"
            @change="search"
          >
            <el-option v-for="c in channels" :key="c.id" :value="c.id" :label="`${c.name}（${c.code}）`" />
          </el-select>
        </el-form-item>
        <el-form-item label="支付类型">
          <ProviderSelect v-model="query.provider" width="220px" @change="onProviderChange" />
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
            <el-option v-for="p in passages" :key="p.id" :value="p.id" :label="p.name" />
          </el-select>
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 220px" @change="search">
            <el-option v-for="s in ORDER_STATUS" :key="s.value" :value="s.value" :label="s.label" />
          </el-select>
        </el-form-item>
        <el-form-item label="订单类型">
          <el-select v-model="query.orderType" placeholder="全部" clearable style="width: 220px" @change="search">
            <el-option v-for="s in ORDER_TYPE" :key="s.value" :value="s.value" :label="s.label" />
          </el-select>
        </el-form-item>
        <el-form-item label="订阅号">
          <el-input
            v-model="query.subNo"
            placeholder="平台订阅号"
            clearable
            style="width: 220px"
            @keyup.enter="search"
          />
        </el-form-item>
        <el-form-item label="创建时间">
          <TimeRangePicker v-model="query.timeRange" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Search" @click="search">查询</el-button>
          <el-button :icon="Refresh" @click="onReset">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          订单列表
          <span class="toolbar__sub">共 {{ total }} 条</span>
        </div>
        <div class="toolbar__actions">
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="orderNo">
        <el-table-column label="订单号 / 商户订单号" min-width="210">
          <template #default="{ row }">
            <div class="cell-stack">
              <el-link type="primary" underline="never" class="mono" @click="openDetail(row.orderNo)">
                {{ row.orderNo }}
              </el-link>
              <span class="mono text-secondary">{{ row.mchOrderNo || '-' }}</span>
              <span v-if="row.subNo" class="text-small sub-line">
                <span class="text-secondary">订阅</span>
                <el-link type="primary" underline="never" class="mono" @click="openSubscription(row.subNo)">
                  {{ row.subNo }}
                </el-link>
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="应用 / 渠道" min-width="120">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="mono">{{ row.appCode }}</span>
              <span class="mono text-secondary">{{ row.channelCode }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="通道 / 支付方式" min-width="170">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="ellipsis" :title="row.passageName">{{ row.passageName || '-' }}</span>
              <span class="text-secondary text-small ellipsis">
                {{ meta.providerName(row.provider) }} · {{ meta.methodName(row.payMethod) }}
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="商品 / 用户" min-width="140">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="ellipsis" :title="row.subject">{{ row.subject || '-' }}</span>
              <span class="mono text-secondary ellipsis" :title="row.userId">{{ row.userId || '-' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="金额" width="110" align="right">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="money">{{ formatMoney(row.amount) }}</span>
              <span v-if="row.refundAmount > 0" class="text-danger text-small">
                退 {{ formatMoney(row.refundAmount) }}
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态 / 类型" width="100" align="center">
          <template #default="{ row }">
            <div class="cell-stack cell-stack--tight">
              <EnumTag :options="ORDER_STATUS" :value="row.status" />
              <EnumTag :options="ORDER_TYPE" :value="row.orderType ?? 0" size="small" effect="plain" />
            </div>
          </template>
        </el-table-column>
        <el-table-column label="创建 / 支付时间" width="165">
          <template #default="{ row }">
            <div class="cell-stack">
              <span>{{ formatTime(row.createdAt) }}</span>
              <span class="text-secondary">{{ formatTime(row.paidAt) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="操作" :width="auth.canWrite ? 110 : 70" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openDetail(row.orderNo)">详情</el-button>
            <el-button
              v-if="auth.canWrite && row.status === 0"
              link
              type="primary"
              :loading="syncingNo === row.orderNo"
              @click="quickSync(row as OrderItem)"
            >
              同步
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
    </el-card>

    <DetailDrawers ref="drawers" @changed="load" />
  </div>
</template>

<style scoped>
.sub-line {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.sub-line .el-link {
  font-size: 12px;
}
</style>
