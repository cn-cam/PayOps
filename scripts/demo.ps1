<#
.SYNOPSIS
  PayOps 端到端演示 / 冒烟测试：多渠道多通道路由、权重、起付金额、故障转移、支付回调、业务通知、退款。

.DESCRIPTION
  使用模拟通道（mock）走通完整链路，不依赖任何真实支付凭证：
    1. 登录后台，创建应用 demo、渠道 official；
    2. 创建三个模拟通道：A（权重 70）、B（权重 30，起付 50 元）、C（必定下单失败，用于演示故障转移与熔断）；
    3. 路由模拟：10 元只会命中 A，100 元按 70/30 分流；
    4. 通过带签名的业务接入 API 批量下单，统计实际通道分布；
    5. 模拟上游回调 → 订单成功 → 业务通知送达（演示接收端）；
    6. 部分退款。
  需要服务以 Development 环境运行（开启了演示接收端）。

.EXAMPLE
  pwsh scripts/demo.ps1 -BaseUrl http://localhost:5080
#>
param(
    [string]$BaseUrl = "http://localhost:5080",
    [string]$Username = "admin",
    [string]$Password = "Admin@123456",
    [string]$AppCode = "demo",
    [int]$Orders = 40,
    # PayOps 的 Worker 回调"演示接收端"时使用的地址。容器部署时 Worker 访问不到宿主机的 localhost，
    # 需要传容器网络内的入口，例如 docker compose 部署传 http://nginx
    [string]$InternalBaseUrl = ""
)
if (-not $InternalBaseUrl) { $InternalBaseUrl = $BaseUrl }

$ErrorActionPreference = "Stop"
$script:Failed = 0

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Assert($condition, $text) {
    if ($condition) { Write-Host "  [PASS] $text" -ForegroundColor Green }
    else { Write-Host "  [FAIL] $text" -ForegroundColor Red; $script:Failed++ }
}

function Admin($method, $path, $body = $null) {
    $params = @{ Method = $method; Uri = "$BaseUrl/api/admin$path"; Headers = @{ Authorization = "Bearer $script:Token" } }
    if ($null -ne $body) { $params.Body = ($body | ConvertTo-Json -Depth 8 -Compress); $params.ContentType = "application/json; charset=utf-8" }
    $resp = Invoke-RestMethod @params
    if ($resp.code -ne 0) { throw "后台接口 $method $path 失败：$($resp.message)" }
    return $resp.data
}

# 业务接入签名：hex(HMAC-SHA256(secret, "METHOD\npath?query\ntimestamp\nnonce\nbody"))
function Gateway($method, $pathAndQuery, $body = $null) {
    $json = if ($null -ne $body) { $body | ConvertTo-Json -Depth 8 -Compress } else { "" }
    $ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString()
    $nonce = [Guid]::NewGuid().ToString("N")
    $text = "$method`n$pathAndQuery`n$ts`n$nonce`n$json"
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($script:Secret))
    $sign = [Convert]::ToHexString($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    $params = @{
        Method = $method; Uri = "$BaseUrl$pathAndQuery"; SkipHttpErrorCheck = $true
        Headers = @{ "X-App-Id" = $AppCode; "X-Timestamp" = $ts; "X-Nonce" = $nonce; "X-Sign" = $sign }
    }
    if ($json) { $params.Body = [Text.Encoding]::UTF8.GetBytes($json); $params.ContentType = "application/json; charset=utf-8" }
    return Invoke-RestMethod @params
}

Step "1. 登录管理后台"
$login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/admin/auth/login" -ContentType "application/json" -Body (@{ username = $Username; password = $Password } | ConvertTo-Json)
if ($login.code -ne 0) { throw "登录失败：$($login.message)" }
$script:Token = $login.data.token
Assert ($login.data.user.role -eq "admin") "登录成功，角色 admin"

Step "2. 准备应用 / 渠道 / 通道 / 绑定"
$merchantNotifyUrl = "$InternalBaseUrl/api/demo/merchant-notify"
$app = (Admin GET "/apps/all") | Where-Object { $_.appCode -eq $AppCode }
if (-not $app) {
    $created = Admin POST "/apps" @{ appCode = $AppCode; name = "演示应用"; notifyUrl = $merchantNotifyUrl; remark = "demo.ps1 创建"; status = 1 }
    $appId = $created.id
    $script:Secret = $created.appSecret
} else {
    $appId = $app.id
    Admin PUT "/apps/$appId" @{ name = "演示应用"; notifyUrl = $merchantNotifyUrl; remark = "demo.ps1 创建"; status = 1 } | Out-Null
    $script:Secret = (Admin GET "/apps/$appId/secret").appSecret
}
Assert ($script:Secret.Length -gt 10) "应用 $AppCode 就绪（id=$appId）"

