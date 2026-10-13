# -*- coding: utf-8 -*-
#
# 本地 SauceNAO 桩服务（开发用，不参与构建）
# ------------------------------------------------------------------
# 用途：在没有 API Key / 不想消耗配额的情况下，离线验证识图链路
#       （multipart 上传、JSON 解析、库索引排序、dHash 复核、感知分组）。
#
# 依赖：Python 3 + Pillow
# 用法：
#   1) python scripts\mock-saucenao.py 8899
#   2) 复制 PixivRenamer.json，把 SauceNaoApiBase 改为 http://127.0.0.1:8899，
#      SauceNaoApiKey 随便填一个非空值（匿名会被真实服务拒绝，桩服务同样模拟该行为）
#   3) PixivRenamer.exe --cli --config 改好的配置.json --dry-run --limit 14
#
# 行为：按 上传文件名 crc32 % 4 返回四种结果 
#   0: pixiv 高置信度(95.3)，缩略图与上传图一致 -> 直接采纳
#   1: pixiv 中置信度(71.4)，缩略图一致          -> 应通过 dHash 复核
#   2: pixiv 中置信度(66.8)，缩略图不一致        -> 应被 dHash 复核拦下
#   3: Danbooru 转载(96.1)，无 pixiv id          -> 走"无 PID"分支只用作者/标题
#   另外：请求不带 api_key 时返回 status:-1，模拟匿名调用被拒。
# ------------------------------------------------------------------
"""本地 SauceNAO 桩服务：按真实 API 的 JSON 结构返回，用于离线验证识图链路。"""
import io, json, os, re, sys, zlib, random, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from PIL import Image

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8899
LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "stub.log")
UPLOADS = {}          # pid -> 原始图片字节（当作来源缩略图回吐）
LOCK = threading.Lock()
STATS = {"search": 0, "anon_rejected": 0}

def log(msg):
    with LOCK:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(msg + "\n")

def noise_png():
    random.seed(20240926)
    im = Image.new("RGB", (300, 300))
    im.putdata([(random.randrange(256), random.randrange(256), random.randrange(256)) for _ in range(300*300)])
    buf = io.BytesIO(); im.save(buf, "PNG"); return buf.getvalue()

NOISE = noise_png()

def parse_multipart(body, boundary):
    parts, out = {}, {}
    for chunk in body.split(b"--" + boundary):
        if b"\r\n\r\n" not in chunk:
            continue
        head, data = chunk.split(b"\r\n\r\n", 1)
        if data.endswith(b"\r\n"):
            data = data[:-2]
        m = re.search(rb'name="?([^";\r\n]+)"?', head)
        if not m:
            continue
        name = m.group(1).decode("utf-8", "replace")
        fm = re.search(rb'filename="?([^";\r\n]*)"?', head)
        out[name] = data
        if fm:
            out["__filename"] = os.path.basename(fm.group(1).decode("utf-8", "replace"))
    return out

class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _send(self, code, ctype, body):
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        m = re.match(r"^/thumb/(\d+)\.(png|jpg)$", self.path)
        if m:
            pid = m.group(1)
            data = UPLOADS.get(pid, NOISE)
            self._send(200, "image/png", data)
            log(f"GET  thumb pid={pid} -> {'echo(一致)' if pid in UPLOADS else 'noise(不一致)'} {len(data)}B")
            return
        self._send(404, "text/plain", b"not found")

    def do_POST(self):
        if not self.path.startswith("/search.php"):
            self._send(404, "text/plain", b"not found"); return
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        ct = self.headers.get("Content-Type", "")
        bm = re.search(r'boundary=(?:"([^"]+)"|([^;]+))', ct)
        if bm:
            bnd = (bm.group(1) or bm.group(2) or "").strip()
            fields = parse_multipart(body, bnd.encode())
        else:
            fields = {}
        filename = fields.get("__filename", "?")
        img = fields.get("file", b"")
        has_key = "api_key" in fields and fields["api_key"]
        STATS["search"] += 1

        # 与真实服务一致：匿名调用直接拒绝
        if not has_key:
            STATS["anon_rejected"] += 1
            log(f"POST {filename} {len(img)}B -> status -1（匿名拒绝）")
            payload = {"header": {"status": -1, "message": "The anonymous account type does not permit API usage."}}
            self._send(200, "application/json", json.dumps(payload).encode())
            return

        variant = zlib.crc32(filename.encode("utf-8")) % 4
        pid = str(90000000 + zlib.crc32(filename.encode("utf-8")) % 100000)
        thumb = f"http://127.0.0.1:{PORT}/thumb/{pid}.png"

        if variant == 0:      # 高置信度 pixiv
            UPLOADS[pid] = img
            sim, index_id, index_name = "95.3", 5, "pixiv"
            member, title = "测试画师A", "高置信度命中"
            pixiv_id = pid
        elif variant == 1:    # 中置信度但缩略图一致 -> 应通过复核
            UPLOADS[pid] = img
            sim, index_id, index_name = "71.4", 5, "pixiv"
            member, title = "测试画师B", "中置信度命中"
            pixiv_id = pid
        elif variant == 2:    # 中置信度但缩略图不一致 -> 应被复核拦下
            UPLOADS.pop(pid, None)
            sim, index_id, index_name = "66.8", 5, "pixiv"
            member, title = "测试画师C", "中置信度但图不符"
            pixiv_id = pid
        else:                 # booru 转载（无 pixiv id）
            sim, index_id, index_name = "96.1", 9, "Danbooru"
            member, title = "转载画师", "booru 转载命中"
            pixiv_id = ""

        item = {
            "header": {"similarity": sim, "thumbnail": thumb, "index_id": index_id,
                       "index_name": index_name, "dup": False, "hidden": 0},
            "data": {"ext_urls": [f"https://www.pixiv.net/artworks/{pixiv_id}"] if pixiv_id else ["https://danbooru.donmai.us/posts/1"],
                     "title": title, "member_name": member, "member_id": "10001" if pixiv_id else "10002"},
        }
        if pixiv_id:
            item["data"]["pixiv_id"] = int(pixiv_id)
        payload = {"header": {"status": 0, "short_remaining": 100, "long_remaining": 1000},
                   "results": [item]}
        log(f"POST {filename} {len(img)}B -> variant={variant} idx={index_id} sim={sim} pid={pixiv_id or '-'}")
        self._send(200, "application/json", json.dumps(payload).encode())

if os.path.exists(LOG):
    os.remove(LOG)
srv = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
log(f"stub 启动 127.0.0.1:{PORT}")
print(f"stub listening on 127.0.0.1:{PORT}", flush=True)
srv.serve_forever()