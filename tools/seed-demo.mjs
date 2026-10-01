// 造一份带完整状态变化的测试数据（活跃/空闲/锁屏/熄屏齐全），
// 用于验证时间轴与柱状图的渲染，而不必等真实使用一天。
//
// 只写入独立的数据目录，绝不碰正式数据。
import { DatabaseSync } from 'node:sqlite';
import { mkdirSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2];
if (!dir) {
  console.error('用法: node seed-demo.mjs <数据目录>');
  process.exit(2);
}

rmSync(dir, { recursive: true, force: true });
mkdirSync(dir, { recursive: true });

const db = new DatabaseSync(join(dir, 'usage.db'));

// ---- 建表（必须与 UsageStore.InitSchema 完全一致，否则应用打不开）----
db.exec(`
CREATE TABLE IF NOT EXISTS meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS app (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  file_path   TEXT NOT NULL,
  process     TEXT NOT NULL,
  display     TEXT NOT NULL,
  created_utc INTEGER NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_app_path ON app(file_path);
CREATE INDEX IF NOT EXISTS ix_app_display ON app(display);

CREATE TABLE IF NOT EXISTS session (
  id        INTEGER PRIMARY KEY AUTOINCREMENT,
  start_utc INTEGER NOT NULL,
  end_utc   INTEGER NOT NULL,
  day_key   TEXT    NOT NULL,
  state     INTEGER NOT NULL,
  app_id    INTEGER NULL REFERENCES app(id),
  tz_offset INTEGER NOT NULL,
  CHECK (end_utc > start_utc),
  CHECK (state BETWEEN 0 AND 3)
);
CREATE INDEX IF NOT EXISTS ix_session_start ON session(start_utc);
CREATE INDEX IF NOT EXISTS ix_session_day   ON session(day_key, app_id);

CREATE TABLE IF NOT EXISTS day_summary (
  day_key TEXT    NOT NULL,
  app_id  INTEGER NOT NULL,
  seconds INTEGER NOT NULL,
  PRIMARY KEY (day_key, app_id)
);

CREATE TABLE IF NOT EXISTS heartbeat (
  ts_utc        INTEGER PRIMARY KEY,
  spans_written INTEGER NOT NULL,
  note          TEXT NOT NULL DEFAULT ''
);
`);

// ---- 造几个"应用"（用真实存在的程序，图标才取得到）----
const apps = [
  ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', 'msedge', 'Microsoft Edge'],
  ['C:\\Windows\\System32\\notepad.exe', 'notepad', 'Notepad'],
  ['C:\\Windows\\explorer.exe', 'explorer', 'Windows Explorer'],
  ['C:\\Windows\\System32\\mspaint.exe', 'mspaint', 'Paint'],
];
const insApp = db.prepare(
  'INSERT OR IGNORE INTO app (file_path, process, display, created_utc) VALUES (?, ?, ?, ?)'
);
for (const [p, proc, n] of apps) insApp.run(p, proc, n, Math.floor(Date.now() / 1000));

const ids = db.prepare('SELECT id, display FROM app ORDER BY id').all();
const idOf = (name) => ids.find((r) => r.display === name)?.id ?? null;

// ---- 今天的本地零点（本地时区）----
const now = new Date();
const midnight = new Date(now.getFullYear(), now.getMonth(), now.getDate(), 0, 0, 0, 0);
const dayStart = Math.floor(midnight.getTime() / 1000);
const nowSec = Math.floor(now.getTime() / 1000);
const dayEnd = dayStart + 86400;
const until = Math.min(nowSec, dayEnd);

const STATE = { active: 0, idle: 1, locked: 2, off: 3 };

// ---- 一天的剧本：[起始小时, 持续分钟, 状态, 应用] ----
const script = [
  [0, 60, 'off', null],          // 00:00-01:00 熄屏
  [1, 420, 'off', null],         // 01:00-08:00 熄屏（长段）
  [8, 25, 'active', 'Microsoft Edge'],
  [8.42, 10, 'idle', null],
  [8.58, 40, 'active', 'Notepad'],
  [9.25, 15, 'active', 'Microsoft Edge'],
  [9.5, 30, 'locked', null],     // 锁屏半小时
  [10, 45, 'active', 'Microsoft Edge'],
  [10.75, 20, 'active', 'Paint'],
  [11.08, 35, 'idle', null],
  [11.67, 50, 'active', 'Microsoft Edge'],
  [12.5, 60, 'off', null],       // 午休熄屏
  [13.5, 40, 'active', 'Notepad'],
  [14.17, 25, 'active', 'Microsoft Edge'],
  [14.58, 45, 'locked', null],
  [15.33, 55, 'active', 'Microsoft Edge'],
  [16.25, 30, 'idle', null],
];

const tzOffsetMin = -new Date().getTimezoneOffset();   // 本地时区相对 UTC 的分钟数
const ins = db.prepare(
  'INSERT INTO session (start_utc, end_utc, day_key, state, app_id, tz_offset) VALUES (?, ?, ?, ?, ?, ?)'
);
const dayKey = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
const put = (s, e, state, appId) => ins.run(s, e, dayKey, state, appId, tzOffsetMin);

const fmt = (u) => new Date(u * 1000).toLocaleTimeString('zh-CN', { hour12: false });
let written = 0;
const totals = { active: 0, idle: 0, locked: 0, off: 0 };

for (const [startH, durMin, state, appName] of script) {
  const s = dayStart + Math.round(startH * 3600);
  const e = Math.min(s + Math.round(durMin * 60), until);
  if (e <= s) continue;

  // 活跃段拆成多个应用片段，让柱状图有堆叠效果
  if (state === 'active') {
    const parts = Math.max(1, Math.round(durMin / 18));
    const step = Math.floor((e - s) / parts);
    let cur = s;
    for (let i = 0; i < parts; i++) {
      const segEnd = i === parts - 1 ? e : cur + step;
      const name = appName ?? 'Microsoft Edge';
      put(cur, segEnd, STATE.active, idOf(name));
      totals.active += segEnd - cur;
      written++;
      cur = segEnd;
    }
  } else {
    put(s, e, STATE[state], null);
    totals[state] += e - s;
    written++;
  }
}

// ---- 把最后一段接到"现在"，保证时间轴一直画到当前时刻 ----
const last = db.prepare('SELECT MAX(end_utc) m FROM session').get().m ?? dayStart;
if (until > last) {
  put(last, until, STATE.idle, null);
  totals.idle += until - last;
  written++;
}

db.prepare('INSERT OR REPLACE INTO heartbeat (ts_utc, spans_written, note) VALUES (?, ?, ?)')
  .run(nowSec, written, 'seed');
db.prepare('INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)').run('schema_version', '2');
db.prepare('INSERT OR REPLACE INTO meta (key, value) VALUES (?, ?)').run('seeded', 'demo');

// day_summary 是 (day_key, app_id, seconds)：按应用汇总当天秒数
const sum = db.prepare(
  `INSERT OR REPLACE INTO day_summary (day_key, app_id, seconds)
   SELECT day_key, COALESCE(app_id, 0), SUM(end_utc - start_utc)
   FROM session GROUP BY day_key, COALESCE(app_id, 0)`
);
sum.run();

db.close();

console.log('已生成演示数据:', dir);
console.log('  日期    :', dayKey);
console.log('  写入段数:', written);
console.log('  活跃    :', totals.active, '秒');
console.log('  空闲    :', totals.idle, '秒');
console.log('  锁屏    :', totals.locked, '秒');
console.log('  熄屏    :', totals.off, '秒');
console.log('  覆盖到  :', fmt(until));
