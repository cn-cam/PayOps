import type {
  EnableStatus,
  NotifyStatus,
  NotifyType,
  OrderStatus,
  OrderType,
  ProviderCategory,
  ReconBizType,
  ReconDiffStatus,
  ReconDiffType,
  ReconStatus,
  RefundSource,
  RefundStatus,
  Role,
  SubscriptionMockEvent,
  SubscriptionStatus,
} from '@/api/types'

export type TagType = 'primary' | 'success' | 'warning' | 'danger' | 'info'

export interface EnumOption<V extends string | number | boolean = string | number | boolean> {
  value: V
  label: string
  type: TagType
}

export const ENABLE_STATUS: EnumOption<EnableStatus>[] = [
  { value: 1, label: '启用', type: 'success' },
  { value: 0, label: '停用', type: 'info' },
]

export const ORDER_STATUS: EnumOption<OrderStatus>[] = [
  { value: 0, label: '待支付', type: 'warning' },
  { value: 1, label: '支付成功', type: 'success' },
  { value: 2, label: '已关闭', type: 'info' },
  { value: 3, label: '支付失败', type: 'danger' },
]

export const REFUND_STATUS: EnumOption<RefundStatus>[] = [
  { value: 0, label: '处理中', type: 'warning' },
  { value: 1, label: '退款成功', type: 'success' },
  { value: 2, label: '退款失败', type: 'danger' },
]

export const NOTIFY_STATUS: EnumOption<NotifyStatus>[] = [
  { value: 0, label: '待通知', type: 'warning' },
  { value: 1, label: '通知成功', type: 'success' },
  { value: 2, label: '通知失败', type: 'danger' },
]

export const NOTIFY_TYPE: EnumOption<NotifyType>[] = [
  { value: 1, label: '支付', type: 'primary' },
  { value: 2, label: '退款', type: 'warning' },
  { value: 3, label: '订阅', type: 'success' },
]

export const ORDER_TYPE: EnumOption<OrderType>[] = [
  { value: 0, label: '一次性', type: 'info' },
  { value: 1, label: '订阅首购', type: 'primary' },
  { value: 2, label: '订阅续费', type: 'success' },
]

export const SUBSCRIPTION_STATUS: EnumOption<SubscriptionStatus>[] = [
  { value: 1, label: '生效中', type: 'success' },
  { value: 2, label: '已过期', type: 'info' },
  { value: 3, label: '已撤销', type: 'danger' },
]

/** autoRenew：false 表示已退订，当前周期结束后失效 */
export const AUTO_RENEW: EnumOption<boolean>[] = [
  { value: true, label: '自动续订', type: 'success' },
  { value: false, label: '已退订', type: 'warning' },
]

export const SUBSCRIPTION_MOCK_EVENTS: { value: SubscriptionMockEvent; label: string; hint: string }[] = [
  { value: 'renew', label: '续费一期', hint: '生成一笔续费单并顺延到期时间' },
  { value: 'cancel', label: '退订', hint: '关闭自动续订，当前周期结束后失效' },
  { value: 'resume', label: '恢复续订', hint: '重新打开自动续订' },
  { value: 'expire', label: '到期', hint: '订阅变为已过期' },
  { value: 'revoke', label: '撤销', hint: '退款 / 撤回，订阅立即失效' },
]

export const RECON_STATUS: EnumOption<ReconStatus>[] = [
  { value: 0, label: '待执行', type: 'info' },
  { value: 1, label: '已平账', type: 'success' },
  { value: 2, label: '有差异', type: 'warning' },
  { value: 3, label: '失败', type: 'danger' },
]

export const RECON_BIZ_TYPE: EnumOption<ReconBizType>[] = [
  { value: 1, label: '支付', type: 'primary' },
  { value: 2, label: '退款', type: 'warning' },
]

export const RECON_DIFF_TYPE: EnumOption<ReconDiffType>[] = [
  { value: 1, label: '上游有本地无', type: 'danger' },
  { value: 2, label: '本地有上游无', type: 'warning' },
  { value: 3, label: '金额不一致', type: 'warning' },
]

/** 差异类型的大白话解释与处理建议 */
export const RECON_DIFF_HINT: Record<ReconDiffType, { short: string; detail: string }> = {
  1: {
    short: '钱收到了但本地没记成功，通常是掉单',
    detail: '上游账单里有这笔，本地没有或没记成功。通常是回调丢失造成的掉单：到订单详情点「同步」补单，确认成功后再标记已处理。',
  },
  2: {
    short: '本地记了成功但上游账单没有，需核实',
    detail: '本地记了成功，上游账单里却没有这笔。可能是跨日入账，也可能是异常入账，需要到上游商户平台核实资金是否到账。',
  },
  3: {
    short: '两边都有但金额对不上',
    detail: '本地与上游都有这笔，但金额不同。请核对订单金额、优惠 / 部分退款等情况，以上游实际入账为准进行处理。',
  },
}

export const RECON_DIFF_STATUS: EnumOption<ReconDiffStatus>[] = [
  { value: 0, label: '待处理', type: 'warning' },
  { value: 1, label: '已处理', type: 'success' },
]

export const ROLES: EnumOption<Role>[] = [
  { value: 'admin', label: '管理员', type: 'danger' },
  { value: 'operator', label: '运营', type: 'primary' },
  { value: 'viewer', label: '只读', type: 'info' },
]

export const REFUND_SOURCE: EnumOption<RefundSource>[] = [
  { value: 'api', label: '业务发起', type: 'primary' },
  { value: 'admin', label: '后台发起', type: 'warning' },
  { value: 'upstream', label: '上游发起', type: 'info' },
]

export const VERIFIED: EnumOption<boolean>[] = [
  { value: true, label: '验签通过', type: 'success' },
  { value: false, label: '验签失败', type: 'danger' },
]

export const PROVIDER_CATEGORY: EnumOption<ProviderCategory>[] = [
  { value: 'direct', label: '直连支付', type: 'primary' },
  { value: 'store', label: '应用商店内购', type: 'warning' },
  { value: 'mock', label: '演示', type: 'info' },
]

export function findEnum<V extends string | number | boolean>(
  options: readonly EnumOption<V>[],
  value: unknown,
): EnumOption<V> | undefined {
  return options.find((o) => o.value === value)
}

export function enumLabel<V extends string | number | boolean>(
  options: readonly EnumOption<V>[],
  value: unknown,
): string {
  return findEnum(options, value)?.label ?? (value === null || value === undefined ? '-' : String(value))
}
