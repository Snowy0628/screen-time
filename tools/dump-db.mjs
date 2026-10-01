// 独立数据库检查：用 node:sqlite 直接读，不复用应用自身的代码，
// 避免"用自己的代码验证自己"的循环论证。
import { DatabaseSync } from 'node:sqlite';

const path = process.argv[2];
if (!path) {
  console.error('用法: node dump-db.mjs <usage.db>');
  process.exit(2);
}

const db = new DatabaseSync(path, { readOnly: true });
const STATES = { 0: '活跃', 1: '空闲', 2: '锁屏', 3: '熄屏' };
const t = (u) => new Date(u * 1000).toLocaleTimeString('zh-CN', { hour12: false });
const d = (u) => new Date(u * 1000).toLocaleDateString('zh-CN');

const count = (sql) => {
  try { return db.prepare(sql).get().c; } catch { return 'n/a'; }
};

console.log('=== 数据库概览 ===');
console.log('路径        :', path);
console.log('session 行数:', count('SELECT COUNT(*) c FROM session'));
console.log('app 数      :', count('SELECT COUNT(*) c FROM app'));
console.log('心跳        :', count('SELECT COUNT(*) c FROM heartbeat'));
console.log('day_summary :', count('SELECT COUNT(*) c FROM day_summary'));

console.log();
console.log('=== 用户表 ===');
try {
  for (const r of db.prepare('SELECT name FROM sqlite_master WHERE type=? ORDER BY name').all('table')) {
    console.log('  ' + r.name);
  }
} catch (e) { console.log('  读取失败:', e.message); }

console.log();
console.log('=== session 明细 ===');
try {
  const rows = db.prepare(
    'SELECT start_utc, end_utc, state, app_id FROM session ORDER BY start_utc'
  ).all();
  if (rows.length === 0) {
    console.log('  （无数据）');
  } else {
    for (const r of rows) {
      const dur = r.end_utc - r.start_utc;
      console.log(
        `  ${d(r.start_utc)} ${t(r.start_utc)} -> ${t(r.end_utc)}  ` +
        `${String(dur).padStart(5)}s  ${STATES[r.state] ?? r.state}  app=${r.app_id ?? 'NULL'}`
      );
    }
  }
} catch (e) { console.log('  读取失败:', e.message); }

console.log();
console.log('=== 应用表 ===');
try {
  const apps = db.prepare('SELECT id, display FROM app ORDER BY id').all();
  if (apps.length === 0) console.log('  （无数据）');
  for (const a of apps) console.log(`  #${a.id}  ${a.display}`);
} catch (e) { console.log('  读取失败:', e.message); }

console.log();
console.log('=== 连续性检查 ===');
try {
  const rows = db.prepare('SELECT start_utc, end_utc FROM session ORDER BY start_utc').all();
  let gaps = 0, overlaps = 0, negatives = 0;
  for (let i = 0; i < rows.length; i++) {
    if (rows[i].end_utc < rows[i].start_utc) negatives++;
    if (i > 0) {
      const prev = rows[i - 1].end_utc, cur = rows[i].start_utc;
      if (cur > prev + 1) gaps++;
      if (cur < prev - 1) overlaps++;
    }
  }
  console.log(`  空洞 ${gaps} / 重叠 ${overlaps} / 负时长 ${negatives}`);
  if (rows.length > 0) {
    const first = rows[0].start_utc, last = rows[rows.length - 1].end_utc;
    console.log(`  覆盖区间: ${t(first)} -> ${t(last)}  共 ${last - first}s`);
  }
} catch (e) { console.log('  读取失败:', e.message); }

db.close();
