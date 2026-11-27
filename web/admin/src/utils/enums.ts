import type {
  EnableStatus,
  NotifyStatus,
  NotifyType,
  OrderStatus,
  ProviderCategory,
  RefundSource,
  RefundStatus,
  Role,
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
