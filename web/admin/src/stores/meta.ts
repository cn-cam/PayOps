import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { metaApi } from '@/api/meta'
import { appsApi } from '@/api/apps'
import type { AppBrief, ProviderMeta } from '@/api/types'

export interface MethodOption {
  code: string
  name: string
  provider: string
  providerName: string
}

/** 支付类型元数据（全部来自 /meta/providers，前端不写死任何 provider / 配置字段）与应用下拉数据 */
export const useMetaStore = defineStore('meta', () => {
  const providers = ref<ProviderMeta[]>([])
  const providersLoaded = ref(false)
  let providersPromise: Promise<void> | null = null

  const apps = ref<AppBrief[]>([])
  let appsPromise: Promise<void> | null = null

  const providerMap = computed(() => new Map(providers.value.map((p) => [p.code, p])))

  const methodMap = computed(() => {
    const map = new Map<string, MethodOption>()
    for (const p of providers.value) {
      for (const m of p.methods ?? []) {
        if (!map.has(m.code)) {
          map.set(m.code, { code: m.code, name: m.name, provider: p.code, providerName: p.name })
        }
      }
    }
    return map
  })

  function loadProviders(force = false): Promise<void> {
    if (providersLoaded.value && !force) return Promise.resolve()
    if (!providersPromise) {
      providersPromise = metaApi
        .providers()
        .then((list) => {
          providers.value = list ?? []
          providersLoaded.value = true
        })
        .finally(() => {
          providersPromise = null
        })
    }
    return providersPromise
  }

  function loadApps(): Promise<void> {
    if (!appsPromise) {
      appsPromise = appsApi
        .all()
        .then((list) => {
          apps.value = list ?? []
        })
        .finally(() => {
          appsPromise = null
        })
    }
    return appsPromise
  }

  function getProvider(code: string | null | undefined): ProviderMeta | undefined {
    return code ? providerMap.value.get(code) : undefined
  }

  function providerName(code: string | null | undefined): string {
    if (!code) return '-'
    return providerMap.value.get(code)?.name ?? code
  }

  function methodName(code: string | null | undefined): string {
    if (!code) return '-'
    return methodMap.value.get(code)?.name ?? code
  }

  function appName(id: number | null | undefined): string {
    if (id === null || id === undefined) return '-'
    const app = apps.value.find((a) => a.id === id)
    return app ? `${app.name}（${app.appCode}）` : String(id)
  }

  function reset() {
    apps.value = []
  }

  return {
    providers,
    providersLoaded,
    providerMap,
    methodMap,
    apps,
    loadProviders,
    loadApps,
    getProvider,
    providerName,
    methodName,
    appName,
    reset,
  }
})
