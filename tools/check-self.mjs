// 检查程序是否能把"自己"识别为应用（而不是记成未知）
import { DatabaseSync } from 'node:sqlite';

const db = new DatabaseSync(process.argv[2], { readOnly: true });
const t = (u) => new Date(u * 1000).toLocaleTimeString('zh-CN', { hour12: false });

console.log('=== app 表（最近 10 条）===');
for (const a of db.prepare('SELECT id, display, process FROM app ORDER BY id DESC LIMIT 10').all()) {
  console.log(`  #${a.id}  ${a.display}   [${a.process}]`);
}

const self = db.prepare("SELECT COUNT(*) c FROM app WHERE display LIKE '%屏幕使用时间%'").get().c;
console.log('');
console.log(`识别出「屏幕使用时间」自身的记录数: ${self}`);

console.log('');
console.log('=== 最近 8 个活跃片段（看 app_id 是否为空）===');
const rows = db.prepare(`
  SELECT s.start_utc, s.end_utc, a.display
  FROM session s LEFT JOIN app a ON a.id = s.app_id
  WHERE s.state = 0
  ORDER BY s.start_utc DESC LIMIT 8
`).all();
for (const r of rows) {
  const dur = r.end_utc - r.start_utc;
  console.log(`  ${t(r.start_utc)} -> ${t(r.end_utc)}  ${String(dur).padStart(4)}s  ${r.display ?? '(未知)'}`);
}

console.log('');
console.log('=== 归属统计 ===');
const total = db.prepare('SELECT COUNT(*) c FROM session WHERE state = 0').get().c;
const nulls = db.prepare('SELECT COUNT(*) c FROM session WHERE state = 0 AND app_id IS NULL').get().c;
console.log(`  活跃片段 ${total} 条，其中未归属 ${nulls} 条 (${total ? Math.round(nulls * 100 / total) : 0}%)`);

db.close();
