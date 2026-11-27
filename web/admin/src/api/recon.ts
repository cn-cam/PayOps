import { http } from './http'
import type {
  IdResult,
  PageResult,
  ReconBatchCreateInput,
  ReconBatchItem,
  ReconBatchListQuery,
  ReconDiffHandleInput,
  ReconDiffItem,
  ReconDiffListQuery,
} from './types'

export const reconApi = {
  batches: (query: ReconBatchListQuery) => http.get<PageResult<ReconBatchItem>>('/recon/batches', query),
  /** 手动发起；已存在则重置并重跑 */
  createBatch: (data: ReconBatchCreateInput) => http.post<IdResult>('/recon/batches', data),
  rerunBatch: (id: number) => http.post<null>(`/recon/batches/${id}/rerun`),
  diffs: (query: ReconDiffListQuery) => http.get<PageResult<ReconDiffItem>>('/recon/diffs', query),
  /** 标记为已处理，remark 必填 */
  handleDiff: (id: number, data: ReconDiffHandleInput) => http.post<null>(`/recon/diffs/${id}/handle`, data),
}
