import { http } from './http'
import type {
  ChannelBrief,
  ChannelCopyInput,
  ChannelCreateInput,
  ChannelItem,
  ChannelListQuery,
  ChannelUpdateInput,
  IdResult,
  PageResult,
} from './types'

export const channelsApi = {
  list: (query: ChannelListQuery) => http.get<PageResult<ChannelItem>>('/channels', query),
  all: (appId?: number) => http.get<ChannelBrief[]>('/channels/all', { appId }),
  get: (id: number) => http.get<ChannelItem>(`/channels/${id}`),
  create: (data: ChannelCreateInput) => http.post<IdResult>('/channels', data),
  update: (id: number, data: ChannelUpdateInput) => http.put<null>(`/channels/${id}`, data),
  remove: (id: number) => http.delete<null>(`/channels/${id}`),
  copy: (id: number, data: ChannelCopyInput) => http.post<IdResult>(`/channels/${id}/copy`, data),
}
