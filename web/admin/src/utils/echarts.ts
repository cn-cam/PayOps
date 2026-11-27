import * as echarts from 'echarts/core'
import { BarChart, LineChart, PieChart } from 'echarts/charts'
import type { BarSeriesOption, LineSeriesOption, PieSeriesOption } from 'echarts/charts'
import { GridComponent, LegendComponent, TitleComponent, TooltipComponent } from 'echarts/components'
import type {
  GridComponentOption,
  LegendComponentOption,
  TitleComponentOption,
  TooltipComponentOption,
} from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import type { ComposeOption } from 'echarts/core'

// 按需注册，避免把整个 echarts 打进包里
echarts.use([
  LineChart,
  BarChart,
  PieChart,
  GridComponent,
  TooltipComponent,
  LegendComponent,
  TitleComponent,
  CanvasRenderer,
])

export type ECOption = ComposeOption<
  | LineSeriesOption
  | BarSeriesOption
  | PieSeriesOption
  | GridComponentOption
  | TooltipComponentOption
  | LegendComponentOption
  | TitleComponentOption
>

export { echarts }
