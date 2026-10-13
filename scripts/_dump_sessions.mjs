// 临时工具：把 DSH 会话存档（按 zstd 帧追加的 jsonl 流）导出成可读的 Markdown 转录。
//
// 会话文件是多个连续 zstd 帧的拼接（每帧 magic 28 b5 2f fd），
// 不能用 zstdDecompressSync 一次解整个文件（只会解出第一帧），
// 必须按 magic 切帧、逐帧解压再拼接。
//
// 记录类型（v4）：
//   session                  会话元信息（createdAt / cwd / agentPreset）
//   user/message             data.content[] 里 type=text，data.source.kind=user 才是真人输入
//   assistant/message        data.message.content[] 里 type=text（正文）/ reasoning / tool-call
//   tool/call, tool/result   工具调用与结果（默认只保留一行摘要，避免噪声）
//   agent/inbox/spliced      客户端原始投递的用户消息（与 user/message 重复，默认不重复输出）
//
// 用法:
//   node _dump_sessions.mjs --sessions <sessionsDir> --out <outDir> [--ids a,b,c] [--tools]
import { mkdirSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { zstdDecompressSync } from 'node:zlib';

const MAGIC = Buffer.from([0x28, 0xb5, 0x2f, 0xfd]);
const TZ_OFFSET_MIN = 8 * 60; // UTC+8

/** 把按 zstd 帧拼接的文件拆成多段并逐帧解压 */
function decompressFrames(buf) {
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
      // 单帧失败时尝试只取到下一个 magic 之前的最大可解压前缀
      for (let end = chunk.length - 1; end > 4; end--) {
        try { out.push(zstdDecompressSync(chunk.subarray(0, end)).toString('utf8')); break; } catch { /* retry */ }
      }
    }
    if (next === -1) break;
    i = next;
  }
  return { text: out.join(''), frames: out.length };
}

function fmtTime(ms) {
  if (typeof ms !== 'number' || !Number.isFinite(ms)) return '';
  const d = new Date(ms + TZ_OFFSET_MIN * 60 * 1000);
  const p = (n, w = 2) => String(n).padStart(w, '0');
  return `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())} ${p(d.getUTCHours())}:${p(d.getUTCMinutes())}:${p(d.getUTCSeconds())}`;
}

function blocksOf(msg) {
  const c = msg?.content;
  if (Array.isArray(c)) return c;
  if (typeof c === 'string') return [{ type: 'text', text: c }];
  return [];
}

const textOf = (blocks) => blocks.filter((b) => b?.type === 'text' && typeof b.text === 'string').map((b) => b.text).join('\n').trim();

