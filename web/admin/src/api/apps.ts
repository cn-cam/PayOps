import { http } from './http'
import type {
  AppBrief,
  AppCreateInput,
  AppCreateResult,
  AppItem,
  AppListQuery,
  AppSecretResult,
  AppUpdateInput,
  PageResult,
} from './types'

export const appsApi = {
  list: (query: AppListQuery) => http.get<PageResult<AppItem>>('/apps', query),
  all: () => http.get<AppBrief[]>('/apps/all'),
  create: (data: AppCreateInput) => http.post<AppCreateResult>('/apps', data),
  update: (id: number, data: AppUpdateInput) => http.put<null>(`/apps/${id}`, data),
  remove: (id: number) => http.delete<null>(`/apps/${id}`),
  secret: (id: number) => http.get<AppSecretResult>(`/apps/${id}/secret`),
  resetSecret: (id: number) => http.post<AppSecretResult>(`/apps/${id}/reset-secret`),
}