$channel = (Admin GET "/channels/all?appId=$appId") | Where-Object { $_.code -eq "official" }
if ($channel) { Admin DELETE "/channels/$($channel.id)" | Out-Null }
$channelId = (Admin POST "/channels" @{ appId = $appId; code = "official"; name = "官方包"; remark = ""; status = 1 }).id

function New-MockPassage($name, $failCreate) {
    $existing = (Admin GET "/passages/all?provider=mock") | Where-Object { $_.name -eq $name }
    if ($existing) { return $existing.id }
    return (Admin POST "/passages" @{ name = $name; provider = "mock"; status = 1; remark = "demo"; config = @{ secret = "demo-secret"; failCreate = $failCreate; refundAsync = "false" } }).id
}
$pA = New-MockPassage "模拟-商户号A" "false"
$pB = New-MockPassage "模拟-商户号B" "false"
$pC = New-MockPassage "模拟-故障商户号C" "true"

function Bind($passageId, $weight, $priority, $minAmount) {
    return (Admin POST "/channels/$channelId/bindings" @{
            passageId = $passageId; methods = @(); weight = $weight; priority = $priority
            minAmount = $minAmount; maxAmount = 0; dailyLimitAmount = 0; dailyLimitCount = 0
            startTime = ""; endTime = ""; status = 1; remark = "demo"
        }).id
}
$bA = Bind $pA 70 0 0
$bB = Bind $pB 30 0 5000
$bindings = Admin GET "/channels/$channelId/bindings"
Assert ($bindings.Count -eq 2) "渠道 official 绑定了 2 个同类型通道：A 权重 70；B 权重 30、起付 50 元"

Step "3. 路由模拟（与真实下单同一套算法）"
$sim10 = Admin POST "/channels/$channelId/route-simulate" @{ payMethod = "mock"; amount = 1000; times = 2000 }
$sim100 = Admin POST "/channels/$channelId/route-simulate" @{ payMethod = "mock"; amount = 10000; times = 2000 }
$sim10.items | ForEach-Object { Write-Host ("    10 元  {0,-16} 可用={1,-5} 命中率={2:P1} {3}" -f $_.passageName, $_.eligible, $_.hitRate, $_.reason) }
$sim100.items | ForEach-Object { Write-Host ("    100 元 {0,-16} 可用={1,-5} 命中率={2:P1} {3}" -f $_.passageName, $_.eligible, $_.hitRate, $_.reason) }
$b10 = $sim10.items | Where-Object { $_.bindingId -eq $bB }
$a100 = $sim100.items | Where-Object { $_.bindingId -eq $bA }
Assert (-not $b10.eligible -and $b10.hitCount -eq 0) "10 元低于 B 的起付金额，全部走 A"
Assert ($a100.hitRate -gt 0.62 -and $a100.hitRate -lt 0.78) "100 元时 A 命中率约 70%（实际 $([math]::Round($a100.hitRate * 100, 1))%）"

Step "4. 加入必定失败的通道 C，批量下单验证故障转移"
$bC = Bind $pC 100 0 0
Admin POST "/bindings/$bC/reset-breaker" | Out-Null
$stat = @{}
$placed = @()
for ($i = 0; $i -lt $Orders; $i++) {
    $resp = Gateway POST "/api/pay/orders" @{
        channel = "official"; payMethod = "mock"; mchOrderNo = "D" + [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() + $i
        amount = 10000; subject = "演示商品-$i"; userId = "u1001"; attach = "demo"
    }
    if ($resp.code -ne 0) { throw "下单失败：$($resp.message)" }
    $placed += $resp.data
    $stat[$resp.data.passageName] = 1 + [int]$stat[$resp.data.passageName]
}
$stat.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host ("    {0,-16} {1} 笔" -f $_.Name, $_.Value) }
Assert ($placed.Count -eq $Orders -and -not $stat.ContainsKey("模拟-故障商户号C")) "$Orders 笔全部下单成功：命中故障通道 C 的请求已自动转移到 A / B"
$rtC = (Admin GET "/channels/$channelId/bindings") | Where-Object { $_.id -eq $bC }
Assert ($rtC.runtime.breakerOpen -or $rtC.runtime.recentFailures -gt 0) "故障通道 C 已触发熔断/失败计数（breakerOpen=$($rtC.runtime.breakerOpen)，failures=$($rtC.runtime.recentFailures)）"
Admin DELETE "/bindings/$bC" | Out-Null

