import { http } from './http'
import type {
  AdminUser,
  AuditLogItem,
  AuditLogQuery,
  IdResult,
  PageResult,
  UserCreateInput,
  UserUpdateInput,
} from './types'

export const usersApi = {
  list: () => http.get<AdminUser[]>('/users'),
  create: (data: UserCreateInput) => http.post<IdResult>('/users', data),
  update: (id: number, data: UserUpdateInput) => http.put<null>(`/users/${id}`, data),
  resetPassword: (id: number, password: string) =>
    http.post<null>(`/users/${id}/reset-password`, { password }),
  remove: (id: number) => http.delete<null>(`/users/${id}`),
}

export const auditLogsApi = {
  list: (query: AuditLogQuery) => http.get<PageResult<AuditLogItem>>('/audit-logs', query),
}
