# 生成独立设计稿，所有示例数值仅用于布局展示。
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$outDir = $PSScriptRoot
$script:svg = [System.Text.StringBuilder]::new()
$script:ink = '#F3F5F7'
$script:muted = '#B2BBC4'
$script:accent = '#149CFF'
function Rect($x, $y, $w, $h, $fill = '#202124', $stroke = '', $radius = 8) {
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($fill))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    if ($radius -gt 0) {
        $d = $radius * 2
        $path.AddArc($x, $y, $d, $d, 180, 90)
        $path.AddArc(($x+$w-$d), $y, $d, $d, 270, 90)
        $path.AddArc(($x+$w-$d), ($y+$h-$d), $d, $d, 0, 90)
        $path.AddArc($x, ($y+$h-$d), $d, $d, 90, 90)
        $path.CloseFigure()
    } else { $path.AddRectangle([System.Drawing.RectangleF]::new($x,$y,$w,$h)) }
    $script:g.FillPath($brush, $path)
    if ($stroke) {
        $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($stroke), 1)
        $script:g.DrawPath($pen, $path)
        $pen.Dispose()
    }
    $brush.Dispose(); $path.Dispose()
    $border = if ($stroke) { " stroke='$stroke'" } else { '' }
    [void]$script:svg.AppendLine("<rect x='$x' y='$y' width='$w' height='$h' rx='$radius' fill='$fill'$border/>")
}
function Txt($x, $y, $text, $size = 14, $color = '#F3F5F7', $bold = $false) {
    $style = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    $font = [System.Drawing.Font]::new('Microsoft YaHei UI', $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($color))
    $script:g.DrawString($text,$font,$brush,[float]$x,[float]$y)
    $font.Dispose(); $brush.Dispose()
    $safe = [System.Security.SecurityElement]::Escape($text)
    $weight = if ($bold) { 600 } else { 400 }
    $baseline = $y + $size + 1
    [void]$script:svg.AppendLine("<text x='$x' y='$baseline' font-family='Microsoft YaHei UI,Segoe UI,sans-serif' font-size='$size' font-weight='$weight' fill='$color'>$safe</text>")
}
function Line($x1,$y1,$x2,$y2,$color='#363C43') {
    $pen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml($color),1)
    $script:g.DrawLine($pen,[float]$x1,[float]$y1,[float]$x2,[float]$y2); $pen.Dispose()
    [void]$script:svg.AppendLine("<path d='M $x1 $y1 L $x2 $y2' stroke='$color'/>")
}
function Button($x,$y,$w,$label,$primary=$false) {
    $bg = if ($primary) { '#149CFF' } else { '#2B2D30' }
    $fg = if ($primary) { '#001525' } else { '#F3F5F7' }
    Rect $x $y $w 36 $bg; Txt ($x+14) ($y+8) $label 14 $fg $primary
}
function Badge($x,$y,$label,$color='#7CD7AB') { Txt $x $y ('●  '+$label) 12 $color }
function InputBox($x,$y,$w,$value) { Rect $x $y $w 36 '#2B2D30' '#363C43' 6; Txt ($x+12) ($y+8) $value }
function Toggle($x,$y,$on=$true) {
    $bg = if ($on) { '#149CFF' } else { '#414850' }
    Rect $x $y 38 20 $bg '' 10
    $offset = if ($on) { 21 } else { 3 }
    Rect ($x+$offset) ($y+3) 14 14 '#F3F5F7' '' 7
}
function Check($x,$y,$label,$on=$true) {
    if ($on) { Rect $x ($y+2) 18 18 '#E8EAED' '' 3; Txt ($x+2) $y '✓' 14 '#001525' }
    else { Rect $x ($y+2) 18 18 '#2B2D30' '#66727C' 3 }
    Txt ($x+28) $y $label
}
function Icon($x,$y,$kind,$color='#B2BBC4') {
    switch ($kind) {
        0 { Rect $x $y 22 15 '#000000' $color 2; Line ($x+11) ($y+15) ($x+11) ($y+20) $color; Line ($x+5) ($y+20) ($x+17) ($y+20) $color }
        1 { Rect $x $y 22 20 '#000000' $color 3; Line ($x+3) ($y+16) ($x+9) ($y+8) $color; Line ($x+9) ($y+8) ($x+18) ($y+16) $color }
        2 { foreach ($j in 0..2) { Line $x ($y+3+$j*7) ($x+22) ($y+3+$j*7) $color; Rect ($x+4+$j*5) ($y+$j*7) 4 6 $color '' 1 } }
        3 { Line $x ($y+12) ($x+6) ($y+12) $color; Line ($x+6) ($y+12) ($x+10) $y $color; Line ($x+10) $y ($x+14) ($y+20) $color; Line ($x+14) ($y+20) ($x+18) ($y+8) $color; Line ($x+18) ($y+8) ($x+23) ($y+8) $color }
        4 { Rect ($x+2) ($y+2) 18 18 '#000000' $color 8; Rect ($x+8) ($y+8) 6 6 '#000000' $color 2 }
    }
}
function StartSheet($title,$selected,$subtitle='AD102 · 已连接',$w=1180,$h=820,$navigation='Top') {
    $script:bitmap = [System.Drawing.Bitmap]::new($w,$h)
    $script:g = [System.Drawing.Graphics]::FromImage($script:bitmap)
    $script:g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $script:g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $script:svg = [System.Text.StringBuilder]::new()
    [void]$script:svg.AppendLine("<svg xmlns='http://www.w3.org/2000/svg' width='$w' height='$h' viewBox='0 0 $w $h'><title>USB Display $title 设计稿</title>")
    Rect 0 0 $w $h '#000000' '' 0
    Rect 0 0 $w 40 '#000000' '' 0
    Rect 16 12 16 16 '#E8EAED' '' 4
    Txt 43 10 'USB Display' 13 '#F3F5F7' $true
    Txt ($w-134) 8 '—       □       ×' 16 '#B2BBC4'
    $script:hasContentTransform = $w -gt 800 -and $navigation -eq 'Top'
    if ($script:hasContentTransform) {
        $labels = @('设备','主题','设备设置','诊断','应用设置')
        $xs = @(72,162,252,374,464)
        for ($i=0;$i -lt 5;$i++) {
            $c = '#B2BBC4'
            if ($i -eq $selected) { Rect $xs[$i] 86 ($labels[$i].Length*15) 2 '#149CFF' '' 1; $c='#149CFF' }
            Txt $xs[$i] 56 $labels[$i] 14 $c ($i -eq $selected)
        }
        Line 0 88 $w 88 '#292B2E'
        Txt 72 108 $title 26 '#F3F5F7' $true
        Txt 74 147 $subtitle 12 '#B2BBC4'
        Rect 888 44 220 36 '#202124' '' 6
        $contextLabel = if ($subtitle -eq '未连接设备') { '未选择设备          ﹀' } else { 'AD102  /  A01        ﹀' }
        Txt 904 53 $contextLabel 13
        Txt 72 794 '设计稿 v1.2 · 示例数据，非运行截图' 10 '#B2BBC4'
        $script:g.TranslateTransform(-44,40)
        [void]$script:svg.AppendLine("<g transform='translate(-44 40)'>")
    } elseif ($w -gt 800) {
        Rect 0 40 88 ($h-40) '#000000' '' 0
        $labels = @('设备','主题','设备设置','诊断','应用设置')
        $ys = @(66,138,210,598,670)
        for ($i=0;$i -lt 5;$i++) {
            $c = '#B2BBC4'
            if ($i -eq $selected) { Rect 6 $ys[$i] 76 62 '#2B2D30'; Rect 6 ($ys[$i]+19) 3 24 '#149CFF' '' 1; $c='#149CFF' }
            Icon 32 ($ys[$i]+9) $i $c
            $tx = if ($labels[$i].Length -gt 2) { 18 } else { 30 }
            Txt $tx ($ys[$i]+37) $labels[$i] 12 $c
        }
        Txt 116 65 $title 26 '#F3F5F7' $true
        Txt 118 106 $subtitle 12 '#B2BBC4'
        Rect 932 68 220 38 '#202124' '' 6
        $contextLabel = if ($subtitle -eq '未连接设备') { '未选择设备          ﹀' } else { 'AD102  /  A01        ﹀' }
        Txt 948 78 $contextLabel 13
        Txt 116 738 '设计稿 · 示例数据，非运行截图' 10 '#B2BBC4'
    }
}
function EndSheet($name) {
    if ($script:hasContentTransform) { [void]$script:svg.AppendLine('</g>') }
    [void]$script:svg.AppendLine('</svg>')
    [System.IO.File]::WriteAllText((Join-Path $outDir "$name.svg"),$script:svg.ToString(),[System.Text.UTF8Encoding]::new($false))
    $script:bitmap.Save((Join-Path $outDir "$name.png"),[System.Drawing.Imaging.ImageFormat]::Png)
    $script:g.Dispose(); $script:bitmap.Dispose()
}
function ClockPreview($x,$y,$w) {
    $h=$w*9/16
    Rect $x $y $w $h '#131416' '#45484C' 4
    Txt ($x+24) ($y+23) 'MONDAY' 12 '#B2BBC4'
    $fontSize = [int]($w/7)
    Txt ($x+$w*0.25) ($y+$h*0.31) '10:24' $fontSize '#F3F5F7' $true
    Txt ($x+$w*0.31) ($y+$h*0.69) '2026 / 10 / 12' 16 '#B2BBC4'
}
function Footer($status,$primary='应用到设备') {
    Line 116 672 1152 672
    Txt 118 695 $status 12 '#B2BBC4'
    Button 768 689 84 '更多  ···'
    Button 864 689 120 '保存草稿'
    Button 996 689 156 $primary $true
}
function Tabs($active=0) {
    $names=@('连接与日志','电脑监测','系统规格')
    for ($i=0;$i -lt 3;$i++) {
        $c=if($i -eq $active){'#149CFF'}else{'#B2BBC4'}
        Txt (118+$i*138) 142 $names[$i] 14 $c
        if($i -eq $active){Rect (118+$i*138) 170 86 3 '#149CFF' '' 1}
    }
}

