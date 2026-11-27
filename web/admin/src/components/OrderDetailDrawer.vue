<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { CopyDocument, Refresh } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { ordersApi } from '@/api/orders'
import { refundsApi } from '@/api/refunds'
import { notifyTasksApi } from '@/api/notify'
import type { NotifyTaskItem, OrderDetail, RefundItem, UpstreamNotifyItem } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import ContentDialog, { type ContentSection } from './ContentDialog.vue'
import EnumTag from './EnumTag.vue'
import JsonViewer from './JsonViewer.vue'
import MoneyInput from './MoneyInput.vue'
import { confirmAction, copyText, validateForm } from '@/utils/common'
import {
  NOTIFY_STATUS,
  NOTIFY_TYPE,
  ORDER_STATUS,
  REFUND_SOURCE,
  REFUND_STATUS,
  VERIFIED,
  enumLabel,
} from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { formatTime } from '@/utils/time'

const props = defineProps<{
  modelValue: boolean
  orderNo: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  /** 订单发生了变更（同步 / 关闭 / 退款 / 模拟支付等），列表页可据此刷新 */
  changed: []
}>()

const auth = useAuthStore()
const meta = useMetaStore()

const loading = ref(false)
const order = ref<OrderDetail | null>(null)
const activeTab = ref('refunds')
const acting = ref('')

async function load() {
  if (!props.orderNo) return
  loading.value = true
  try {
    order.value = await ordersApi.get(props.orderNo)
  } finally {
    loading.value = false
  }
}

watch(
  () => [props.modelValue, props.orderNo] as const,
  ([visible, orderNo]) => {
    if (visible && orderNo) {
      if (order.value?.orderNo !== orderNo) {
        order.value = null
        activeTab.value = 'refunds'
      }
      meta.loadProviders()
      load()
    }
  },
  { immediate: true },
)

const refundable = computed(() => {
  const o = order.value
  return o ? Math.max(0, o.amount - (o.refundAmount ?? 0)) : 0
})

const providerMeta = computed(() => meta.getProvider(order.value?.provider))
const refundSupported = computed(() => providerMeta.value?.capabilities?.refund !== false)

const canSync = computed(() => !!order.value)
const canClose = computed(() => order.value?.status === 0)
const canRefund = computed(() => order.value?.status === 1 && refundable.value > 0)
const canRenotify = computed(() => order.value?.status === 1)
const canMockPay = computed(() => order.value?.provider === 'mock' && order.value?.status === 0)

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
    const res = await ordersApi.sync(props.orderNo)
    const text = res.message || (res.changed ? '订单状态已更新' : '订单状态无变化')
    if (res.changed) ElMessage.success(`${text}（当前：${enumLabel(ORDER_STATUS, res.status)}）`)
    else ElMessage.info(text)
    await afterChange()
  })
}

async function closeOrder() {
  if (!(await confirmAction('关闭后用户将无法继续支付该订单，确定关闭吗？', '关闭订单'))) return
  await run('close', async () => {
    await ordersApi.close(props.orderNo)
    ElMessage.success('订单已关闭')
    await afterChange()
  })
}

async function renotify() {
  if (!(await confirmAction('将重新向业务方发送该订单的支付结果通知，确定吗？', '重发通知', 'info'))) return
  await run('renotify', async () => {
    await ordersApi.renotify(props.orderNo)
    ElMessage.success('已重新发起通知')
    activeTab.value = 'notifyTasks'
    await afterChange()
  })
}

async function mockPay() {
  if (!(await confirmAction('将模拟上游支付成功回调，订单会变为支付成功并通知业务方，确定吗？', '模拟支付', 'info'))) return
  await run('mockPay', async () => {
    await ordersApi.mockPay(props.orderNo)
    ElMessage.success('已模拟支付成功')
    await afterChange()
  })
}

// ---------- 退款 ----------
const refundVisible = ref(false)
const refundFormRef = ref<FormInstance>()
const refundSaving = ref(false)
const refundForm = reactive({ amount: 0, reason: '' })

const refundRules: FormRules = {
  amount: [
    {
      validator: (_rule, _value, callback) => {
        if (refundForm.amount <= 0) callback(new Error('请输入退款金额'))
        else if (refundForm.amount > refundable.value) {
          callback(new Error(`退款金额不能超过可退金额 ${formatMoney(refundable.value)}`))
        } else callback()
      },
      trigger: 'change',
    },
  ],
  reason: [{ required: true, message: '请输入退款原因', trigger: 'blur' }],
}

function openRefund() {
  refundForm.amount = refundable.value
  refundForm.reason = ''
  refundVisible.value = true
}

