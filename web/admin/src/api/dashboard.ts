import { http } from './http'
import type {
  DashboardSummary,
  DistributionDimension,
  DistributionItem,
  SystemInfo,
  TrendPoint,
} from './types'

export const dashboardApi = {
  summary: () => http.get<DashboardSummary>('/dashboard/summary'),
  trend: (days: number) => http.get<TrendPoint[]>('/dashboard/trend', { days }),
  distribution: (dimension: DistributionDimension, days: number) =>
    http.get<DistributionItem[]>('/dashboard/distribution', { dimension, days }),
  systemInfo: () => http.get<SystemInfo>('/system/info'),
}