StartSheet '设备' 0 '当前设备 · 主题模式'
Rect 116 146 644 420 '#202124' '#E8EAED' 12
Txt 142 164 'AD102' 24 '#F3F5F7' $true
Badge 628 175 '已连接'
Txt 144 203 'USB DISPLAY  /  1920 × 1080' 12 '#B2BBC4'
Rect 174 244 528 302 '#09090A' '#45484C' 8
ClockPreview 184 254 508
Txt 142 546 '屏幕外观示意 · 主机预览，未确认设备呈现' 12 '#B2BBC4'
Txt 802 167 '当前显示' 24 '#F3F5F7' $true
Txt 802 211 '当前使用时钟主题' 14 '#B2BBC4'
Line 802 248 1130 248
Txt 802 271 '显示模式' 13 '#B2BBC4'; Txt 1004 269 '主题模式' 15
Txt 802 315 '固件版本' 13 '#B2BBC4'; Txt 1004 313 '未取得' 15
Button 802 363 328 '编辑主题' $true
Button 802 415 156 '切换副屏'; Button 974 415 156 '熄屏'
Button 802 467 156 '暂停推流'; Button 974 467 156 '更多  ···'
Rect 116 586 1036 128
Txt 138 603 '发现的设备' 16 '#F3F5F7' $true
Button 1010 597 120 '重新检测'
Line 138 644 1130 644
Badge 140 667 'AD102  ·  A01'; Txt 396 667 '当前设备' 13 '#B2BBC4'; Txt 662 667 '接口就绪' 13 '#B2BBC4'
Txt 1017 667 '已连接' 13 '#7CD7AB'
EndSheet '01-devices'