async function submitRefund() {
  if (!(await validateForm(refundFormRef.value))) return
  refundSaving.value = true
  try {
    const res = await ordersApi.refund(props.orderNo, {
      amount: refundForm.amount,
      reason: refundForm.reason.trim(),
    })
    refundVisible.value = false
    ElMessage.success(`退款已提交：${res.refundNo}（${enumLabel(REFUND_STATUS, res.status)}）`)
    activeTab.value = 'refunds'
    await afterChange()
  } finally {
    refundSaving.value = false
  }
}

// ---------- 子表操作 ----------
const rowBusy = ref('')

async function syncRefund(row: RefundItem) {
  rowBusy.value = `refund:${row.refundNo}`
  try {
    const res = await refundsApi.sync(row.refundNo)
    ElMessage[res.changed ? 'success' : 'info'](res.message || (res.changed ? '退款状态已更新' : '退款状态无变化'))
    await afterChange()
  } finally {
    rowBusy.value = ''
  }
}

async function retryNotify(row: NotifyTaskItem) {
  rowBusy.value = `notify:${row.id}`
  try {
    await notifyTasksApi.retry(row.id)
    ElMessage.success('已重新发起通知')
    await load()
  } finally {
    rowBusy.value = ''
  }
}

const contentVisible = ref(false)
const contentTitle = ref('')
const contentSections = ref<ContentSection[]>([])

