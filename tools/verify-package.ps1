$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ws  = 'C:\Users\MR\Desktop\ScreenTime'
$pkg = Join-Path $ws 'package\屏幕使用时间-安装包'
$dst = 'C:\Users\MR\ScreenTimeApp'
$std = Join-Path $env:LOCALAPPDATA 'ScreenTime'

# 本轮所有修复：用源码里的特征字符串确认，再确认产物与源码同一构建
$fixes = @(
    @{ Name = '静默启动创建窗口对象（托盘双击修复）'; Marker = '窗口对象已就绪'; Source = 'src\ScreenTime.App\Program.cs' },
    @{ Name = '窗口未挂接时告警（不再静默失败）';     Marker = '唤出主界面失败：窗口对象未挂接'; Source = 'src\ScreenTime.App\TrayContext.cs' },
    @{ Name = '开机自启管理';                         Marker = '登录后自动启动'; Source = 'src\ScreenTime.App\MainWindow.xaml' },
    @{ Name = '自启用计划任务（COM）';                Marker = 'Schedule.Service'; Source = 'src\ScreenTime.App\AutoStart.cs' },
    @{ Name = '周/月视图自递归修复';                  Marker = '((int)d.DayOfWeek + 6) % 7'; Source = 'src\ScreenTime.App\ViewModels\RangeViewModel.cs' },
    @{ Name = '单日标题不再残留月视图内容';           Marker = 'yyyy年M月d日 dddd'; Source = 'src\ScreenTime.App\ViewModels\RangeViewModel.cs' },
    @{ Name = '主题选择持久化';                       Marker = 'AppSettings.Current.Theme'; Source = 'src\ScreenTime.App\ThemeManager.cs' },
    @{ Name = '图标主色上色';                         Marker = 'GetDominantColor'; Source = 'src\ScreenTime.App\AppIconCache.cs' },
    @{ Name = '时间轴无记录补浅灰段';                 Marker = 'SleepGapMinutes'; Source = 'src\ScreenTime.App\ViewModels\DayViewModel.cs' },
    @{ Name = '跨进程睡眠回填';                       Marker = 'BackfillGapSinceLastRun'; Source = 'src\ScreenTime.Core\Recorder.cs' },
    @{ Name = '界面 1 秒刷新';                        Marker = 'TimeSpan.FromSeconds(1)'; Source = 'src\ScreenTime.App\MainWindow.xaml.cs' },
    @{ Name = '时长格式（不足 1 小时显示秒）';        Marker = 'if (h > 0) return $"{h} 小时 {m} 分";'; Source = 'src\ScreenTime.App\ViewModels\DayViewModel.cs' }
)

Write-Output '=== 修复项逐条核对 ==='
$allOk = $true
foreach ($f in $fixes) {
    $srcPath = Join-Path $ws $f.Source
    $hasMarker = $false
    if (Test-Path $srcPath) {
        $hasMarker = (Get-Content $srcPath -Raw -Encoding UTF8).Contains($f.Marker)
    }
    $mark = if ($hasMarker) { '✓' } else { '✗'; }
    if (-not $hasMarker) { $allOk = $false }
    Write-Output ("  {0} {1}" -f $mark, $f.Name)
}

Write-Output ''
Write-Output '=== 产物与源码一致性 ==='
$srcNewest = Get-ChildItem (Join-Path $ws 'src') -Recurse -File -Include *.cs,*.xaml |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$exeTime = (Get-Item (Join-Path $dst 'ScreenTime.App.exe')).LastWriteTime
Write-Output ("  最新的源文件 : {0}  {1}" -f $srcNewest.Name, $srcNewest.LastWriteTime.ToString('MM-dd HH:mm:ss'))
Write-Output ("  已部署的 exe : {0}" -f $exeTime.ToString('MM-dd HH:mm:ss'))
if ($exeTime -gt $srcNewest.LastWriteTime) {
    Write-Output '  ✓ exe 比所有源文件都新，说明包含全部改动'
} else {
    Write-Output '  ✗ 有源文件比 exe 更新，可能需要重新构建'
    $allOk = $false
}

Write-Output ''
Write-Output '=== 安装包结构 ==='
$zip = Join-Path $ws 'package\屏幕使用时间-安装包.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [System.IO.Compression.ZipFile]::OpenRead($zip)
Write-Output ("  zip 条目数: {0}" -f $z.Entries.Count)
$z.Entries | Sort-Object FullName | ForEach-Object {
    Write-Output ("    {0,-34} {1,8:N2} MB" -f $_.FullName, ($_.Length / 1MB))
}
$z.Dispose()

Write-Output ''
Write-Output '=== 实际运行状态 ==='
$p = Get-Process -Name 'ScreenTime.App' -ErrorAction SilentlyContinue
if ($p) {
    Write-Output ("  进程: PID={0}  启动于 {1}  内存 {2} MB" -f $p.Id, $p.StartTime.ToString('HH:mm:ss'), [math]::Round($p.WorkingSet64 / 1MB, 1))
} else {
    Write-Output '  （未运行）'
}
$ts = Join-Path $std 'tray-status.txt'
if (Test-Path $ts) {
    Get-Content $ts -Encoding UTF8 | Select-String -Pattern '结论|完整性级别' | ForEach-Object { '  ' + $_.Line.Trim() }
}
Write-Output ("  开机自启: 计划任务={0} 启动文件夹={1}" -f `
    (Test-Path "$env:SystemRoot\System32\Tasks\ScreenTimeAutoStart"), `
    (Test-Path (Join-Path ([Environment]::GetFolderPath('Startup')) '屏幕使用时间.lnk')))

Write-Output ''
Write-Output $(if ($allOk) { '=== 全部核对通过，可以发出 ===' } else { '=== 有项目未通过，见上面 ✗ ===' })
