import { http } from './http'
import type {
  PageResult,
  SubscriptionDetail,
  SubscriptionItem,
  SubscriptionListQuery,
  SubscriptionMockEvent,
  SubscriptionStatus,
  SyncResult,
} from './types'

const enc = encodeURIComponent

export const subscriptionsApi = {
  list: (query: SubscriptionListQuery) => http.get<PageResult<SubscriptionItem>>('/subscriptions', query),
  get: (subNo: string) => http.get<SubscriptionDetail>(`/subscriptions/${enc(subNo)}`),
  /** 向商店查询并同步：补记续费、同步续订开关、确认过期 / 撤销 */
  sync: (subNo: string) => http.post<SyncResult<SubscriptionStatus>>(`/subscriptions/${enc(subNo)}/sync`),
  /** 立即发起一次代扣（仅签约代扣订阅） */
  charge: (subNo: string) => http.post<SyncResult<SubscriptionStatus>>(`/subscriptions/${enc(subNo)}/charge`),
  /**
   * 服务端退订。类型不支持时后端返回带引导说明的业务错误，
   * 这里关闭统一的错误轻提示，由调用方用对话框完整展示。
   */
  cancel: (subNo: string) => http.post<null>(`/subscriptions/${enc(subNo)}/cancel`, undefined, { silent: true }),
  /** 仅 mock 通道：模拟上游订阅事件；productId 仅升级 / 降级等换商品事件需要 */
  mockEvent: (subNo: string, event: SubscriptionMockEvent, productId?: string) =>
    http.post<null>(
      `/subscriptions/${enc(subNo)}/mock-event`,
      productId ? { event, productId } : { event },
    ),
}
