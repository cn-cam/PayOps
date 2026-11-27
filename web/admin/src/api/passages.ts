import { http } from './http'
import type {
  IdResult,
  PageResult,
  PassageBrief,
  PassageCreateInput,
  PassageDetail,
  PassageItem,
  PassageListQuery,
  PassageTestResult,
  PassageUpdateInput,
} from './types'

export const passagesApi = {
  list: (query: PassageListQuery) => http.get<PageResult<PassageItem>>('/passages', query),
  all: (provider?: string) => http.get<PassageBrief[]>('/passages/all', { provider }),
  get: (id: number) => http.get<PassageDetail>(`/passages/${id}`),
  create: (data: PassageCreateInput) => http.post<IdResult>('/passages', data),
  update: (id: number, data: PassageUpdateInput) => http.put<null>(`/passages/${id}`, data),
  remove: (id: number) => http.delete<null>(`/passages/${id}`),
  test: (id: number) => http.post<PassageTestResult>(`/passages/${id}/test`),
}