foreach ($kind in @('clock','image','dashboard')) {
    StartSheet '主题' 1 '草稿归属 · AD102 / A01'
    Rect 116 146 348 508
    Txt 136 162 '选择模板' 16 '#F3F5F7' $true
    $names=@('图片','时钟','仪表盘'); $keys=@('image','clock','dashboard')
    for($i=0;$i -lt 3;$i++) {
        $selected=$keys[$i] -eq $kind
        $stroke=if($selected){'#E8EAED'}else{'#363C43'}
        Rect (136+$i*104) 202 94 68 '#2B2D30' $stroke 6
        Txt (149+$i*104) 223 $names[$i] 14
        if($selected){Txt (209+$i*104) 246 '✓' 12 '#E8EAED'}
    }
    if($kind -eq 'clock') {
        Check 138 302 '24 小时制'; Check 138 340 '显示日期'
        Txt 138 394 '背景色' 13 '#B2BBC4'; InputBox 138 420 304 '#131416'
        Txt 138 479 '文字色' 13 '#B2BBC4'; InputBox 138 505 304 '#F3F5F7'
    } elseif($kind -eq 'image') {
        Txt 138 301 '图片素材' 13 '#B2BBC4'; Button 138 331 304 '选择图片'
        Txt 138 379 'mountain.png · 1920 × 1080' 12 '#B2BBC4'
        Txt 138 418 '适配方式' 13 '#B2BBC4'; InputBox 138 445 304 '完整适配                         ﹀'
        Txt 138 501 '背景色' 13 '#B2BBC4'; InputBox 138 529 304 '#000000'
    } else {
        Txt 138 298 '显示指标' 13 '#B2BBC4'
        Check 138 334 'PC CPU 使用率'; Check 138 371 'PC 内存占用'
        Check 138 408 'PC 温度' $false; Check 138 445 '设备温度' $false
        Txt 138 491 '背景色 / 文字色' 13 '#B2BBC4'
        InputBox 138 522 142 '#131416'; InputBox 292 522 150 '#F3F5F7'
    }
    Txt 138 606 '草稿来源  ﹀' 13 '#B2BBC4'
    Txt 506 165 '草稿预览' 16 '#F3F5F7' $true
    Txt 986 168 '1920 × 1080' 12 '#B2BBC4'
    if($kind -eq 'clock'){ClockPreview 506 216 624}
    elseif($kind -eq 'image') {
        Rect 506 216 624 351 '#191B1E' '' 4
        for($j=0;$j -lt 8;$j++) { Line 506 (435+$j*17) (740+$j*40) (265+$j*25) '#45484C'; Line (740+$j*40) (265+$j*25) 1130 (442+$j*14) '#45484C' }
        Txt 533 242 '图片内容示意' 12 '#B2BBC4'
    } else {
        Rect 506 216 624 351 '#131416' '' 4
        Txt 542 248 'SYSTEM STATUS' 15 '#B2BBC4'
        Txt 542 324 'CPU' 14 '#B2BBC4'; Txt 825 324 'MEMORY' 14 '#B2BBC4'
        Txt 538 353 '24%' 64 '#F3F5F7' $true; Txt 821 353 '61%' 64 '#F3F5F7' $true
        Line 542 465 1092 465; Txt 542 497 '此电脑  /  10:24' 14 '#B2BBC4'
    }
    Txt 506 588 '预览就绪 · 尚未应用到设备' 13 '#B2BBC4'
    Footer '草稿已修改'
    $name=switch($kind){'clock'{'02-themes-clock'}'image'{'07-themes-image'}'dashboard'{'08-themes-dashboard'}}
    EndSheet $name
}

StartSheet '设备设置' 2 'AD102 / A01 · 参数与当前设备绑定'
Rect 116 146 650 180
Txt 138 166 '亮度' 16 '#F3F5F7' $true; Txt 208 169 '即时生效' 12 '#B2BBC4'
Txt 668 165 '80%' 23 '#F3F5F7' $true
Rect 140 230 600 4 '#414850' '' 2; Rect 140 230 480 4 '#E8EAED' '' 2; Rect 611 222 20 20 '#E8EAED' '' 10
Txt 138 272 '目标 80%     已受理 80%     设备读回 80%' 12 '#B2BBC4'
Button 620 266 122 '设为 80'
Rect 116 346 650 266
Txt 138 366 '图像输出' 16 '#F3F5F7' $true; Txt 239 369 '应用后生效' 12 '#B2BBC4'
Txt 138 428 '显示方向'; InputBox 482 418 260 '0°                             ﹀'
Txt 138 487 '帧率上限'; InputBox 482 477 260 '5 fps                          ﹀'
Txt 138 546 'JPEG 画质'; InputBox 482 536 260 '80                      −     +'
Rect 786 146 366 466
Txt 808 166 '设备读回' 16 '#F3F5F7' $true
Txt 808 213 '模式' 13 '#B2BBC4'; Txt 998 213 '主题模式'
Txt 808 254 '分辨率' 13 '#B2BBC4'; Txt 987 254 '1920 × 1080'
Txt 808 295 '亮度' 13 '#B2BBC4'; Txt 998 295 '80%'
Line 808 340 1130 340
Txt 808 365 '草稿归属' 16 '#F3F5F7' $true; Txt 808 404 'AD102 / A01' 14 '#B2BBC4'
Button 808 454 322 '读取所选设备草稿'; Button 808 506 322 '读取无设备草稿'
Footer '图像输出草稿未修改' '应用设置'
EndSheet '03-device-settings'

