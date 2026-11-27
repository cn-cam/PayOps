import { http } from './http'
import type { PageResult, RefundItem, RefundListQuery, RefundStatus, SyncResult } from './types'

export const refundsApi = {
  list: (query: RefundListQuery) => http.get<PageResult<RefundItem>>('/refunds', query),
  sync: (refundNo: string) =>
    http.post<SyncResult<RefundStatus>>(`/refunds/${encodeURIComponent(refundNo)}/sync`),
}
