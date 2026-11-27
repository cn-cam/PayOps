<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ArrowDown, CopyDocument, Refresh } from '@element-plus/icons-vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { HttpError } from '@/api/http'
import { notifyTasksApi } from '@/api/notify'
import { subscriptionsApi } from '@/api/subscriptions'
import type { NotifyTaskItem, SubscriptionDetail, SubscriptionMockEvent } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import ContentDialog, { type ContentSection } from './ContentDialog.vue'
import EnumTag from './EnumTag.vue'
import { confirmAction, copyText } from '@/utils/common'
import {
  AUTO_RENEW,
  NOTIFY_STATUS,
  ORDER_STATUS,
  ORDER_TYPE,
  SUBSCRIPTION_MOCK_EVENTS,
  SUBSCRIPTION_STATUS,
  enumLabel,
} from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { expiryInfo } from '@/utils/subscription'
import { formatTime } from '@/utils/time'

const props = defineProps<{
  modelValue: boolean
  subNo: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  /** 订阅发生了变更（同步 / 退订 / 模拟事件），列表页可据此刷新 */
  changed: []
  /** 点击了首购单 / 续费单的订单号，由宿主打开订单详情 */
  'open-order': [orderNo: string]
}>()

const auth = useAuthStore()
const meta = useMetaStore()

const loading = ref(false)
const sub = ref<SubscriptionDetail | null>(null)
const activeTab = ref('orders')
const acting = ref('')

async function load() {
  if (!props.subNo) return
  loading.value = true
  try {
    sub.value = await subscriptionsApi.get(props.subNo)
  } finally {
    loading.value = false
  }
}

watch(
  () => [props.modelValue, props.subNo] as const,
  ([visible, subNo]) => {
    if (visible && subNo) {
      if (sub.value?.subNo !== subNo) {
        sub.value = null
        activeTab.value = 'orders'
      }
      meta.loadProviders()
      load()
    }
  },
  { immediate: true },
)

const providerMeta = computed(() => meta.getProvider(sub.value?.provider))
/** 元数据未加载到时按"支持"处理，交给后端判断 */
const cancelSupported = computed(() => providerMeta.value?.capabilities?.subscriptionCancel !== false)
const canCancel = computed(() => sub.value?.status === 1 && sub.value?.autoRenew === true)
const isMock = computed(() => sub.value?.provider === 'mock')
const expiry = computed(() => (sub.value ? expiryInfo(sub.value) : { level: 'ended' as const, text: '' }))

async function run(action: string, fn: () => Promise<void>) {
  acting.value = action
  try {
    await fn()
  } finally {
    acting.value = ''
  }
}

function afterChange() {
  emit('changed')
  return load()
}

function sync() {
  return run('sync', async () => {
    const res = await subscriptionsApi.sync(props.subNo)
    const text = res.message || (res.changed ? '订阅状态已更新' : '订阅状态无变化')
    if (res.changed) ElMessage.success(`${text}（当前：${enumLabel(SUBSCRIPTION_STATUS, res.status)}）`)
    else ElMessage.info(text)
    await afterChange()
  })
}

async function cancel() {
  const name = meta.providerName(sub.value?.provider)
  const tip = cancelSupported.value
    ? '退订后不再自动扣费，订阅在当前周期结束前仍然有效，到期后失效。确定退订吗？'
    : `「${name}」不支持服务端退订，只能由用户本人在应用商店的订阅管理里退订。继续的话后端会返回具体的引导说明，不会改动订阅。仍要尝试吗？`
  if (!(await confirmAction(tip, '退订'))) return
  await run('cancel', async () => {
    try {
      await subscriptionsApi.cancel(props.subNo)
    } catch (err) {
      // 该接口关闭了统一轻提示：后端的引导说明可能较长，用对话框完整展示
      if (err instanceof HttpError && err.httpStatus !== 401) {
        await ElMessageBox.alert(err.message, '无法退订', { type: 'warning', confirmButtonText: '知道了' }).catch(() => {})
        return
      }
      throw err
    }
    ElMessage.success('已退订，当前周期结束后失效')
    await afterChange()
  })
}

