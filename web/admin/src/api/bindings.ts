import { http } from './http'
import type {
  BindingCreateInput,
  BindingInput,
  BindingItem,
  IdResult,
  RouteSimulateInput,
  RouteSimulateResult,
} from './types'

export const bindingsApi = {
  list: (channelId: number) => http.get<BindingItem[]>(`/channels/${channelId}/bindings`),
  create: (channelId: number, data: BindingCreateInput) =>
    http.post<IdResult>(`/channels/${channelId}/bindings`, data),
  update: (id: number, data: BindingInput) => http.put<null>(`/bindings/${id}`, data),
  remove: (id: number) => http.delete<null>(`/bindings/${id}`),
  resetBreaker: (id: number) => http.post<null>(`/bindings/${id}/reset-breaker`),
  simulate: (channelId: number, data: RouteSimulateInput) =>
    http.post<RouteSimulateResult>(`/channels/${channelId}/route-simulate`, data),
}

/** 从 BindingItem 中提取可回传给 PUT 的 BindingInput */
export function toBindingInput(item: BindingInput): BindingInput {
  return {
    methods: [...(item.methods ?? [])],
    weight: item.weight,
    priority: item.priority,
    minAmount: item.minAmount,
    maxAmount: item.maxAmount,
    dailyLimitAmount: item.dailyLimitAmount,
    dailyLimitCount: item.dailyLimitCount,
    startTime: item.startTime ?? '',
    endTime: item.endTime ?? '',
    status: item.status,
    remark: item.remark ?? '',
  }
}
