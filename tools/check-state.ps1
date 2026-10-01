$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$std = "$env:LOCALAPPDATA\ScreenTime"
$ws  = 'C:\Users\MR\Desktop\ScreenTime'

Write-Output '=== 日志中的缺口回填记录 ==='
$gap = Get-Content "$std\app.log" -Encoding UTF8 -ErrorAction SilentlyContinue |
       Select-String -Pattern '缺口|回填'
if ($gap) { $gap | Select-Object -Last 8 | ForEach-Object { '  ' + $_.Line } }
else { Write-Output '  （从未检测到缺口 —— 说明睡眠/休眠的回填逻辑没被触发过）' }

Write-Output ''
Write-Output '=== 状态分布 ==='
& 'C:\Program Files\nodejs\node.exe' -e "
const {DatabaseSync}=require('node:sqlite');
const db=new DatabaseSync(process.argv[1],{readOnly:true});
const S={0:'活跃',1:'空闲',2:'锁屏',3:'熄屏/睡眠'};
const rows=db.prepare('SELECT state, SUM(end_utc-start_utc) s, COUNT(*) c FROM session GROUP BY state').all();
if(rows.length===0) console.log('  （数据库为空）');
for(const r of rows) console.log('  ' + (S[r.state]||('state='+r.state)) + ' : ' + r.s + ' 秒 / ' + r.c + ' 段');
const t=db.prepare('SELECT SUM(end_utc-start_utc) s FROM session').get();
console.log('  总计: ' + (t.s||0) + ' 秒');
db.close();
" "$std\usage.db" 2>&1 | ForEach-Object { $_ }

Write-Output ''
Write-Output '=== 时间轴覆盖情况（最近 3 小时是否连续）==='
& 'C:\Program Files\nodejs\node.exe' -e "
const {DatabaseSync}=require('node:sqlite');
const db=new DatabaseSync(process.argv[1],{readOnly:true});
const t=u=>new Date(u*1000).toLocaleTimeString('zh-CN',{hour12:false});
const rows=db.prepare('SELECT start_utc,end_utc,state FROM session ORDER BY start_utc DESC LIMIT 12').all();
if(rows.length===0) console.log('  （无数据）');
for(const r of rows.reverse()) console.log('  ' + t(r.start_utc) + ' -> ' + t(r.end_utc) + '  ' + String(r.end_utc-r.start_utc).padStart(5) + 's  state=' + r.state);
db.close();
" "$std\usage.db" 2>&1 | ForEach-Object { $_ }

Write-Output ''
Write-Output '=== 进程 ==='
Get-Process -Name 'ScreenTime.App' -ErrorAction SilentlyContinue |
  Select-Object Id, @{n='启动';e={$_.StartTime.ToString('HH:mm:ss')}} | Format-Table -AutoSize
Write-Output ('现在: ' + (Get-Date -Format 'HH:mm:ss'))
