// 定位本机 DSH 会话存档里与某个关键词相关的会话。
// 会话文件是按帧追加的 zstd 流（每帧一个 JSON 对象），必须逐帧解压，
// 不能用 zstdDecompressSync 一次解整文件（只会解出第一帧）。
// 用法:
//   node scripts\_scan_sessions.mjs <sessionsDir> <关键词1> [关键词2 ...]
// 例:
//   node scripts\_scan_sessions.mjs "C:\Users\<你>\.dsh\sessions\--...--" PixivRenamer SauceNAO
// 配套：scripts\_dump_sessions.mjs 用来把命中的会话导出成可读 Markdown 转录。
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { zstdDecompressSync } from 'node:zlib';

const MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);

/** 把按 zstd 帧拼接的文件拆成多段并逐帧解压 */
export function decompressFrames(buf) {
  const out = [];
  let i = 0;
  while (i < buf.length) {
    const idx = buf.indexOf(MAGIC, i);
    if (idx === -1) break;
    const next = buf.indexOf(MAGIC, idx + 4);
    const chunk = buf.subarray(idx, next === -1 ? buf.length : next);
    try {
      out.push(zstdDecompressSync(chunk).toString('utf8'));
    } catch {
      for (let end = chunk.length - 1; end > 4; end--) {
        try { out.push(zstdDecompressSync(chunk.subarray(0, end)).toString('utf8')); break; } catch { /* 继续缩短 */ }
      }
    }
    if (next === -1) break;
    i = next;
  }
  return out.join('');
}

const [dir, ...keywords] = process.argv.slice(2);
if (!dir || keywords.length === 0) {
  console.log('用法：node scripts/_scan_sessions.mjs <sessionsDir> <关键词...>');
  process.exit(2);
}

const rows = [];
for (const name of readdirSync(dir)) {
  const full = join(dir, name);
  let st;
  try { st = statSync(full); } catch { continue; }
  if (!st.isDirectory()) continue;
  let text = '';
  try {
    text = decompressFrames(readFileSync(join(full, 'session.v4.jsonl.zstd')));
  } catch (e) {
    rows.push({ id: name, error: String((e && e.message) || e) });
    continue;
  }
  const lines = text.split('\n').filter(Boolean);
  const counts = {};
  for (const kw of keywords) {
    let n = 0, from = 0;
    for (;;) {
      const at = text.indexOf(kw, from);
      if (at === -1) break;
      n++; from = at + kw.length;
    }
    counts[kw] = n;
  }
  const first = lines.find((l) => l.includes('"type":"session"'));
  let created = '';
  try { created = new Date(JSON.parse(first).createdAt).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai' }); } catch { /* ignore */ }
  rows.push({
    id: name,
    lines: lines.length,
    mtime: st.mtime.toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai' }),
    created,
    counts,
  });
}

rows.sort((a, b) => {
  const sa = keywords.reduce((s, k) => s + (a.counts?.[k] ?? 0), 0);
  const sb = keywords.reduce((s, k) => s + (b.counts?.[k] ?? 0), 0);
  return sb - sa;
});
for (const r of rows) {
  const hit = keywords.some((k) => (r.counts?.[k] ?? 0) > 0);
  if (!hit && !r.error) continue;
  console.log(`${r.id}\t行 ${r.lines}\t创建 ${r.created}\t` +
    keywords.map((k) => `${k}=${r.counts?.[k] ?? 0}`).join(' ') + (r.error ? `\t错误 ${r.error}` : ''));
}
