import { http } from './http'
import type { ProviderMeta } from './types'

export const metaApi = {
  providers: () => http.get<ProviderMeta[]>('/meta/providers'),
}