async function mockEvent(event: SubscriptionMockEvent) {
  const option = SUBSCRIPTION_MOCK_EVENTS.find((e) => e.value === event)
  await run('mock', async () => {
    await subscriptionsApi.mockEvent(props.subNo, event)
    ElMessage.success(`已模拟事件：${option?.label ?? event}`)
    if (event === 'renew') activeTab.value = 'orders'
    await afterChange()
  })
}

// ---------- 通知 ----------
const rowBusy = ref<number | null>(null)

async function retryNotify(row: NotifyTaskItem) {
  rowBusy.value = row.id
  try {
    await notifyTasksApi.retry(row.id)
    ElMessage.success('已重新发起通知')
    await load()
  } finally {
    rowBusy.value = null
  }
}

const contentVisible = ref(false)
const contentTitle = ref('')
const contentSections = ref<ContentSection[]>([])

function viewNotify(row: NotifyTaskItem) {
  contentTitle.value = `订阅通知 #${row.id}`
  contentSections.value = [
    { label: '通知地址', content: row.url },
    { label: '通知内容（payload）', content: row.payload },
    {
      label: '最近一次响应',
      content: row.lastResponse,
      extra: row.lastHttpStatus ? `HTTP ${row.lastHttpStatus} · ${formatTime(row.lastTime)}` : '尚未发送',
    },
  ]
  contentVisible.value = true
}
</script>

