// 用 Node 内置 node:sqlite 独立核对采集数据库
// 目的：用与 C# 完全无关的工具读同一份数据，验证落库正确性
import { DatabaseSync } from 'node:sqlite';

const dbPath = process.argv[2];
if (!dbPath) {
  console.error('用法: node verify-db.mjs <数据库路径>');
  process.exit(2);
}

const db = new DatabaseSync(dbPath, { readOnly: false });

const fmt = (s) => {
  s = Math.max(0, Math.round(s));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return h > 0 ? `${h}h${m}m` : m > 0 ? `${m}m${sec}s` : `${sec}s`;
};

console.log('=== schema ===');
const tables = db.prepare(
  "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
).all();
console.log('  表:', tables.map(t => t.name).join(', '));
console.log('  schema_version =', db.prepare("SELECT value FROM meta WHERE key='schema_version'").get()?.value);

console.log('\n=== app 表 ===');
const apps = db.prepare('SELECT id, file_path, process, display FROM app ORDER BY id').all();
if (apps.length === 0) console.log('  （空）');
for (const a of apps) {
  console.log(`  #${a.id}  ${a.display}  (${a.process})`);
  console.log(`       ${a.file_path}`);
}

console.log('\n=== session 表 ===');
const sessions = db.prepare(`
  SELECT s.id, s.start_utc, s.end_utc, s.state, s.day_key, s.app_id, a.display AS app_display
  FROM session s LEFT JOIN app a ON a.id = s.app_id
  ORDER BY s.start_utc
`).all();

const STATE = { 0: '活跃', 1: '空闲', 2: '锁屏', 3: '熄屏/睡眠' };
const localTime = (utc) => new Date(utc * 1000).toLocaleTimeString('zh-CN', { hour12: false });

let problems = [];
let warnings = [];
let prevEnd = null;
let unattributed = 0;
let totalActive = 0;

for (const s of sessions) {
  const dur = s.end_utc - s.start_utc;
  const label = STATE[s.state] ?? `未知(${s.state})`;
  console.log(`  #${s.id}  ${localTime(s.start_utc)} → ${localTime(s.end_utc)}  ${String(dur).padStart(5)}s  ${label.padEnd(8)} ${s.app_display ?? '-'}`);

  // 一致性检查
  if (dur <= 0) problems.push(`session#${s.id} 时长非正: ${dur}s`);
  if (prevEnd !== null && s.start_utc < prevEnd) problems.push(`session#${s.id} 与前一行时间重叠`);
  prevEnd = s.end_utc;

  // 活跃但无归属：设计内允许（前台是桌面/开始菜单等壳窗口），但要盯住比例
  if (s.state === 0) {
    totalActive += dur;
    if (s.app_id === null) unattributed += dur;
  }
}

// 无归属比例过高说明应用解析可能失效（例如 UWP 解包坏了、权限不足）
const unattributedRatio = totalActive > 0 ? unattributed / totalActive : 0;
console.log(`\n  活跃总时长 ${totalActive}s，其中无应用归属 ${unattributed}s（${(unattributedRatio * 100).toFixed(1)}%）`);
if (unattributedRatio > 0.3) {
  problems.push(`无归属活跃时间占比 ${(unattributedRatio * 100).toFixed(1)}% 过高，应用解析可能异常`);
} else if (unattributed > 0) {
  warnings.push(`存在 ${unattributed}s 无归属活跃时间（桌面/系统壳窗口，属正常）`);
}

console.log('\n=== 按状态汇总（全部数据）===');
const totals = db.prepare(`
  SELECT state, SUM(end_utc - start_utc) AS secs, COUNT(*) AS rows
  FROM session GROUP BY state ORDER BY state
`).all();
let grand = 0;
for (const t of totals) {
  grand += t.secs;
  console.log(`  ${(STATE[t.state] ?? t.state).padEnd(10)} ${String(t.secs).padStart(6)}s  (${fmt(t.secs)})  ${t.rows} 行`);
}
console.log(`  ${'合计'.padEnd(10)} ${String(grand).padStart(6)}s  (${fmt(grand)})`);

console.log('\n=== 应用排行（活跃）===');
const ranks = db.prepare(`
  SELECT a.display, SUM(s.end_utc - s.start_utc) AS secs
  FROM session s JOIN app a ON a.id = s.app_id
  WHERE s.state = 0 GROUP BY a.id ORDER BY secs DESC
`).all();
if (ranks.length === 0) console.log('  （无）');
for (const r of ranks) console.log(`  ${String(r.secs).padStart(6)}s  ${fmt(r.secs).padStart(8)}  ${r.display}`);

console.log('\n=== heartbeat ===');
const hb = db.prepare('SELECT COUNT(*) AS n, MAX(ts_utc) AS last FROM heartbeat').get();
console.log(`  心跳次数 ${hb.n}，最近一次 ${hb.last ? new Date(hb.last * 1000).toLocaleString('zh-CN') : '无'}`);

// 缺口检查：相邻两行之间是否有未记录的时间空洞
console.log('\n=== 时间连续性 ===');
if (sessions.length > 1) {
  let gaps = 0;
  for (let i = 1; i < sessions.length; i++) {
    const gap = sessions[i].start_utc - sessions[i - 1].end_utc;
    if (gap > 0) { gaps++; if (gaps <= 5) console.log(`  #${sessions[i-1].id}→#${sessions[i].id} 之间存在 ${gap}s 空洞`); }
  }
  console.log(gaps === 0 ? '  无空洞，时间轴连续 ✓' : `  共 ${gaps} 处空洞`);
} else {
  console.log('  行数不足，跳过');
}

console.log('\n=== 结论 ===');
if (warnings.length > 0) {
  console.log('  提示:');
  for (const w of warnings) console.log('   ~ ' + w);
}
if (problems.length === 0) {
  console.log('  数据一致性检查通过 ✓');
} else {
  console.log('  发现问题:');
  for (const p of problems) console.log('   - ' + p);
}
db.close();
process.exit(problems.length === 0 ? 0 : 1);
