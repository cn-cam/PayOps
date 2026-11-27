<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { ReconDiffStatus } from '@/api/types'
import ReconBatchesPanel from './ReconBatchesPanel.vue'
import ReconDiffsPanel from './ReconDiffsPanel.vue'

type TabName = 'batches' | 'diffs'

const route = useRoute()
const router = useRouter()

/** 地址栏 ?tab=diffs 直接进入差异明细（仪表盘的「待处理对账差异」就是这样跳过来的） */
const tab = ref<TabName>(route.query.tab === 'diffs' ? 'diffs' : 'batches')

const batchesRef = ref<InstanceType<typeof ReconBatchesPanel>>()
const diffsRef = ref<InstanceType<typeof ReconDiffsPanel>>()
/** 差异被处理后，批次列表里的未处理数已过期，切回去时刷新一次 */
let batchesStale = false

function markBatchesStale() {
  batchesStale = true
}

function onTabChange(name: string | number) {
  const next: TabName = name === 'diffs' ? 'diffs' : 'batches'
  router.replace({ query: next === 'diffs' ? { tab: 'diffs' } : {} })
  if (next === 'batches' && batchesStale) {
    batchesStale = false
    batchesRef.value?.reload()
  }
}

function viewDiffs(filter: { batchId: number; status?: ReconDiffStatus }) {
  tab.value = 'diffs'
  router.replace({ query: { tab: 'diffs' } })
  diffsRef.value?.applyFilter(filter)
}
</script>

<template>
  <div class="page">
    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          对账是怎么运转的
          <span class="toolbar__sub">交易账单核对支付与退款，资金账单核对账户收支和手续费；只报告差异，不自动改账</span>
        </div>
      </div>
      <div class="flow-steps">
        <div class="flow-steps__step">
          <span class="flow-steps__no">1</span>
          <div>
            <div class="flow-steps__title">生成批次</div>
            <div class="flow-steps__desc">每天自动为支持账单的通道生成昨日批次，交易账单与资金账单各一批，也可以手动发起或重跑</div>
          </div>
        </div>
        <div class="flow-steps__step">
          <span class="flow-steps__no">2</span>
          <div>
            <div class="flow-steps__title">下载并比对</div>
            <div class="flow-steps__desc">下载交易账单或资金账单：交易逐笔比对支付、退款，资金核对收入、支出、退款与手续费</div>
          </div>
        </div>
        <div class="flow-steps__step">
          <span class="flow-steps__no">3</span>
          <div>
            <div class="flow-steps__title">产出差异</div>
            <div class="flow-steps__desc">两边一致即「已平账」，否则「有差异」并列出每一笔；只报告，不自动改账</div>
          </div>
        </div>
        <div class="flow-steps__step">
          <span class="flow-steps__no">4</span>
          <div>
            <div class="flow-steps__title">人工处理</div>
            <div class="flow-steps__desc">掉单去订单详情「同步」补单，其它情况核实后把差异标记为已处理</div>
          </div>
        </div>
      </div>
    </el-card>

    <el-card shadow="never">
      <el-tabs v-model="tab" class="recon-tabs" @tab-change="onTabChange">
        <el-tab-pane label="对账批次" name="batches">
          <ReconBatchesPanel ref="batchesRef" @view-diffs="viewDiffs" />
        </el-tab-pane>
        <el-tab-pane label="差异明细" name="diffs">
          <ReconDiffsPanel ref="diffsRef" @handled="markBatchesStale" />
        </el-tab-pane>
      </el-tabs>
    </el-card>
  </div>
</template>

<style scoped>
.recon-tabs :deep(.el-tabs__header) {
  margin-bottom: 18px;
}
.recon-tabs :deep(.el-tabs__item) {
  font-size: 15px;
}
</style>