StartSheet '诊断' 3 '连接与日志 · 当前会话'
Tabs 0
Button 872 136 132 '打开日志目录'; Button 1016 136 136 '导出诊断' $true
Rect 116 194 508 144; Rect 644 194 508 144
Txt 138 214 '主机传输' 16 '#F3F5F7' $true; Badge 520 217 '运行中'
Txt 138 255 'USB 写入完成' 22 '#F3F5F7' $true; Txt 138 298 '主机侧记录，不代表设备已呈现' 12 '#B2BBC4'
Txt 666 214 '设备反馈' 16 '#F3F5F7' $true
Txt 666 255 '呈现状态不可用' 22 '#B2BBC4'; Txt 666 298 '当前反馈未提供屏幕呈现确认' 12 '#B2BBC4'
Rect 116 358 1036 356
Txt 138 377 '最近事件' 16 '#F3F5F7' $true; Txt 1012 381 '3 条记录' 12 '#B2BBC4'
$xs=@(138,274,390,582,888);$heads=@('时间','级别','阶段','代码','错误编号')
for($i=0;$i -lt 5;$i++){Txt $xs[$i] 429 $heads[$i] 12 '#B2BBC4'}
Line 138 460 1130 460
$rows=@(@('10:24:18','信息','传输','FrameCompleted','—'),@('10:24:17','信息','会话','Connected','—'),@('10:23:52','警告','本地保存','SaveUnconfirmed','DEMO-1042'))
for($r=0;$r -lt 3;$r++){for($c=0;$c -lt 5;$c++){Txt $xs[$c] (480+$r*58) $rows[$r][$c] 13 $(if($r -eq 2){'#F3C777'}else{'#F3F5F7'})};Line 138 (517+$r*58) 1130 (517+$r*58)}
EndSheet '04-diagnostics'

StartSheet '应用设置' 4 '偏好与软件维护'
Button 996 92 156 '外观设置  →'
Rect 116 146 1036 170
Txt 138 165 '启动与窗口' 16 '#F3F5F7' $true
Txt 138 211 '关闭窗口'; InputBox 860 202 270 '最小化到托盘                ﹀'
Line 138 251 1130 251
Txt 138 271 '开机自启'; Toggle 1088 274 $false
Rect 116 336 1036 154
Txt 138 355 '设备模式策略' 16 '#F3F5F7' $true
Txt 138 398 '启动自动切主题'; Toggle 1088 401 $false
Txt 138 445 '退出时切回副屏'; Toggle 1088 448 $false
Rect 116 510 1036 142
Txt 138 529 '软件更新' 16 '#F3F5F7' $true
Txt 138 564 '当前版本：以安装版本为准' 12 '#B2BBC4'
Txt 138 600 '尚未检查 · 自动检查至少间隔 24 小时' 12 '#F3C777'
Txt 718 544 '自动检查更新'; Toggle 1088 547 $true
Button 996 590 134 '检查更新' $true
Line 116 672 1152 672
Txt 118 698 '设置尚未修改' 12 '#B2BBC4'
Button 864 689 140 '重新读取设置'; Button 1016 689 136 '保存设置' $true
EndSheet '05-application-settings'

StartSheet '设备' 0 '未连接设备'
Rect 116 146 1036 500
Icon 611 240 0 '#149CFF'
Txt 514 304 '尚未发现 USB 显示设备' 24 '#F3F5F7' $true
Txt 487 352 '连接设备后重新检测，也可以先编辑离线主题。' 14 '#B2BBC4'
Button 480 411 150 '重新检测' $true; Button 646 411 170 '离线编辑主题'
Txt 512 490 '草稿归属：无设备 · 不会发送到硬件' 12 '#B2BBC4'
EndSheet '06-offline'

StartSheet '诊断' 3 '电脑监测 · 扩展视图设计'
Tabs 1
$labels=@('CPU','物理内存','GPU','存储');$vals=@('24%','61%','不可用','不可用')
for($i=0;$i -lt 4;$i++) {
    $x=116+($i%2)*528;$y=194+[math]::Floor($i/2)*260
    Rect $x $y 508 240
    Txt ($x+24) ($y+22) $labels[$i] 16 '#F3F5F7' $true
    Txt ($x+24) ($y+66) $vals[$i] 40 $(if($i -lt 2){'#F3F5F7'}else{'#B2BBC4'}) $true
    if($i -lt 2) {
        Rect ($x+24) ($y+132) 456 4 '#414850' '' 2
        Rect ($x+24) ($y+132) $(if($i -eq 0){110}else{278}) 4 '#E8EAED' '' 2
        Txt ($x+24) ($y+159) $(if($i -eq 0){'温度  不可用        功耗  不可用'}else{'系统物理内存占用百分比'}) 13 '#B2BBC4'
        Txt ($x+24) ($y+201) '最后更新 10:24:18 · 1 秒前' 12 '#B2BBC4'
    } else { Txt ($x+24) ($y+158) '当前未接入此项采集' 13 '#B2BBC4' }
}
EndSheet '09-monitoring'

StartSheet '诊断' 3 '系统规格 · 扩展视图设计'
Tabs 2
Button 906 136 102 '刷新'; Button 1020 136 132 '复制规格'
Rect 116 194 1036 506
Txt 140 216 '此电脑' 16 '#F3F5F7' $true
$labels=@('设备名称','操作系统','CPU','GPU','主板','内存','磁盘')
$values=@('DESKTOP-DEMO（示例）','Windows 11（示例）','AMD Ryzen 7 9700X（示例）','NVIDIA GeForce RTX 4070（示例）','B650 主板（示例）','DDR5 · 16 GB × 2（示例）','NVMe SSD · 2 TB（示例）')
for($i=0;$i -lt 7;$i++){ $y=267+$i*57;Txt 140 $y $labels[$i] 14 '#B2BBC4';Txt 350 $y $values[$i] 14; if($i -lt 6){Line 140 ($y+38) 1128 ($y+38)}}
EndSheet '10-system-info'

