import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import type { Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    title?: string
    /** 允许访问的角色；省略表示全部登录用户 */
    roles?: Role[]
    /** 无需登录 */
    public?: boolean
    /** 详情类页面在侧边栏高亮的菜单路径 */
    activeMenu?: string
  }
}

const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/views/login/LoginView.vue'),
    meta: { title: '登录', public: true },
  },
  {
    path: '/',
    component: () => import('@/layouts/MainLayout.vue'),
    redirect: '/dashboard',
    children: [
      {
        path: 'dashboard',
        name: 'dashboard',
        component: () => import('@/views/dashboard/DashboardView.vue'),
        meta: { title: '仪表盘' },
      },
      {
        path: 'apps',
        name: 'apps',
        component: () => import('@/views/apps/AppsView.vue'),
        meta: { title: '应用管理' },
      },
      {
        path: 'channels',
        name: 'channels',
        component: () => import('@/views/channels/ChannelsView.vue'),
        meta: { title: '渠道管理' },
      },
      {
        path: 'channels/:id(\\d+)/bindings',
        name: 'channel-bindings',
        component: () => import('@/views/channels/BindingsView.vue'),
        meta: { title: '渠道通道配置', activeMenu: '/channels' },
      },
      {
        path: 'passages',
        name: 'passages',
        component: () => import('@/views/passages/PassagesView.vue'),
        meta: { title: '通道管理' },
      },
      {
        path: 'orders',
        name: 'orders',
        component: () => import('@/views/orders/OrdersView.vue'),
        meta: { title: '订单管理' },
      },
      {
        path: 'orders/test',
        name: 'orders-test',
        component: () => import('@/views/orders/TestOrderView.vue'),
        meta: { title: '测试下单', roles: ['admin', 'operator'] },
      },
      {
        path: 'subscriptions',
        name: 'subscriptions',
        component: () => import('@/views/subscriptions/SubscriptionsView.vue'),
        meta: { title: '订阅管理' },
      },
      {
        path: 'refunds',
        name: 'refunds',
        component: () => import('@/views/refunds/RefundsView.vue'),
        meta: { title: '退款管理' },
      },
      {
        path: 'recon',
        name: 'recon',
        component: () => import('@/views/recon/ReconView.vue'),
        meta: { title: '对账管理' },
      },
      {
        path: 'notify-tasks',
        name: 'notify-tasks',
        component: () => import('@/views/notify/NotifyTasksView.vue'),
        meta: { title: '业务通知' },
      },
      {
        path: 'upstream-notifies',
        name: 'upstream-notifies',
        component: () => import('@/views/notify/UpstreamNotifiesView.vue'),
        meta: { title: '上游回调日志' },
      },
      {
        path: 'users',
        name: 'users',
        component: () => import('@/views/system/UsersView.vue'),
        meta: { title: '用户管理', roles: ['admin'] },
      },
      {
        path: 'audit-logs',
        name: 'audit-logs',
        component: () => import('@/views/system/AuditLogsView.vue'),
        meta: { title: '操作审计' },
      },
      {
        path: '403',
        name: 'forbidden',
        component: () => import('@/views/error/ErrorView.vue'),
        props: { kind: '403' },
        meta: { title: '无权限' },
      },
      {
        path: ':pathMatch(.*)*',
        name: 'not-found',
        component: () => import('@/views/error/ErrorView.vue'),
        props: { kind: '404' },
        meta: { title: '页面不存在' },
      },
    ],
  },
]

const router = createRouter({
  history: createWebHistory('/'),
  routes,
  scrollBehavior: () => ({ top: 0 }),
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  const toLogin = { path: '/login', query: to.fullPath === '/' ? {} : { redirect: to.fullPath } }

  if (to.meta.public) {
    if (to.path === '/login' && auth.isLoggedIn) return '/'
    return true
  }

  if (!auth.isLoggedIn) return toLogin

  if (!auth.verified) {
    try {
      await auth.fetchMe()
    } catch {
      // 401 时 token 已被清除；其它错误（如网络异常）在有本地缓存用户时允许继续
      if (!auth.isLoggedIn || !auth.user) {
        auth.clear()
        return toLogin
      }
    }
  }

  if (!auth.hasRole(to.meta.roles)) return { path: '/403', replace: true }
  return true
})

router.afterEach((to) => {
  document.title = to.meta.title ? `${to.meta.title} - PayOps 管理后台` : 'PayOps 管理后台'
})

export default router
