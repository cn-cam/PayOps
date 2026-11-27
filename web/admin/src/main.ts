import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import dayjs from 'dayjs'
import 'dayjs/locale/zh-cn'
import 'element-plus/dist/index.css'
import './styles/index.css'

import App from './App.vue'
import router from './router'
import { setupHttp } from './api/http'
import { useAuthStore } from './stores/auth'
import { isHandledError } from './utils/common'

dayjs.locale('zh-cn')

const app = createApp(App)
const pinia = createPinia()
app.use(pinia)

const auth = useAuthStore(pinia)
setupHttp({
  getToken: () => auth.token,
  onUnauthorized: () => {
    // 首次进入时的 401 由路由守卫负责跳转（能保留目标地址）
    const wasVerified = auth.verified
    auth.clear()
    const current = router.currentRoute.value
    if (wasVerified && current.path !== '/login') {
      router.replace({ path: '/login', query: { redirect: current.fullPath } })
    }
  },
})

app.use(router)
app.use(ElementPlus, { locale: zhCn })

// 接口错误已在 http 层统一提示，这里只吞掉重复报错，其余照常输出
app.config.errorHandler = (err) => {
  if (isHandledError(err)) return
  console.error(err)
}
window.addEventListener('unhandledrejection', (event) => {
  if (isHandledError(event.reason)) event.preventDefault()
})

app.mount('#app')