Step "5. 模拟上游支付回调 → 订单成功 → 业务通知"
$order = $placed[0]
# 凭据里的回调地址以 PayOps:PublicBaseUrl 为前缀；演示时统一改从本脚本可达的 BaseUrl 发起
$upstreamNotifyUrl = $BaseUrl + ([Uri]$order.credential.data.notifyUrl).AbsolutePath
$cb = Invoke-WebRequest -Method Post -Uri $upstreamNotifyUrl -Body $order.credential.data.body -ContentType "application/json" -SkipHttpErrorCheck
Assert ($cb.StatusCode -eq 200 -and $cb.Content -eq "success") "上游回调验签通过并应答 success"
$cb2 = Invoke-WebRequest -Method Post -Uri $upstreamNotifyUrl -Body $order.credential.data.body -ContentType "application/json" -SkipHttpErrorCheck
Assert ($cb2.Content -eq "success") "重复回调幂等"
$forged = $order.credential.data.body -replace '"amount":10000', '"amount":1'
$cb3 = Invoke-WebRequest -Method Post -Uri $upstreamNotifyUrl -Body $forged -ContentType "application/json" -SkipHttpErrorCheck
Assert ($cb3.Content -ne "success") "篡改金额的回调被拒绝"

$q = Gateway GET "/api/pay/orders?orderNo=$($order.orderNo)"
Assert ($q.data.status -eq 1) "业务接入 API 查询订单：支付成功"
$bad = Invoke-RestMethod -Uri "$BaseUrl/api/pay/orders?orderNo=$($order.orderNo)" -SkipHttpErrorCheck -Headers @{ "X-App-Id" = $AppCode; "X-Timestamp" = "1"; "X-Nonce" = "n"; "X-Sign" = "bad" }
Assert ($bad.code -ne 0) "签名错误的请求被拒绝（$($bad.errorCode)）"

$notified = $false
for ($i = 0; $i -lt 20 -and -not $notified; $i++) {
    Start-Sleep -Milliseconds 500
    $tasks = (Admin GET "/notify-tasks?bizNo=$($order.orderNo)").items
    $notified = ($tasks.Count -gt 0 -and $tasks[0].status -eq 1)
}
Assert $notified "业务通知经 RabbitMQ 投递成功（接收端验签通过并应答 success）"

Step "6. 退款"
$refund = Gateway POST "/api/pay/refunds" @{ orderNo = $order.orderNo; mchRefundNo = "RF" + $order.orderNo; amount = 3000; reason = "演示部分退款" }
Assert ($refund.code -eq 0 -and $refund.data.status -eq 1) "部分退款 30 元成功"
$again = Gateway POST "/api/pay/refunds" @{ orderNo = $order.orderNo; mchRefundNo = "RF" + $order.orderNo; amount = 3000; reason = "演示部分退款" }
Assert ($again.data.refundNo -eq $refund.data.refundNo) "相同退款单号重复请求幂等"
$over = Gateway POST "/api/pay/refunds" @{ orderNo = $order.orderNo; mchRefundNo = "RF2" + $order.orderNo; amount = 8000; reason = "超额" }
Assert ($over.code -ne 0) "超过可退余额的退款被拒绝（$($over.message)）"

Step "7. 集群与统计"
$info = Admin GET "/system/info"
$info.checks | ForEach-Object { Write-Host ("    {0,-9} ok={1} {2}" -f $_.name, $_.ok, $_.message) }
$info.nodes | ForEach-Object { Write-Host ("    节点 {0}  角色={1}  节点号={2}" -f $_.instanceId, ($_.roles -join ","), $_.workerId) }
Assert (($info.checks | Where-Object { -not $_.ok }).Count -eq 0) "MySQL / Redis / RabbitMQ 均正常，在线节点 $($info.nodes.Count) 个"
$summary = Admin GET "/dashboard/summary"
Write-Host ("    今日：订单 {0} 笔，成功 {1} 笔，成功金额 {2:N2} 元" -f $summary.today.orderCount, $summary.today.paidCount, ($summary.today.paidAmount / 100))

Write-Host ""
if ($script:Failed -eq 0) { Write-Host "全部通过。" -ForegroundColor Green; exit 0 }
Write-Host "$($script:Failed) 项未通过。" -ForegroundColor Red
exit 1