StartSheet '操作反馈与确认' 3 '组件设计 · 关键状态'
Rect 116 146 1036 98 '#322A1C'
Txt 138 164 '操作结果尚未确认' 16 '#F3C777' $true
Txt 138 201 '请先核实当前状态，再继续操作。错误编号：DEMO-1042' 13 '#F3F5F7'
Button 998 175 132 '核实状态'
Rect 116 264 1036 98 '#1C302B'
Txt 138 282 '主题已应用，本地保存未完成' 16 '#7CD7AB' $true
Txt 138 319 '重试只保存恢复配置，不重复发送主题。' 13 '#F3F5F7'
Button 958 293 172 '仅重试保存'
Rect 116 382 496 288 '#202124' '#363C43' 12
Txt 140 407 '离开副屏并切换主题？' 22 '#F3F5F7' $true
Txt 140 463 'Windows 可能重新排列扩展屏上的窗口。' 14 '#B2BBC4'
Txt 140 494 '确认后重新连接设备并核验主题首帧。' 14 '#B2BBC4'
Check 140 540 '不再显示此提示' $false
Button 342 609 100 '取消'; Button 458 609 128 '切换主题' $true
Rect 632 382 520 288 '#202124' '#363C43' 12
Txt 656 407 '保存草稿后离开？' 22 '#F3F5F7' $true
Txt 656 463 'AD102 / A01 有尚未保存的编辑。' 14 '#B2BBC4'
Txt 656 494 '保存草稿不会应用到设备。' 14 '#B2BBC4'
Button 656 609 100 '取消'; Button 772 609 140 '放弃本次编辑'; Button 928 609 200 '保存草稿' $true
EndSheet '11-states'

StartSheet '主题' 1 '' 640 400
Txt 16 57 '☰' 22 '#149CFF'; Txt 56 57 '主题' 22 '#F3F5F7' $true
Txt 368 63 'AD102 / A01 · 草稿' 12 '#B2BBC4'
Rect 16 102 608 232
Txt 32 115 '草稿预览（已收起）' 14 '#F3F5F7' $true
Txt 523 117 '展开  ﹀' 12 '#149CFF'
Line 32 151 604 151
Txt 32 164 '时钟' 16 '#149CFF' $true
Check 32 201 '24 小时制'; Check 32 237 '显示日期'
Txt 32 279 '背景色' 12 '#B2BBC4'; InputBox 142 274 462 '#131416'
Line 16 344 624 344; Txt 18 363 '草稿已修改' 12 '#B2BBC4'
Button 230 354 80 '更多'; Button 322 354 128 '保存草稿'; Button 462 354 162 '应用到设备' $true
EndSheet '12-compact'

foreach ($layout in @('Top','Side')) {
    $caption = if ($layout -eq 'Top') { '顶部导航' } else { '侧边导航' }
    StartSheet '应用设置' 4 '外观 · 导航布局' 1180 820 $layout
    Rect 116 146 1036 340
    Txt 142 168 '导航布局' 18 '#F3F5F7' $true
    Txt 142 206 '选择适合你的操作方式' 14 '#B2BBC4'
    for ($i=0;$i -lt 2;$i++) {
        $x=142+$i*490
        $isChosen = ($i -eq 0 -and $layout -eq 'Top') -or ($i -eq 1 -and $layout -eq 'Side')
        $border = if ($isChosen) { '#E8EAED' } else { '#363C43' }
        Rect $x 250 464 204 '#2B2D30' $border 8
        Rect ($x+24) 270 416 128 '#000000' '' 4
        if ($i -eq 0) {
            Rect ($x+36) 284 392 24 '#202124' '' 3
            Rect ($x+50) 294 42 3 '#149CFF' '' 1
            Rect ($x+36) 324 250 60 '#202124' '' 3
        } else {
            Rect ($x+36) 282 52 104 '#202124' '' 3
            Rect ($x+44) 298 3 16 '#149CFF' '' 1
            Rect ($x+104) 282 250 104 '#202124' '' 3
        }
        $label = if ($i -eq 0) { '顶部导航' } else { '侧边导航' }
        Txt ($x+24) 418 $label 15 '#F3F5F7' $true
        if ($isChosen) { Txt ($x+358) 419 '✓ 已选择' 13 '#E8EAED' }
    }
    Txt 142 523 '切换后保留当前页面、设备连接和未保存草稿。' 14 '#B2BBC4'
    Txt 142 556 '窄窗口自动收起为菜单，窗口变宽后恢复所选布局。' 14 '#B2BBC4'
    Line 116 672 1152 672
    Txt 118 698 ('当前预览：'+$caption) 12 '#B2BBC4'
    Button 864 689 140 '撤销预览'; Button 1016 689 136 '保存设置' $true
    EndSheet $(if ($layout -eq 'Top') { '13-navigation-top' } else { '14-navigation-side' })
}


function DisabledAction([int]$x,[int]$y,[string]$label) { Rect $x $y 220 38 '#2B2D30'; Txt ($x+14) ($y+10) $label 13 '#8B949E' }

StartSheet '选择设备' 0 '发现 2 台 · 尚未接管'
Rect 116 146 1036 112
Txt 140 169 '请明确选择要连接的设备' 24 '#F3F5F7' $true
Txt 140 216 '发现 2 台 · 尚未接管' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  检测设备' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  选中候选' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  确认选择' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  连接设备' 14 '#149CFF'
Rect 116 346 1036 228
Rect 132 360 1004 40 '#2B2D30' '#E8EAED'
Check 146 369 'AD102 / A01   · 主题模式 · 身份可区分' $true
Check 146 417 'AD102 / B02   · 副屏模式 · 身份可区分' $false
Txt 140 456 '已选候选：A01；选择后仍需显式连接' 15 '#B2BBC4'
Txt 140 598 '未确认选择前，连接与所有硬件操作不可用' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '重新检测'; Button 996 689 156 '选择设备' $true
EndSheet '15-device-selection'

StartSheet '连接设备' 0 'AD102 / A01 · 连接中'
Rect 116 146 1036 112
Txt 140 169 '正在读取设备状态' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · 连接中' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  发现接口' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  读取状态' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  建立会话' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  连接完成' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '已发现匹配接口' 15 '#B2BBC4'
Txt 140 413 '正在核验模式、能力与电源读回' 15 '#B2BBC4'
Txt 140 456 '连接完成前不启用硬件控制' 15 '#B2BBC4'
Txt 140 598 '取消后等待原生操作收敛，不立即开放重连' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '查看诊断'; Button 996 689 156 '取消等待' $true
EndSheet '16-connecting'

