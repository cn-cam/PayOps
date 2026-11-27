import { onMounted, reactive, ref, type Ref, type UnwrapNestedRefs } from 'vue'
import type { PageQuery, PageResult } from '@/api/types'

export interface UsePagedTableOptions {
  /** 初始每页条数，默认 20 */
  pageSize?: number
  /** 是否在组件挂载后自动加载，默认 true */
  immediate?: boolean
}

/**
 * 分页表格通用逻辑：查询条件、分页、加载状态。
 *
 * @param fetcher     列表接口
 * @param createQuery 生成初始查询条件（重置时再次调用）
 * @param toParams    把界面查询条件转换为接口参数（不含 page / pageSize），省略则原样透传
 */
export function usePagedTable<T, Q extends object, P extends PageQuery = PageQuery>(
  fetcher: (params: P) => Promise<PageResult<T>>,
  createQuery: () => Q,
  toParams?: (query: Q) => Omit<P, 'page' | 'pageSize'>,
  options: UsePagedTableOptions = {},
) {
  const query = reactive(createQuery()) as UnwrapNestedRefs<Q>
  const items = ref([]) as Ref<T[]>
  const total = ref(0)
  const page = ref(1)
  const pageSize = ref(options.pageSize ?? 20)
  const loading = ref(false)
  let seq = 0

  async function load() {
    const current = ++seq
    loading.value = true
    try {
      const extra = toParams ? toParams(query as Q) : (query as unknown as Omit<P, 'page' | 'pageSize'>)
      const params = { ...extra, page: page.value, pageSize: pageSize.value } as P
      const res = await fetcher(params)
      if (current !== seq) return
      items.value = res?.items ?? []
      total.value = res?.total ?? 0
      // 删除最后一页的最后一条后，自动回退一页
      if (items.value.length === 0 && total.value > 0 && page.value > 1) {
        page.value = Math.max(1, Math.ceil(total.value / pageSize.value))
        await load()
      }
    } finally {
      if (current === seq) loading.value = false
    }
  }

  /** 以当前条件从第一页查询 */
  function search() {
    page.value = 1
    return load()
  }

  /** 重置查询条件并查询 */
  function reset() {
    Object.assign(query, createQuery())
    return search()
  }

  function onPageChange(p: number) {
    page.value = p
    return load()
  }

  function onSizeChange(size: number) {
    pageSize.value = size
    page.value = 1
    return load()
  }

  if (options.immediate !== false) {
    onMounted(() => load())
  }

  return { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange }
}
