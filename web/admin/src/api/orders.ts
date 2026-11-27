import { http } from './http'
import type {
  OrderDetail,
  OrderItem,
  OrderListQuery,
  OrderRefundInput,
  OrderRefundResult,
  OrderStatus,
  PageResult,
  SyncResult,
  TestOrderInput,
  TestOrderResult,
} from './types'

const enc = encodeURIComponent

export const ordersApi = {
  list: (query: OrderListQuery) => http.get<PageResult<OrderItem>>('/orders', query),
  get: (orderNo: string) => http.get<OrderDetail>(`/orders/${enc(orderNo)}`),
  sync: (orderNo: string) => http.post<SyncResult<OrderStatus>>(`/orders/${enc(orderNo)}/sync`),
  close: (orderNo: string) => http.post<null>(`/orders/${enc(orderNo)}/close`),
  refund: (orderNo: string, data: OrderRefundInput) =>
    http.post<OrderRefundResult>(`/orders/${enc(orderNo)}/refund`, data),
  renotify: (orderNo: string) => http.post<null>(`/orders/${enc(orderNo)}/renotify`),
  mockPay: (orderNo: string) => http.post<null>(`/orders/${enc(orderNo)}/mock-pay`),
  test: (data: TestOrderInput) => http.post<TestOrderResult>('/orders/test', data),
}