StartSheet '设备' 0 'AD102 / A01 · SCREEN'
Rect 116 146 1036 112
Txt 140 169 '副屏模式已确认' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · SCREEN' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  目标 PID' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  HID 核实' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  显示路径核实' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  副屏就绪' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '桌面输出：待核实，由 Windows 显示驱动负责' 15 '#B2BBC4'
Txt 140 413 '亮度、熄屏、唤醒：当前副屏模式不支持' 15 '#B2BBC4'
Txt 140 456 '主题可编辑并保存草稿，不能应用到设备' 15 '#B2BBC4'
Txt 140 598 '模式确认不等于桌面输出可用；首次切出显示窗口重排提示' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '编辑草稿'; Button 996 689 156 '切换主题' $true
DisabledAction 140 526 '亮度 / 熄屏：不可用'
EndSheet '17-screen-mode'

StartSheet '设备' 0 'AD102 / A01 · THEME / Off'
Rect 116 146 1036 112
Txt 140 169 '设备已熄屏' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · THEME / Off' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  电源 Off' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  主题停流' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  保留活动配置' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  等待唤醒' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '最后主机预览保留，不表示屏幕正在显示' 15 '#B2BBC4'
Txt 140 413 '模式切换、应用主题、恢复推流不可用' 15 '#B2BBC4'
Txt 140 456 '当前固件切换会亮屏，请先明确唤醒' 15 '#B2BBC4'
Txt 140 598 '唤醒恢复活动配置，不自动应用新草稿' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '保存草稿'; Button 996 689 156 '唤醒设备' $true
DisabledAction 140 526 '切换模式：请先唤醒'
EndSheet '18-power-off'

StartSheet '设备' 0 'AD102 / A01 · THEME / On'
Rect 116 146 1036 112
Txt 140 169 '主题推流已暂停' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · THEME / On' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  活动主题' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  停止产帧' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  在途收敛' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  已暂停' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '屏幕电源：On；主机停止产生新帧' 15 '#B2BBC4'
Txt 140 413 '当前活动配置：时钟；编辑草稿不会改变活动配置' 15 '#B2BBC4'
Txt 140 456 '亮度、熄屏、切副屏仍按各自能力开放' 15 '#B2BBC4'
Txt 140 598 '恢复继续活动主题，不应用未提交的编辑' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '编辑草稿'; Button 996 689 156 '恢复推流' $true
EndSheet '19-stream-paused'

StartSheet '切换模式' 0 'AD102 / A01 · THEME → SCREEN'
Rect 116 146 1036 112
Txt 140 169 '正在等待设备重新连接' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · THEME → SCREEN' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  停止推流' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  发送切换' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  等待重枚举' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  核实目标' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '主题产帧已停止，在途写已收敛' 15 '#B2BBC4'
Txt 140 413 '切换命令已发送，正在等待同一设备目标接口' 15 '#B2BBC4'
Txt 140 456 '所有冲突控制与设备选择暂不可用' 15 '#B2BBC4'
Txt 140 598 '使用阶段进度；8 秒等待后进入状态核实，不伪造百分比' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '查看诊断'; Button 996 689 156 '取消等待' $true
EndSheet '20-mode-switching'

StartSheet '切换模式' 0 'AD102 / A01 · 结果待确认'
Rect 116 146 1036 112
Txt 140 169 '尚无法确认设备当前模式' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · 结果待确认' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  切换请求' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  等待超时' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  核实状态' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  结果未知' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '未找到可核实的原态或目标态' 15 '#B2BBC4'
Txt 140 413 '错误编号：DEMO-1042' 15 '#B2BBC4'
Txt 140 456 '保留原操作记录，不盲目重发或发送反向切换' 15 '#B2BBC4'
Txt 140 598 '确认实际状态之前，模式与其他冲突写入保持禁用' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '查看诊断'; Button 996 689 156 '核实状态' $true
EndSheet '21-mode-unknown'

StartSheet '恢复连接' 0 'AD102 / A01 · 正在恢复'
Rect 116 146 1036 112
Txt 140 169 '等待原设备重新连接' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · 正在恢复' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  发现原设备' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  核验能力' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  恢复会话' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  核实状态' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '仅恢复 A01，不接管其他设备' 15 '#B2BBC4'
Txt 140 413 '恢复已保存配置及电源意图，不重放未应用草稿' 15 '#B2BBC4'
Txt 140 456 '若原电源意图为 Off，保持熄屏' 15 '#B2BBC4'
Txt 140 598 '自动恢复有界；等待期间不重复发起恢复' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '查看诊断'; Button 996 689 156 '停止等待' $true
EndSheet '22-recovering'

StartSheet '连接故障' 0 'AD102 / A01 · 未连接'
Rect 116 146 1036 112
Txt 140 169 '设备恢复未完成' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · 未连接' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  自动恢复' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  等待设备' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  核验失败' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  等待处理' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '未能在恢复窗口内建立可用会话' 15 '#B2BBC4'
Txt 140 413 '错误编号：DEMO-2031；请检查设备连接' 15 '#B2BBC4'
Txt 140 456 '草稿保留，恢复不会续传残帧' 15 '#B2BBC4'
Txt 140 598 '重试不绕过身份与能力核验，不自动循环恢复' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '查看诊断'; Button 996 689 156 '重新检测' $true
EndSheet '23-recovery-failed'

