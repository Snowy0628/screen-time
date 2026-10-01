// 分析"未归属"片段（app_id 为 NULL）的成因
import { DatabaseSync } from 'node:sqlite';

const db = new DatabaseSync(process.argv[2], { readOnly: true });
const t = (u) => new Date(u * 1000).toLocaleTimeString('zh-CN', { hour12: false });

console.log('=== 未归属片段时长分布 ===');
const buckets = [
  ['< 3 秒', 'd < 3'],
  ['3–10 秒', 'd >= 3 AND d < 10'],
  ['10–60 秒', 'd >= 10 AND d < 60'],
  ['1–5 分钟', 'd >= 60 AND d < 300'],
  ['>= 5 分钟', 'd >= 300'],
];
const total = db.prepare('SELECT COUNT(*) c FROM session WHERE state = 0 AND app_id IS NULL').get().c;
const totalSec = db.prepare('SELECT SUM(end_utc - start_utc) s FROM session WHERE state = 0 AND app_id IS NULL').get().s ?? 0;
console.log(`  合计 ${total} 条，共 ${totalSec} 秒`);
for (const [label, cond] of buckets) {
  const r = db.prepare(
    `SELECT COUNT(*) c, COALESCE(SUM(end_utc-start_utc),0) s
     FROM (SELECT end_utc-start_utc AS d, end_utc, start_utc FROM session
           WHERE state = 0 AND app_id IS NULL)
     WHERE ${cond}`
  ).get();
  const pct = total ? Math.round(r.c * 100 / total) : 0;
  console.log(`  ${label.padEnd(10)} ${String(r.c).padStart(5)} 条 (${String(pct).padStart(3)}%)  ${r.s} 秒`);
}

console.log('');
console.log('=== 最长的 10 条未归属片段 ===');
for (const r of db.prepare(`
  SELECT start_utc, end_utc FROM session
  WHERE state = 0 AND app_id IS NULL
  ORDER BY (end_utc - start_utc) DESC LIMIT 10
`).all()) {
  console.log(`  ${t(r.start_utc)} -> ${t(r.end_utc)}  ${r.end_utc - r.start_utc}s`);
}

console.log('');
console.log('=== 对照：已归属片段的时长分布 ===');
const okMed = db.prepare('SELECT AVG(end_utc-start_utc) a FROM session WHERE state=0 AND app_id IS NOT NULL').get().a ?? 0;
const nullMed = db.prepare('SELECT AVG(end_utc-start_utc) a FROM session WHERE state=0 AND app_id IS NULL').get().a ?? 0;
console.log(`  已归属平均时长: ${okMed.toFixed(1)} 秒`);
console.log(`  未归属平均时长: ${nullMed.toFixed(1)} 秒`);

db.close();