/** 一行摘要：去掉 markdown 噪声，压成单行，截断 */
function oneLine(s, max = 150) {
  let t = String(s ?? '').replace(/\r/g, '');
  t = t.replace(/```[\s\S]*?```/g, ' 〔代码块〕 ');
  t = t.replace(/^\s{0,3}#{1,6}\s+/gm, '');
  t = t.replace(/\*\*(.+?)\*\*/g, '$1').replace(/`([^`]*)`/g, '$1');
  t = t.replace(/\[([^\]]*)\]\([^)]*\)/g, '$1');
  t = t.replace(/^\s*[-*+]\s+/gm, '');
  t = t.replace(/^\s*\|.*$/gm, ' '); // 表格行丢弃
  t = t.replace(/\s+/g, ' ').trim();
  if (t.length > max) t = t.slice(0, max) + '…';
  return t || '（空）';
}

const args = process.argv.slice(2);
const opt = { sessions: '', out: '', ids: '', tools: false, results: false };
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--sessions') opt.sessions = args[++i];
  else if (args[i] === '--out') opt.out = args[++i];
  else if (args[i] === '--ids') opt.ids = args[++i];
  else if (args[i] === '--tools') opt.tools = true;      // 折叠显示工具调用参数（截断）
  else if (args[i] === '--results') opt.results = true;  // 折叠显示工具结果文本（截断）
  else if (args[i] === '--evidence') { opt.tools = true; opt.results = true; }
}
if (!opt.sessions || !opt.out) {
  console.error('用法: node _dump_sessions.mjs --sessions <dir> --out <dir> [--ids a,b,c] [--tools]');
  process.exit(2);
}

const wanted = opt.ids ? new Set(opt.ids.split(',').map((s) => s.trim()).filter(Boolean)) : null;
const transcriptDir = join(opt.out, '_transcripts');
mkdirSync(transcriptDir, { recursive: true });

const dirs = readdirSync(opt.sessions).filter((n) => {
  if (wanted) return wanted.has(n);
  try { return statSync(join(opt.sessions, n)).isDirectory(); } catch { return false; }
});

const report = [];

for (const id of dirs) {
  const file = join(opt.sessions, id, 'session.v4.jsonl.zstd');
  let raw;
  try { raw = readFileSync(file); } catch { console.error('跳过（无文件）:', id); continue; }
  const { text, frames } = decompressFrames(raw);
  const lines = text.split('\n').filter(Boolean);

  const meta = { id, file, bytes: raw.length, frames, textChars: text.length, rawLines: lines.length, createdAt: null, title: '', cwd: '' };
  const events = [];       // 归一化后的事件流
  const seenUser = new Set();
  const seenAssistant = new Set();
  const callResults = new Map(); // callId -> 工具结果文本（仅用于证据附录）

  for (const line of lines) {
    let o;
    try { o = JSON.parse(line); } catch { continue; }
    if (o.type === 'session') {
      meta.createdAt = o.createdAt ?? null;
      meta.cwd = o.cwd ?? '';
      meta.agentPreset = o.agentPreset ?? '';
      continue;
    }
    if (o.type === 'session/title') {
      if (!meta.title || o.data?.source?.kind !== 'fallback') meta.title = o.data?.title ?? meta.title;
      continue;
    }
    if (o.type === 'user/message') {
      const src = o.data?.source ?? {};
      if (src.kind !== 'user') continue; // 跳过 user-approval / system-reminder 之类注入
      const blocks = blocksOf(o.data);
      const t = textOf(blocks);
      const key = o.data?.id || t;
      if (seenUser.has(key)) continue;
      seenUser.add(key);
      events.push({ kind: 'user', seq: o.seq, time: o.time, turn: null, step: null, blocks, text: t });
      continue;
    }
    if (o.type === 'tool/result') {
      const d = o.data ?? {};
      const cid = d.toolCallId ?? d.message?.toolCallId;
      if (cid) {
        const blocks = blocksOf(d.message);
        callResults.set(cid, blocks.filter((b) => b?.type === 'text' && typeof b.text === 'string').map((b) => b.text).join('\n'));
      }
      continue;
    }
    if (o.type === 'assistant/message') {
      const d = o.data ?? {};
      const msg = d.message ?? {};
      const blocks = blocksOf(msg);
      const key = msg.id || `${d.turn}/${d.step}/${textOf(blocks).slice(0, 80)}`;
      if (seenAssistant.has(key)) continue;
      seenAssistant.add(key);
      events.push({
        kind: 'assistant', seq: o.seq, time: o.time, turn: d.turn ?? null, step: d.step ?? null,
        blocks, text: textOf(blocks),
        tools: blocks.filter((b) => b?.type === 'tool-call').map((b) => b.name),
        reasoningChars: blocks.filter((b) => b?.type === 'reasoning').reduce((n, b) => n + (b.text?.length ?? 0), 0),
      });
      continue;
    }
  }

  // 排序：会话内本来就是追加顺序，seq 稳定，直接保持；这里按 seq 兜底
  events.sort((a, b) => (a.seq ?? 0) - (b.seq ?? 0));

  const first = events.find((e) => e.kind === 'user')?.time ?? meta.createdAt;
  const last = [...events].reverse().find((e) => e.time)?.time ?? meta.createdAt;
  meta.firstTime = first;
  meta.lastTime = last;
  const date = (() => {
    const d = new Date((meta.createdAt ?? first ?? 0) + TZ_OFFSET_MIN * 60 * 1000);
    const p = (n) => String(n).padStart(2, '0');
    return `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())}`;
  })();
  meta.date = date;

  // ---------- 转录 Markdown ----------
  const md = [];
  md.push(`# 会话转录 ${id}`);
  md.push('');
  md.push('| 项 | 值 |');
  md.push('| --- | --- |');
  md.push(`| 会话 ID | \`${id}\` |`);
  md.push(`| 归档源文件 | \`${file}\` |`);
  md.push(`| 起始时间（UTC+8） | ${fmtTime(meta.createdAt ?? first)} |`);
  md.push(`| 最后消息时间（UTC+8） | ${fmtTime(last)} |`);
  md.push(`| 会话标题 | ${meta.title || '（无）'} |`);
  md.push(`| cwd | \`${meta.cwd}\` |`);
  md.push(`| zstd 帧数 / 解压字符数 | ${frames} / ${meta.textChars} |`);
  md.push(`| 提取消息数（user+assistant） | ${events.filter((e) => e.kind === 'user').length} user + ${events.filter((e) => e.kind === 'assistant').length} assistant |`);
  md.push('');
  md.push('> 说明：只保留 `user/message`（`source.kind=user`，即真人输入）与 `assistant/message` 的**文本块**；');
  md.push('> `reasoning`、`tool-call` 参数、`tool/result` 正文、system prompt、审批策略变更等注入消息均已剔除（工具名只在摘要行里保留）。');
  md.push('');
  md.push('## 消息索引（一行摘要）');
  md.push('');
  md.push('| # | seq | 时间(UTC+8) | 轮/步 | 角色 | 摘要 |');
  md.push('| --- | --- | --- | --- | --- | --- |');
  events.forEach((e, i) => {
    const role = e.kind === 'user' ? '用户' : '助手';
    const rt = e.kind === 'assistant' ? `T${e.turn}/S${e.step}${e.tools?.length ? ` · ${[...new Set(e.tools)].join(',')}` : ''}` : '';
    const sum = oneLine(e.text || (e.kind === 'assistant' ? `（仅工具调用：${[...new Set(e.tools ?? [])].join(', ')}）` : '（空）'), 150);
    md.push(`| ${i + 1} | ${e.seq} | ${fmtTime(e.time)} | ${rt} | ${role} | ${sum.replace(/\|/g, '\\|')} |`);
  });
  md.push('');
  md.push('---');
  md.push('');
  md.push('## 完整转录');
  md.push('');
  events.forEach((e, i) => {
    const role = e.kind === 'user' ? '用户' : '助手';
    const rt = e.kind === 'assistant' ? ` · T${e.turn}/S${e.step}` : '';
    const tools = e.kind === 'assistant' && e.tools?.length ? ` · 工具: ${[...new Set(e.tools)].join(', ')}` : '';
    md.push(`### [${i + 1}] ${role} · seq ${e.seq} · ${fmtTime(e.time)}${rt}${tools}`);
    md.push('');
    if (e.text) md.push(e.text);
    if (e.kind === 'assistant' && !e.text) {
      md.push(`*（本条没有文本正文，只有推理与工具调用：${[...new Set(e.tools ?? [])].join(', ') || '无工具'}；推理约 ${e.reasoningChars} 字）*`);
    }
    if ((opt.tools || opt.results) && e.kind === 'assistant') {
      const calls = e.blocks.filter((b) => b?.type === 'tool-call');
      if (calls.length) {
        md.push('');
        md.push('<details><summary>工具证据（参数/结果均截断，仅用于核对事实）</summary>');
        md.push('');
        for (const c of calls) {
          if (opt.tools) md.push(`- 调用 \`${c.name}\`：${oneLine(String(c.arguments ?? ''), 700)}`);
          const res = callResults.get(c.id);
          if (opt.results && res) {
            md.push('');
            md.push('  ```text');
            for (const ln of res.split('\n').slice(0, 40)) md.push('  ' + ln.replace(/\r/g, '').slice(0, 400));
            if (res.split('\n').length > 40) md.push('  …（结果更长，已截断）');
            md.push('  ```');
          }
        }
        md.push('');
        md.push('</details>');
      }
    }
    md.push('');
  });

  writeFileSync(join(transcriptDir, `${id}.md`), md.join('\n'), 'utf8');

  // ---------- 给后续摘要用的机器可读摘要 ----------
  const digest = {
    id, date, createdAt: meta.createdAt, firstTime: first, lastTime: last, title: meta.title,
    frames, textChars: meta.textChars,
    userCount: events.filter((e) => e.kind === 'user').length,
    assistantCount: events.filter((e) => e.kind === 'assistant').length,
    messages: events.map((e, i) => ({
      n: i + 1, seq: e.seq, time: fmtTime(e.time), role: e.kind === 'user' ? 'user' : 'assistant',
      turn: e.turn, step: e.step, tools: e.tools ? [...new Set(e.tools)] : undefined,
      textChars: e.text.length,
      head: oneLine(e.text, 220),
    })),
  };
  writeFileSync(join(transcriptDir, `${id}.digest.json`), JSON.stringify(digest, null, 2), 'utf8');

  report.push({
    id, date, firstTime: fmtTime(first), lastTime: fmtTime(last), title: meta.title,
    frames, textChars: meta.textChars, user: digest.userCount, assistant: digest.assistantCount,
    transcript: join(transcriptDir, `${id}.md`),
  });
  console.log(`✓ ${id}  帧=${frames}  消息=${digest.userCount}u+${digest.assistantCount}a  → ${join(transcriptDir, `${id}.md`)}`);
}

writeFileSync(join(opt.out, '_dump_report.json'), JSON.stringify(report, null, 2), 'utf8');
console.log(`完成 ${report.length} 个会话，输出目录 ${opt.out}`);