StartSheet '托盘与窗口' 0 'AD102 / A01 · 主题运行中'
Rect 116 146 1036 112
Txt 140 169 '窗口已收至托盘' 24 '#F3F5F7' $true
Txt 140 216 'AD102 / A01 · 主题运行中' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  关闭窗口' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  保留会话' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  继续推流' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  托盘操作' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '打开 USB Display' 15 '#B2BBC4'
Txt 140 413 '切换主题（当前已是主题：禁用）／切换副屏' 15 '#B2BBC4'
Txt 140 456 '暂停推流／恢复推流（按实际状态互斥）' 15 '#B2BBC4'
Txt 140 499 '退出 USB Display' 15 '#B2BBC4'
Txt 140 598 '托盘与主窗口共用命令门禁；熄屏、未知和忙碌状态同步限制' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '打开窗口'; Button 996 689 156 '暂停推流' $true
EndSheet '24-tray'

StartSheet '退出应用' 0 '资源与配置确认'
Rect 116 146 1036 112
Txt 140 169 '退出切回副屏未完成' 24 '#F3F5F7' $true
Txt 140 216 '资源与配置确认' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  处理未保存' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  切回副屏' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  收敛资源' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  退出' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '设备切换结果待确认；错误编号：DEMO-1042' 15 '#B2BBC4'
Txt 140 413 '继续退出仍需停止推流并等待资源安全收敛' 15 '#B2BBC4'
Txt 140 456 '未保存内容先提供保存、放弃本次编辑、取消' 15 '#B2BBC4'
Txt 140 598 '重试前先核实原切换结果；系统关机使用有界清理' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '取消退出'; Button 996 689 156 '继续退出' $true
Button 640 689 166 '核实后重试'
EndSheet '25-exit'

StartSheet '软件更新' 4 '当前版本：1.0.0（示例）'
Rect 116 146 1036 112
Txt 140 169 '正在检查更新' 24 '#F3F5F7' $true
Txt 140 216 '当前版本：1.0.0（示例）' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  检查来源' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  兼容筛选' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  版本比较' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  检查结果' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '自动检查：开启；自动尝试至少间隔 24 小时' 15 '#B2BBC4'
Txt 140 413 '手动检查不受 24 小时间隔限制' 15 '#B2BBC4'
Txt 140 456 '本次只检查，不自动下载或安装' 15 '#B2BBC4'
Txt 140 598 '取消结束本次等待；未确认结果不显示“已是最新版本”' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '返回设置'; Button 996 689 156 '取消检查' $true
EndSheet '26-update-check'

StartSheet '软件更新' 4 '当前版本：1.0.0（示例）'
Rect 116 146 1036 112
Txt 140 169 '发现新版本 1.1.0（示例）' 24 '#F3F5F7' $true
Txt 140 216 '当前版本：1.0.0（示例）' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  检查完成' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  等待下载' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  校验产物' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  等待安装' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '下载大小：42 MB（示例） · 稳定版' 15 '#B2BBC4'
Txt 140 413 '更新说明：连接状态与界面改进（示例）' 15 '#B2BBC4'
Txt 140 456 '仅更新上位机，不更新设备固件或驱动' 15 '#B2BBC4'
Txt 140 598 '无兼容新版本显示“已是最新版本”，不与检查失败混同' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '稍后'; Button 996 689 156 '下载更新' $true
EndSheet '27-update-available'

StartSheet '软件更新' 4 '1.1.0 · 下载中（示例）'
Rect 116 146 1036 112
Txt 140 169 '已下载 18 / 42 MB · 43%' 24 '#F3F5F7' $true
Txt 140 216 '1.1.0 · 下载中（示例）' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  下载文件' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  校验产物' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  等待安装' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  重启交接' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '进度由真实已下载字节计算' 15 '#B2BBC4'
Txt 140 413 '下载结束后校验包身份、完整性与兼容性' 15 '#B2BBC4'
Txt 140 456 '校验期间使用不定进度，校验通过前不能安装' 15 '#B2BBC4'
Txt 140 598 '取消或失败保留当前版本；不能把下载完成显示为安装成功' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '返回设置'; Button 996 689 156 '取消下载' $true
EndSheet '28-update-download'

StartSheet '软件更新' 4 '1.1.0 · 已校验（示例）'
Rect 116 146 1036 112
Txt 140 169 '更新已准备就绪' 24 '#F3F5F7' $true
Txt 140 216 '1.1.0 · 已校验（示例）' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  下载完成' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  校验通过' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  用户确认' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  安全交接' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '重启软件会短暂停止主题推流' 15 '#B2BBC4'
Txt 140 413 '安装前处理未保存草稿、设置和待确认保存' 15 '#B2BBC4'
Txt 140 456 '设备操作安全收敛后才能交接更新程序' 15 '#B2BBC4'
Txt 140 598 '安装准备中禁用重复确认；不执行退出切副屏或启动切主题策略' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '稍后'; Button 996 689 156 '重启并安装' $true
EndSheet '29-update-ready'

StartSheet '软件更新' 4 '保留当前版本'
Rect 116 146 1036 112
Txt 140 169 '检查或安装未完成' 24 '#F3F5F7' $true
Txt 140 216 '保留当前版本' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  来源检查' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  下载校验' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  安装交接' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  结果核实' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '检查失败：网络不可用；可重新检查' 15 '#B2BBC4'
Txt 140 413 '校验失败：禁止安装；重新下载可信产物' 15 '#B2BBC4'
Txt 140 456 '安装或启动失败：打开官方完整安装包与日志位置' 15 '#B2BBC4'
Txt 140 598 '按失败阶段显示对应恢复动作；来源未配置时解释原因并禁用检查' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '查看日志'; Button 996 689 156 '重试检查' $true
EndSheet '30-update-failure'

StartSheet '软件更新' 4 '当前版本：1.1.0（示例）'
Rect 116 146 1036 112
Txt 140 169 '已更新至 1.1.0' 24 '#F3F5F7' $true
Txt 140 216 '当前版本：1.1.0（示例）' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  版本确认' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  配置读取' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  界面初始化' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  更新完成' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 '版本已达到目标，配置读取成功' 15 '#B2BBC4'
Txt 140 413 '主窗口或托盘初始化完成' 15 '#B2BBC4'
Txt 140 456 'USB 设备未连接不影响软件更新成功结论' 15 '#B2BBC4'
Txt 140 598 '重新发现设备实际模式，按已保存配置恢复，不主动切模式' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '查看说明'; Button 996 689 156 '完成' $true
EndSheet '31-update-confirmed'

