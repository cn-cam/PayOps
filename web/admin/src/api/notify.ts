import { http } from './http'
import type {
  NotifyTaskItem,
  NotifyTaskListQuery,
  PageResult,
  UpstreamNotifyItem,
  UpstreamNotifyListQuery,
} from './types'

export const notifyTasksApi = {
  list: (query: NotifyTaskListQuery) => http.get<PageResult<NotifyTaskItem>>('/notify-tasks', query),
  retry: (id: number) => http.post<null>(`/notify-tasks/${id}/retry`),
}

export const upstreamNotifiesApi = {
  list: (query: UpstreamNotifyListQuery) =>
    http.get<PageResult<UpstreamNotifyItem>>('/upstream-notifies', query),
}
