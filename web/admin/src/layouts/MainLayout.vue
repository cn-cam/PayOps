<script setup lang="ts">
import { computed, ref, type Component } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  ArrowDown,
  Bell,
  Calendar,
  Connection,
  DataLine,
  DocumentChecked,
  Expand,
  Fold,
  Grid,
  Key,
  List,
  Memo,
  Promotion,
  RefreshLeft,
  Share,
  SwitchButton,
  Tickets,
  User,
  UserFilled,
} from '@element-plus/icons-vue'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import { ROLES, enumLabel } from '@/utils/enums'
import { confirmAction } from '@/utils/common'
import ChangePasswordDialog from '@/components/ChangePasswordDialog.vue'

interface MenuItem {
  path: string
  icon: Component
}

interface MenuGroup {
  title: string
  items: MenuItem[]
}

const menuGroups: MenuGroup[] = [
  { title: '概览', items: [{ path: '/dashboard', icon: DataLine }] },
  {
    title: '支付配置',
    items: [
      { path: '/apps', icon: Grid },
      { path: '/channels', icon: Share },
      { path: '/passages', icon: Connection },
    ],
  },
  {
    title: '交易',
    items: [
      { path: '/orders', icon: Tickets },
      { path: '/orders/test', icon: Promotion },
      { path: '/subscriptions', icon: Calendar },
      { path: '/refunds', icon: RefreshLeft },
      { path: '/recon', icon: DocumentChecked },
    ],
  },
  {
    title: '通知',
    items: [
      { path: '/notify-tasks', icon: Bell },
      { path: '/upstream-notifies', icon: List },
    ],
  },
  {
    title: '系统',
    items: [
      { path: '/users', icon: User },
      { path: '/audit-logs', icon: Memo },
    ],
  },
]

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const meta = useMetaStore()

// 窄屏（如 1280）默认收起侧边栏，把宽度让给表格
const collapsed = ref(window.innerWidth < 1400)
const passwordVisible = ref(false)

/** 菜单标题与角色限制统一取自路由 meta */
const visibleGroups = computed(() =>
  menuGroups
    .map((group) => ({
      title: group.title,
      items: group.items
        .map((item) => {
          const resolved = router.resolve(item.path)
          return { ...item, title: resolved.meta.title ?? item.path, roles: resolved.meta.roles }
        })
        .filter((item) => auth.hasRole(item.roles)),
    }))
    .filter((group) => group.items.length > 0),
)

const activeMenu = computed(() => route.meta.activeMenu ?? route.path)
const displayName = computed(() => auth.user?.displayName || auth.user?.username || '-')

async function onCommand(command: string) {
  if (command === 'password') {
    passwordVisible.value = true
  } else if (command === 'logout') {
    if (!(await confirmAction('确定退出登录吗？', '退出登录', 'info'))) return
    logout()
  }
}

function logout() {
  auth.clear()
  meta.reset()
  router.replace('/login')
}
</script>