function viewNotify(row: NotifyTaskItem) {
  contentTitle.value = `业务通知 #${row.id}`
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

function viewUpstream(row: UpstreamNotifyItem) {
  contentTitle.value = `上游回调 #${row.id}`
  contentSections.value = [
    { label: '处理结果', content: row.result },
    { label: '请求报文', content: row.requestBody, extra: `${row.clientIp || '-'} · ${formatTime(row.createdAt)}` },
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
        <span class="drawer-head__title">订单详情</span>
        <span class="mono drawer-head__no">{{ orderNo }}</span>
        <el-button link type="primary" :icon="CopyDocument" @click="copyText(orderNo)" />
        <EnumTag v-if="order" :options="ORDER_STATUS" :value="order.status" effect="dark" />
      </div>
    </template>

    <div v-loading="loading" class="detail">
      <template v-if="order">
        <!-- 操作 -->
        <div class="actions">
          <el-button :icon="Refresh" @click="load">刷新</el-button>
          <template v-if="auth.canWrite">
            <el-tooltip content="主动向上游查询该订单的真实状态并同步（补单）" placement="top">
              <el-button v-if="canSync" type="primary" plain :loading="acting === 'sync'" @click="sync">
                同步（补单）
              </el-button>
            </el-tooltip>
            <el-button v-if="canMockPay" type="success" :loading="acting === 'mockPay'" @click="mockPay">
              模拟支付
            </el-button>
            <el-button v-if="canClose" type="warning" plain :loading="acting === 'close'" @click="closeOrder">
              关闭订单
            </el-button>
            <el-tooltip v-if="canRefund" :disabled="refundSupported" content="该支付类型不支持通过接口退款" placement="top">
              <span class="btn-wrap">
                <el-button type="danger" plain :disabled="!refundSupported" @click="openRefund">退款</el-button>
              </span>
            </el-tooltip>
            <el-button v-if="canRenotify" plain :loading="acting === 'renotify'" @click="renotify">
              重发通知
            </el-button>
          </template>
        </div>

        <!-- 金额概览 -->
        <div class="amounts">
          <div class="amounts__item">
            <div class="amounts__label">订单金额</div>
            <div class="amounts__value">{{ formatMoney(order.amount) }}</div>
          </div>
          <div class="amounts__item">
            <div class="amounts__label">已退款</div>
            <div class="amounts__value" :class="{ 'text-danger': order.refundAmount > 0 }">
              {{ formatMoney(order.refundAmount) }}
            </div>
          </div>
          <div class="amounts__item">
            <div class="amounts__label">可退金额</div>
            <div class="amounts__value">{{ order.status === 1 ? formatMoney(refundable) : '-' }}</div>
          </div>
          <div class="amounts__item">
            <div class="amounts__label">路由通道</div>
            <div class="amounts__value amounts__value--text" :title="order.passageName">
              {{ order.passageName || '-' }}
            </div>
          </div>
        </div>

        <el-alert
          v-if="order.errorCode || order.errorMsg"
          type="error"
          show-icon
          :closable="false"
          :title="`${order.errorCode ? `[${order.errorCode}] ` : ''}${order.errorMsg || '下单 / 支付失败'}`"
          class="block"
        />

        <el-descriptions title="基本信息" :column="2" border size="small" class="block">
          <el-descriptions-item label="订单号">
            <span class="mono">{{ order.orderNo }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="商户订单号">
            <span class="mono">{{ order.mchOrderNo || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="上游交易号">
            <span class="mono">{{ order.upstreamTradeNo || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="订单状态">
            <EnumTag :options="ORDER_STATUS" :value="order.status" size="small" />
          </el-descriptions-item>
          <el-descriptions-item label="应用">
            <span class="mono">{{ order.appCode }}</span>
            <span class="text-secondary text-small">（ID {{ order.appId }}）</span>
          </el-descriptions-item>
          <el-descriptions-item label="渠道">
            <span class="mono">{{ order.channelCode }}</span>
            <span class="text-secondary text-small">（ID {{ order.channelId }}）</span>
          </el-descriptions-item>
          <el-descriptions-item label="支付类型">
            {{ meta.providerName(order.provider) }}
            <span class="mono text-secondary">{{ order.provider }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="支付方式">
            {{ meta.methodName(order.payMethod) }}
            <span class="mono text-secondary">{{ order.payMethod }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="通道">
            {{ order.passageName || '-' }}
            <span class="text-secondary text-small">（ID {{ order.passageId }}）</span>
          </el-descriptions-item>
          <el-descriptions-item label="绑定 ID">{{ order.bindingId ?? '-' }}</el-descriptions-item>
          <el-descriptions-item label="金额 / 币种">
            <span class="money">{{ formatMoney(order.amount) }}</span>
            <span class="text-secondary currency">{{ order.currency }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="已退款">
            <span class="money">{{ formatMoney(order.refundAmount) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="商品标题" :span="2">{{ order.subject || '-' }}</el-descriptions-item>
          <el-descriptions-item label="商品描述" :span="2">{{ order.body || '-' }}</el-descriptions-item>
          <el-descriptions-item label="商品 ID">
            <span class="mono">{{ order.productId || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="用户 ID">
            <span class="mono">{{ order.userId || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="买家标识">
            <span class="mono">{{ order.buyerId || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="客户端 IP">
            <span class="mono">{{ order.clientIp || '-' }}</span>
          </el-descriptions-item>
        </el-descriptions>

        <el-descriptions title="时间与处理" :column="2" border size="small" class="block">
          <el-descriptions-item label="创建时间">{{ formatTime(order.createdAt) }}</el-descriptions-item>
          <el-descriptions-item label="支付时间">{{ formatTime(order.paidAt) }}</el-descriptions-item>
          <el-descriptions-item label="过期时间">{{ formatTime(order.expireAt) }}</el-descriptions-item>
          <el-descriptions-item label="关闭时间">{{ formatTime(order.closedAt) }}</el-descriptions-item>
          <el-descriptions-item label="更新时间">{{ formatTime(order.updatedAt) }}</el-descriptions-item>
          <el-descriptions-item label="主动查单">
            已查 {{ order.checkCount ?? 0 }} 次
            <span v-if="order.nextCheckAt" class="text-secondary text-small">
              ，下次 {{ formatTime(order.nextCheckAt) }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="业务通知地址" :span="2">
            <span class="mono break">{{ order.notifyUrl || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="前端返回地址" :span="2">
            <span class="mono break">{{ order.returnUrl || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="附加数据" :span="2">
            <span class="mono break">{{ order.attach || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="错误码">
            <span class="mono">{{ order.errorCode || '-' }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="错误信息">{{ order.errorMsg || '-' }}</el-descriptions-item>
        </el-descriptions>

        <div class="block">
          <div class="block__title">支付凭据（credential）</div>
          <JsonViewer :value="order.credential" empty-text="无支付凭据" max-height="260px" />
        </div>

        <el-tabs v-model="activeTab" class="block">
          <el-tab-pane :label="`退款记录（${order.refunds?.length ?? 0}）`" name="refunds">
            <el-table :data="order.refunds ?? []" size="small" empty-text="暂无退款">
              <el-table-column label="退款单号" min-width="190">
                <template #default="{ row }">
                  <span class="mono">{{ row.refundNo }}</span>
                </template>
              </el-table-column>
              <el-table-column label="金额" width="110" align="right">
                <template #default="{ row }">
                  <span class="money">{{ formatMoney(row.amount) }}</span>
                </template>
              </el-table-column>
              <el-table-column label="状态" width="100">
                <template #default="{ row }">
                  <EnumTag :options="REFUND_STATUS" :value="row.status" size="small" />
                </template>
              </el-table-column>
              <el-table-column label="来源" width="90">
                <template #default="{ row }">{{ enumLabel(REFUND_SOURCE, row.source) }}</template>
              </el-table-column>
              <el-table-column label="原因 / 错误" min-width="150" show-overflow-tooltip>
                <template #default="{ row }">
                  <span>{{ row.reason || '-' }}</span>
                  <span v-if="row.errorMsg" class="text-danger">（{{ row.errorMsg }}）</span>
                </template>
              </el-table-column>
              <el-table-column label="创建时间" width="160">
                <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
              </el-table-column>
              <el-table-column v-if="auth.canWrite" label="操作" width="70" fixed="right">
                <template #default="{ row }">
                  <el-button
                    v-if="row.status !== 1"
                    link
                    type="primary"
                    :loading="rowBusy === `refund:${row.refundNo}`"
                    @click="syncRefund(row as RefundItem)"
                  >
                    同步
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </el-tab-pane>

          <el-tab-pane :label="`业务通知（${order.notifyTasks?.length ?? 0}）`" name="notifyTasks">
            <el-table :data="order.notifyTasks ?? []" size="small" empty-text="暂无通知任务">
              <el-table-column label="ID" prop="id" width="80" />
              <el-table-column label="类型" width="70">
                <template #default="{ row }">{{ enumLabel(NOTIFY_TYPE, row.type) }}</template>
              </el-table-column>
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
              <el-table-column label="最近发送" width="160">
                <template #default="{ row }">{{ formatTime(row.lastTime) }}</template>
              </el-table-column>
              <el-table-column label="操作" :width="auth.canWrite ? 110 : 70" fixed="right">
                <template #default="{ row }">
                  <el-button link type="primary" @click="viewNotify(row as NotifyTaskItem)">查看</el-button>
                  <el-button
                    v-if="auth.canWrite"
                    link
                    type="primary"
                    :loading="rowBusy === `notify:${row.id}`"
                    @click="retryNotify(row as NotifyTaskItem)"
                  >
                    重发
                  </el-button>
                </template>
              </el-table-column>
            </el-table>
          </el-tab-pane>

          <el-tab-pane :label="`上游回调（${order.upstreamNotifies?.length ?? 0}）`" name="upstreamNotifies">
            <el-table :data="order.upstreamNotifies ?? []" size="small" empty-text="暂无上游回调">
              <el-table-column label="ID" prop="id" width="80" />
              <el-table-column label="类型" prop="kind" width="100" show-overflow-tooltip />
              <el-table-column label="验签" width="100">
                <template #default="{ row }">
                  <EnumTag :options="VERIFIED" :value="row.verified" size="small" />
                </template>
              </el-table-column>
              <el-table-column label="处理结果" prop="result" min-width="180" show-overflow-tooltip />
              <el-table-column label="来源 IP" width="130">
                <template #default="{ row }">
                  <span class="mono">{{ row.clientIp || '-' }}</span>
                </template>
              </el-table-column>
              <el-table-column label="时间" width="160">
                <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
              </el-table-column>
              <el-table-column label="操作" width="70" fixed="right">
                <template #default="{ row }">
                  <el-button link type="primary" @click="viewUpstream(row as UpstreamNotifyItem)">报文</el-button>
                </template>
              </el-table-column>
            </el-table>
          </el-tab-pane>
        </el-tabs>
      </template>
      <el-empty v-else-if="!loading" description="未获取到订单信息" />
    </div>

    <!-- 退款 -->
    <el-dialog v-model="refundVisible" title="订单退款" width="480px" :close-on-click-modal="false" append-to-body>
      <el-form ref="refundFormRef" :model="refundForm" :rules="refundRules" label-width="90px" @submit.prevent>
        <el-form-item label="订单号">
          <span class="mono">{{ orderNo }}</span>
        </el-form-item>
        <el-form-item label="可退金额">
          <span class="money">{{ formatMoney(refundable) }}</span>
          <span v-if="order" class="text-secondary text-small refund-hint">
            订单 {{ formatMoney(order.amount) }}，已退 {{ formatMoney(order.refundAmount) }}
          </span>
        </el-form-item>
        <el-form-item label="退款金额" prop="amount">
          <MoneyInput v-model="refundForm.amount" :max="refundable" />
          <el-button link type="primary" class="refund-all" @click="refundForm.amount = refundable">全额</el-button>
        </el-form-item>
        <el-form-item label="退款原因" prop="reason">
          <el-input v-model="refundForm.reason" type="textarea" :rows="2" maxlength="120" show-word-limit />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="refundVisible = false">取消</el-button>
        <el-button type="danger" :loading="refundSaving" @click="submitRefund">确认退款</el-button>
      </template>
    </el-dialog>

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
.btn-wrap {
  display: inline-flex;
}
.amounts {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
  margin-bottom: 16px;
}
.amounts__item {
  padding: 12px 14px;
  border-radius: 8px;
  background: #f8fafc;
}
.amounts__label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.amounts__value {
  margin-top: 4px;
  font-size: 20px;
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}
.amounts__value--text {
  font-size: 15px;
  line-height: 30px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.block {
  margin-bottom: 18px;
}
.block__title {
  margin-bottom: 10px;
  font-size: 16px;
  font-weight: 700;
}
.break {
  word-break: break-all;
}
.currency {
  margin-left: 6px;
}
.refund-hint {
  margin-left: 10px;
}
.refund-all {
  margin-left: 10px;
}
</style>