StartSheet '诊断' 3 '指标来源与更新时间'
Rect 116 146 1036 112
Txt 140 169 '部分数据不可用或已过期' 24 '#F3F5F7' $true
Txt 140 216 '指标来源与更新时间' 13 '#B2BBC4'
Rect 116 278 244 48 '#202124'
Txt 130 294 '01  PC 采样' 14 '#149CFF'
Rect 380 278 244 48 '#202124'
Txt 394 294 '02  设备反馈' 14 '#149CFF'
Rect 644 278 244 48 '#202124'
Txt 658 294 '03  来源校验' 14 '#149CFF'
Rect 908 278 244 48 '#202124'
Txt 922 294 '04  新鲜度判断' 14 '#149CFF'
Rect 116 346 1036 228
Txt 140 370 'CPU 使用率：24%（最后值） · 5 秒前 · 数据已过期' 15 '#B2BBC4'
Txt 140 413 'PC 温度／风扇：不可用，传感器未接入' 15 '#B2BBC4'
Txt 140 456 '设备温度／FPS：不可用，固件字段为占位' 15 '#B2BBC4'
Txt 140 499 '主机发送速率单独列示，不冒充设备实际 FPS' 15 '#B2BBC4'
Txt 140 598 '缺失值不显示假 0；超过 3 秒停更保留最后值并标注过期' 13 '#B2BBC4'
Line 116 672 1152 672
Txt 118 698 '静态设计 · 示例状态' 12 '#B2BBC4'
Button 824 689 156 '连接与日志'; Button 996 689 156 '刷新状态' $true
EndSheet '32-metrics-stale'

$pages = @(
    @('01-devices','设备'),@('02-themes-clock','主题 · 时钟'),@('03-device-settings','设备设置'),
    @('04-diagnostics','诊断 · 连接与日志'),@('05-application-settings','应用设置'),@('06-offline','设备 · 离线'),
    @('07-themes-image','主题 · 图片'),@('08-themes-dashboard','主题 · 仪表盘'),
    @('09-monitoring','诊断 · 电脑监测（扩展）'),@('10-system-info','诊断 · 系统规格（扩展）'),
    @('11-states','操作反馈与确认'),@('12-compact','640 × 400 紧凑布局'),
    @('13-navigation-top','外观 · 顶部导航'),@('14-navigation-side','外观 · 侧边导航'),
    @('15-device-selection','选择设备 · 请明确选择要连接的设备'),
    @('16-connecting','连接设备 · 正在读取设备状态'),
    @('17-screen-mode','设备 · 副屏模式已确认'),
    @('18-power-off','设备 · 设备已熄屏'),
    @('19-stream-paused','设备 · 主题推流已暂停'),
    @('20-mode-switching','切换模式 · 正在等待设备重新连接'),
    @('21-mode-unknown','切换模式 · 尚无法确认设备当前模式'),
    @('22-recovering','恢复连接 · 等待原设备重新连接'),
    @('23-recovery-failed','连接故障 · 设备恢复未完成'),
    @('24-tray','托盘与窗口 · 窗口已收至托盘'),
    @('25-exit','退出应用 · 退出切回副屏未完成'),
    @('26-update-check','软件更新 · 正在检查更新'),
    @('27-update-available','软件更新 · 发现新版本 1.1.0（示例）'),
    @('28-update-download','软件更新 · 已下载 18 / 42 MB · 43%'),
    @('29-update-ready','软件更新 · 更新已准备就绪'),
    @('30-update-failure','软件更新 · 检查或安装未完成'),
    @('31-update-confirmed','软件更新 · 已更新至 1.1.0'),
    @('32-metrics-stale','诊断 · 部分数据不可用或已过期')
)
$links = ($pages | ForEach-Object { "<a href='#$($_[0])'>$($_[1])</a>" }) -join "`n"
$sheets = ($pages | ForEach-Object { "<section id='$($_[0])'><h2>$($_[1])</h2><p><a href='$($_[0]).svg'>可编辑 SVG</a> · <a href='$($_[0]).png'>原尺寸 PNG</a></p><img src='$($_[0]).png' alt='$($_[1])设计稿' loading='lazy'></section>" }) -join "`n"
$html = @"
<!doctype html><html lang='zh-CN'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
<title>USB Display 深色 UI 设计稿</title><style>
*{box-sizing:border-box}body{margin:0;background:#000000;color:#f3f5f7;font:14px/1.7 'Segoe UI','Microsoft YaHei UI',sans-serif}header,main{max-width:1240px;margin:auto;padding:28px}h1{font-size:30px}h2{font-size:22px}p{color:#b2bbc4}a{color:#149CFF;text-decoration:none}a:focus-visible{outline:2px solid #149CFF}nav{display:flex;flex-wrap:wrap;gap:10px 24px}section{margin:24px 0 64px;scroll-margin-top:24px}img{max-width:100%;height:auto;border:1px solid #363c43;border-radius:8px}header{border-bottom:1px solid #363c43}nav a:hover{text-decoration:underline}
</style><header><h1>USB Display / 黑灰与亮蓝</h1><p>设计提案 v1.2 · 32 张设计稿 · 纯黑背景 / 少量亮蓝 / 可配置导航 / WPF UI</p><p>静态设计方案，示例数据不表示设备运行状态；不调用设备或后端。</p><p><a href='新版UI设计方案.md'>完整设计规范</a> · <a href='交付检查.md'>交付检查</a></p><nav>$links</nav></header><main>$sheets</main></html>
"@
[System.IO.File]::WriteAllText((Join-Path $outDir 'index.html'),$html,[System.Text.UTF8Encoding]::new($false))
Write-Output '已生成 32 张 PNG、32 张 SVG 和设计稿总览。'