<template>
  <el-container class="layout">
    <el-aside class="layout__aside" :width="collapsed ? '64px' : 'var(--sidebar-width)'">
      <div class="logo" :class="{ 'logo--collapsed': collapsed }">
        <div class="logo__mark">P</div>
        <div v-show="!collapsed" class="logo__text">
          <div class="logo__name">PayOps</div>
          <div class="logo__sub">支付管理后台</div>
        </div>
      </div>
      <el-scrollbar class="layout__menu-scroll">
        <el-menu
          router
          :default-active="activeMenu"
          :collapse="collapsed"
          :collapse-transition="false"
          background-color="transparent"
          text-color="#cbd5e1"
          active-text-color="#ffffff"
          class="side-menu"
        >
          <template v-for="group in visibleGroups" :key="group.title">
            <div v-show="!collapsed" class="side-menu__group">{{ group.title }}</div>
            <el-menu-item v-for="item in group.items" :key="item.path" :index="item.path">
              <el-icon><component :is="item.icon" /></el-icon>
              <template #title>{{ item.title }}</template>
            </el-menu-item>
          </template>
        </el-menu>
      </el-scrollbar>
    </el-aside>

    <el-container class="layout__main">
      <el-header class="layout__header" height="var(--header-height)">
        <div class="header-left">
          <el-icon class="header-toggle" @click="collapsed = !collapsed">
            <component :is="collapsed ? Expand : Fold" />
          </el-icon>
          <span class="header-title">{{ route.meta.title }}</span>
        </div>
        <div class="header-right">
          <el-tag :type="auth.isAdmin ? 'danger' : auth.canWrite ? 'primary' : 'info'" effect="plain" round>
            {{ enumLabel(ROLES, auth.role) }}
          </el-tag>
          <el-dropdown trigger="click" @command="onCommand">
            <span class="header-user">
              <el-avatar :size="28" :icon="UserFilled" class="header-user__avatar" />
              <span class="header-user__name">{{ displayName }}</span>
              <el-icon><ArrowDown /></el-icon>
            </span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item disabled>
                  <span class="text-small">账号：{{ auth.user?.username }}</span>
                </el-dropdown-item>
                <el-dropdown-item command="password" :icon="Key" divided>修改密码</el-dropdown-item>
                <el-dropdown-item command="logout" :icon="SwitchButton">退出登录</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-header>

      <el-main class="layout__content">
        <router-view v-slot="{ Component: View, route: current }">
          <component :is="View" :key="current.path" />
        </router-view>
      </el-main>
    </el-container>

    <ChangePasswordDialog v-model="passwordVisible" @changed="logout" />
  </el-container>
</template>

<style scoped>
.layout {
  height: 100%;
}
.layout__aside {
  display: flex;
  flex-direction: column;
  background: var(--sidebar-bg);
  transition: width 0.2s;
  overflow: hidden;
}
.layout__menu-scroll {
  flex: 1;
  min-height: 0;
}
.logo {
  display: flex;
  align-items: center;
  gap: 10px;
  height: var(--header-height);
  padding: 0 16px;
  flex-shrink: 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}
.logo--collapsed {
  justify-content: center;
  padding: 0;
}
.logo__mark {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 8px;
  background: linear-gradient(135deg, #3b82f6, #2563eb);
  color: #fff;
  font-size: 18px;
  font-weight: 700;
  flex-shrink: 0;
}
.logo__name {
  color: #fff;
  font-size: 16px;
  font-weight: 700;
  line-height: 1.2;
  letter-spacing: 0.5px;
}
.logo__sub {
  color: #94a3b8;
  font-size: 11px;
  line-height: 1.3;
}
.side-menu {
  border-right: none;
  padding: 8px 8px 16px;
}
.side-menu__group {
  padding: 14px 12px 6px;
  color: #64748b;
  font-size: 12px;
  letter-spacing: 1px;
}
.side-menu :deep(.el-menu-item) {
  height: 42px;
  line-height: 42px;
  margin-bottom: 2px;
  border-radius: 6px;
}
.side-menu :deep(.el-menu-item:hover) {
  background: rgba(255, 255, 255, 0.06);
}
.side-menu :deep(.el-menu-item.is-active) {
  background: var(--el-color-primary);
}
.side-menu.el-menu--collapse {
  width: 64px;
  padding: 8px;
}
.side-menu.el-menu--collapse :deep(.el-menu-item) {
  justify-content: center;
  padding: 0;
}
.layout__main {
  min-width: 0;
}
.layout__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 20px;
  background: #fff;
  border-bottom: 1px solid #e5e9f0;
}
.header-left {
  display: flex;
  align-items: center;
  gap: 14px;
}
.header-toggle {
  font-size: 20px;
  color: #475569;
  cursor: pointer;
}
.header-title {
  font-size: 16px;
  font-weight: 600;
}
.header-right {
  display: flex;
  align-items: center;
  gap: 16px;
}
.header-user {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
  color: #334155;
}
.header-user__avatar {
  background: var(--el-color-primary-light-8);
  color: var(--el-color-primary);
}
.header-user__name {
  max-width: 160px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.layout__content {
  padding: 16px 20px 24px;
  background: var(--app-bg);
}
</style>