<template>
  <el-drawer
    :model-value="modelValue"
    size="900px"
    destroy-on-close
    @update:model-value="(v: boolean) => emit('update:modelValue', v)"
  >
    <template #header>
      <div class="drawer-head">
        <span class="drawer-head__title">订阅详情</span>
        <span class="mono drawer-head__no">{{ subNo }}</span>
        <el-button link type="primary" :icon="CopyDocument" @click="copyText(subNo)" />
        <template v-if="sub">
          <EnumTag :options="SUBSCRIPTION_STATUS" :value="sub.status" effect="dark" />
          <EnumTag v-if="sub.status === 1" :options="AUTO_RENEW" :value="sub.autoRenew" effect="plain" />
        </template>
      </div>
    </template>

    <div v-loading="loading" class="detail">
      <template v-if="sub">
        <!-- 操作 -->
        <div class="actions">
          <el-button :icon="Refresh" @click="load">刷新</el-button>
          <template v-if="auth.canWrite">
            <el-tooltip content="向商店查询并同步：补记续费、同步续订开关、确认过期 / 撤销" placement="top">
              <el-button type="primary" plain :loading="acting === 'sync'" @click="sync">同步</el-button>
            </el-tooltip>
            <el-button v-if="canCancel" type="danger" plain :loading="acting === 'cancel'" @click="cancel">
              退订
            </el-button>
            <el-dropdown v-if="isMock" trigger="click" @command="mockEvent">
              <el-button type="success" plain :loading="acting === 'mock'">
                模拟事件
                <el-icon class="el-icon--right"><ArrowDown /></el-icon>
              </el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item v-for="e in SUBSCRIPTION_MOCK_EVENTS" :key="e.value" :command="e.value">
                    <div class="mock-item">
                      <span class="mock-item__label">{{ e.label }}</span>
                      <span class="mock-item__hint">{{ e.hint }}</span>
                    </div>
                  </el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </div>

        <el-alert
          v-if="sub.status === 1 && !sub.autoRenew"
          type="warning"
          show-icon
          :closable="false"
          :title="`已退订${sub.cancelAt ? `（${formatTime(sub.cancelAt)}）` : ''}：不再自动扣费，订阅在 ${formatTime(sub.expiresAt)} 前仍然有效。`"
          class="block"
        />
        <el-alert
          v-if="canCancel && !cancelSupported && auth.canWrite"
          type="info"
          show-icon
          :closable="false"
          :title="`「${meta.providerName(sub.provider)}」不支持服务端退订，只能由用户在应用商店的订阅管理里退订。`"
          class="block"
        />

        <!-- 概览 -->
        <div class="summary">
          <div class="summary__item">
            <div class="summary__label">最近一期金额</div>
            <div class="summary__value">{{ formatMoney(sub.amount) }}</div>
          </div>
          <div class="summary__item">
            <div class="summary__label">续订次数</div>
            <div class="summary__value">{{ sub.renewCount ?? 0 }}</div>
          </div>
          <div class="summary__item summary__item--wide">
            <div class="summary__label">当前周期到期</div>
            <div class="summary__value summary__value--text">{{ formatTime(sub.expiresAt) }}</div>
            <div v-if="expiry.text" class="summary__hint" :class="`expiry--${expiry.level}`">{{ expiry.text }}</div>
          </div>
          <div class="summary__item summary__item--wide">
            <div class="summary__label">通道</div>
            <div class="summary__value summary__value--text" :title="sub.passageName">
              {{ sub.passageName || '-' }}
            </div>
            <div class="summary__hint">{{ meta.providerName(sub.provider) }}</div>
          </div>
        </div>

        <el-descriptions title="订阅信息" :column="2" border size="small" class="block">
          <el-descriptions-item label="订阅号">
            <span class="mono">{{ sub.subNo }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="上游订阅标识">
            <span class="mono break">{{ sub.upstreamSubId || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="状态">
            <EnumTag :options="SUBSCRIPTION_STATUS" :value="sub.status" size="small" />
          </el-descriptions-item>
          <el-descriptions-item label="续订">
            <EnumTag :options="AUTO_RENEW" :value="sub.autoRenew" size="small" effect="plain" />
            <span v-if="sub.cancelAt" class="text-secondary text-small gap-left">
              退订于 {{ formatTime(sub.cancelAt) }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="应用">
            <span class="mono">{{ sub.appCode }}</span>
            <span class="text-secondary text-small">（ID {{ sub.appId }}）</span>
          </el-descriptions-item>
          <el-descriptions-item label="渠道">
            <span class="mono">{{ sub.channelCode }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="支付类型">
            {{ meta.providerName(sub.provider) }}
            <span class="mono text-secondary">{{ sub.provider }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="通道">
            {{ sub.passageName || '-' }}
            <span class="text-secondary text-small">（ID {{ sub.passageId }}）</span>
          </el-descriptions-item>
          <el-descriptions-item label="商品 ID">
            <span class="mono">{{ sub.productId || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="用户 ID">
            <span class="mono">{{ sub.userId || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="最近一期金额">
            <span class="money">{{ formatMoney(sub.amount) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="续订次数">{{ sub.renewCount ?? 0 }}</el-descriptions-item>
          <el-descriptions-item label="当前周期开始">{{ formatTime(sub.periodStart) }}</el-descriptions-item>
          <el-descriptions-item label="当前周期到期">
            <span :class="`expiry--${expiry.level}`">{{ formatTime(sub.expiresAt) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="首购单">
            <el-link
              v-if="sub.firstOrderNo"
              type="primary"
              underline="never"
              class="mono"
              @click="emit('open-order', sub.firstOrderNo)"
            >
              {{ sub.firstOrderNo }}
            </el-link>
            <span v-else>-</span>
          </el-descriptions-item>
          <el-descriptions-item label="最近一期订单">
            <el-link
              v-if="sub.lastOrderNo"
              type="primary"
              underline="never"
              class="mono"
              @click="emit('open-order', sub.lastOrderNo)"
            >
              {{ sub.lastOrderNo }}
            </el-link>
            <span v-else>-</span>
          </el-descriptions-item>
          <el-descriptions-item label="创建时间">{{ formatTime(sub.createdAt) }}</el-descriptions-item>
          <el-descriptions-item label="更新时间">{{ formatTime(sub.updatedAt) }}</el-descriptions-item>
          <el-descriptions-item label="主动查询">
            已查 {{ sub.checkCount ?? 0 }} 次
            <span v-if="sub.nextCheckAt" class="text-secondary text-small">
              ，下次 {{ formatTime(sub.nextCheckAt) }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="附加数据">
            <span class="mono break">{{ sub.attach || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="业务通知地址" :span="2">
            <span class="mono break">{{ sub.notifyUrl || '-' }}</span>
          </el-descriptions-item>
        </el-descriptions>

        <el-tabs v-model="activeTab" class="block">
          <el-tab-pane :label="`订单（${sub.orders?.length ?? 0}）`" name="orders">
            <el-table :data="sub.orders ?? []" size="small" empty-text="暂无订单">
              <el-table-column label="订单号" min-width="200">
                <template #default="{ row }">
                  <el-link type="primary" underline="never" class="mono" @click="emit('open-order', row.orderNo)">
                    {{ row.orderNo }}
                  </el-link>
                </template>
              </el-table-column>
              <el-table-column label="类型" width="100">
                <template #default="{ row }">
                  <EnumTag :options="ORDER_TYPE" :value="row.orderType" size="small" effect="plain" />
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
              <el-table-column label="状态" width="100">
                <template #default="{ row }">
                  <EnumTag :options="ORDER_STATUS" :value="row.status" size="small" />
                </template>
              </el-table-column>
              <el-table-column label="上游交易号" min-width="150" show-overflow-tooltip>
                <template #default="{ row }">
                  <span class="mono">{{ row.upstreamTradeNo || '-' }}</span>
                </template>
              </el-table-column>
              <el-table-column label="支付时间" width="160">
                <template #default="{ row }">{{ formatTime(row.paidAt || row.createdAt) }}</template>
              </el-table-column>
            </el-table>
            <div class="form-help">每期续费都会生成一笔「订阅续费」订单（由上游扣款事件生成，生来已支付）；退款请到对应订单详情操作。</div>
          </el-tab-pane>

          <el-tab-pane :label="`订阅通知（${sub.notifyTasks?.length ?? 0}）`" name="notifyTasks">
            <el-table :data="sub.notifyTasks ?? []" size="small" empty-text="暂无通知任务">
              <el-table-column label="ID" prop="id" width="80" />
              <el-table-column label="状态" width="100">
                <template #default="{ row }">
                  <EnumTag :options="NOTIFY_STATUS" :value="row.status" size="small" />
                </template>
              </el-table-column>
              <el-table-column label="次数" width="80" align="center">
                <template #default="{ row }">{{ row.attempts }} / {{ row.maxAttempts }}</template>
              </el-table-column>
              <el-table-column label="HTTP" width="70" align="center">
                <template #default="{ row }">{{ row.lastHttpStatus || '-' }}</template>
              </el-table-column>
              <el-table-column label="通知地址" min-width="180" show-overflow-tooltip>
                <template #default="{ row }">
                  <span class="mono">{{ row.url }}</span>
                </template>
              </el-table-column>
              <el-table-column label="创建时间" width="160">
                <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
              </el-table-column>
              <el-table-column label="操作" :width="auth.canWrite ? 110 : 70" fixed="right">
                <template #default="{ row }">
                  <el-button link type="primary" @click="viewNotify(row as NotifyTaskItem)">查看</el-button>
                  <el-button
                    v-if="auth.canWrite"
                    link
                    type="primary"
                    :loading="rowBusy === row.id"
                    @click="retryNotify(row as NotifyTaskItem)"
                  >
                    重发
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
            <div class="form-help">订阅状态每次变化（续费、退订、恢复、到期、撤销）都会通知业务方一次。</div>
          </el-tab-pane>
        </el-tabs>
      </template>
      <el-empty v-else-if="!loading" description="未获取到订阅信息" />
    </div>

    <ContentDialog v-model="contentVisible" :title="contentTitle" :sections="contentSections" />
  </el-drawer>
</template>

<style scoped>
.drawer-head {
  display: flex;
  align-items: center;
  gap: 10px;
}
.drawer-head__title {
  font-size: 16px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}
.drawer-head__no {
  font-size: 13.5px;
  color: var(--el-text-color-regular);
}
.detail {
  min-height: 200px;
}
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 16px;
}
.actions .el-button + .el-button {
  margin-left: 0;
}
.mock-item {
  display: flex;
  flex-direction: column;
  line-height: 1.4;
  padding: 2px 0;
}
.mock-item__label {
  font-weight: 600;
}
.mock-item__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.summary {
  display: grid;
  grid-template-columns: 1fr 1fr 1.6fr 1.6fr;
  gap: 12px;
  margin-bottom: 16px;
}
.summary__item {
  min-width: 0;
  padding: 12px 14px;
  border-radius: 8px;
  background: #f8fafc;
}
.summary__label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.summary__value {
  margin-top: 4px;
  font-size: 20px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}
.summary__value--text {
  font-size: 15px;
  line-height: 30px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.summary__hint {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.summary__hint.expiry--soon {
  color: var(--el-color-warning);
}
.summary__hint.expiry--expired {
  color: var(--el-color-danger);
}
.block {
  margin-bottom: 18px;
}
.break {
  word-break: break-all;
}
.gap-left {
  margin-left: 8px;
}
</style>
