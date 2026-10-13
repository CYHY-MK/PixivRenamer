# PixivRenamer 项目笔记（跨会话汇总）

> 本文汇总自本机 DSH 会话存档中与 PixivRenamer 相关的历史会话，只写会话里**确实出现过**的结论与数据；不确定的一律标注"未在会话中确认"。
> 每一条结论后面用 `（会话 N）` 标注来源，会话编号对应下表。
>
> **重要提示**：归档期间项目源码正在被并行修改。本文中"当前代码"形式的参数与行为核对于 **2026-10-08 12:16**（那时源码里已包含 2.3 的缓冲队列与未识别分类），它们**晚于会话 3 的最后一条记录（12:12:48）**；凡标"当前代码"的条目属旁证，不是会话结论。

| 编号 | 会话 ID | 时间（UTC+8） | 纪要 |
| --- | --- | --- | --- |
| 会话 1 | `session-551c7aa8-d21f-4509-98ee-7cd10854c95c` | 2026-09-26 09:16 → 09:32 | [2026-09-26-输出目录误排除修复.md](2026-09-26-输出目录误排除修复.md) |
| 会话 2 | `session-81fef8c4-1c8e-4b6a-a0d4-6f1446145658` | 2026-09-26 21:06 → 21:57 | [2026-09-26-识图功能增强v2.2.md](2026-09-26-识图功能增强v2.2.md) |
| 会话 3 | `session-45162089-9fd4-43ee-b455-4be4498f000b` | 2026-10-08 10:43 → 12:12（中断） | [2026-10-08-识图缓冲与未识别分类.md](2026-10-08-识图缓冲与未识别分类.md) |

---

## 一、项目定位

**PixivRenamer** 是一个 Windows 桌面工具：把大量下载的 pixiv 插画按 **作品 PID → 作者/标题** 重命名并分类整理；文件名没有 PID、或缺少作者/标题时，用 **SauceNAO / ascii2d** 识图辅助查询。（会话 2 读到的 README 原文）

- 技术栈：**C# / .NET 8 WPF**，发布为**自包含单文件 exe**（win-x64，含运行时，目标机器不需要装 .NET / Python / VC++）。（会话 2）
- 旧版是 C++ / Win32 / CMake / Inno 工程，**v2.0.0 已完全重写并删除旧工程**，重写动机是修掉旧版"点击浏览卡死"和"多页面 UI 叠加/乱码"（改用官方 `Microsoft.Win32.OpenFolderDialog` 与 XAML `TabControl` 布局）。（会话 1 读到的 README/CHANGELOG 记录；该重写过程的原始会话已因上下文满被删除，理由只能追溯到文档条目）
- 用户的原始会话**曾因上下文满被删除**，因此本项目的历史决策有一部分不可考（会话 1 开头用户原话）。
- 使用建议（写在 README / 使用说明里）：**第一次先勾选预览模式**（不修改文件）跑一遍，看报告 CSV 确认无误再取消预览；程序会弹窗二次确认。（会话 2）

## 二、架构与文件职责

### 源码（`src\PixivRenamer\`）

| 文件 | 职责 | 相关会话 |
| --- | --- | --- |
| `Core\AppConfig.cs` | 全部配置项 + 默认值 + `Finalize()` 边界钳制 + JSON 读写（unknown 字段回退默认值） | 1、2、3 |
| `Core\RenamerEngine.cs` | 扫描 → pixiv 元数据 → 识图回退 → 生成计划 → 执行；包含 `Scan` / `EnumerateFiles` / `IsExcluded` / 感知分组 / `JudgeAsync`（dHash 复核）/ 未识别原因分类 | 1、2、3 |
| `Core\TextUtils.cs` | 路径与文本工具：`IsUnder`、`SamePath`、`IsStrictSubdirectory`、`IsImageFile`、`IsHidden`、`HashFileQuick`、`SanitizeFileName`、`NowStamp` | 1 |
| `Core\PidParser.cs` | 从文件名提取 PID / 页码，含"日期不识别"、"6 位裸数字默认不识别（激进模式才认）"等规则 | 2（读到自检用例） |
| `Core\PixivClient.cs` | pixiv 作品元数据：AJAX 优先、HTML 兜底、内存 + 磁盘缓存、失败缓存 | 2（读到源码） |
| `Core\PixivLoginService.cs` / `App.xaml.cs` | 账号密码自动获取 Cookie 等 | 会话内未展开 |
| `Core\HttpService.cs` | HttpClient 封装：限速（`RateLimiter`）、重试、Cookie 隔离、multipart 上传 | 2、3 |
| `Core\PerceptualHash.cs` | **dHash 感知哈希**（9×8 灰度相邻像素差分 → 64 位指纹）+ 汉明距离；**复用 WPF 图像管线，不引入额外 NuGet 依赖**；无法解码返回 0 | 2（新增）、3 |
| `Core\ReverseSearchService.cs` | SauceNAO / ascii2d 识图：库索引优先级、`status != 0` 处理、Cloudflare 识别、缩略图校验字段、`LastProvider` | 2、3 |
| `Core\ReverseQueue.cs` | 识图请求缓冲队列 `ReverseQueue` + 全局冷却闸门 `ReverseThrottle` | 3（新增） |
| `Core\Models.cs` | `FileItem` / `PixivMeta` / `ReverseResult`（索引号、缩略图、校验字段）/ `PlanEntry`；会话 3 增加 `UnresolvedReason` / `UnresolvedKind` / `ReverseProvider` / `ReverseTimedOut` | 2、3 |
| `Core\NamingService.cs` | 命名模板渲染、非法字符与保留名处理、重名去重 | 会话 1 读到 `ReservedNames` |
| `Core\SelfTest.cs` | 内置自检；项数演进 **25 → 30（会话 1 加 5 个路径用例）→ 37（会话 2 加 5 个 dHash/索引用例）** | 1、2 |
| `Core\CliRunner.cs` | 命令行模式；会话 2 加 `--limit` / `--sauce-key`，会话 3 加 `--no-reverse-queue` / `--unresolved-folder` / `--no-unresolved-folder` | 2、3 |
| `MainWindow.xaml` / `.xaml.cs` | 界面与配置绑定（整理 / pixiv·识图 / 日志·配置 三个 Tab） | 1、2 |
| `Core\ReportService.cs`、`Core\Thumbnailer.cs`、`Core\ConsoleHelper.cs`、`Core\RateLimiter.cs` | 报告 CSV、上传缩略图、控制台辅助、限速器 | 会话内未展开 |

### 脚本与产物

| 路径 | 说明 | 相关会话 |
| --- | --- | --- |
| `scripts\publish.ps1` | `dotnet publish` 自包含单文件到 `publish\win-x64\`（约 10 秒，62.9 MB） | 2 |
| `scripts\package.ps1` | 生成 `dist\PixivRenamer\` 便携目录 + ZIP + 免安装部署 bat | 2（修了既有 here-string bug） |
| `scripts\mock-saucenao.py` | **离线 SauceNAO 桩服务**（Python 3 + Pillow，不参与构建），用于不消耗配额的识图链路回归 | 2（新增） |
| `PixivRenamer.json` | 配置模板（含 Cookie，注意保密）；`Save()` 用 UTF-8 BOM 写回 | 2、3 |
| `dist\` | 便携版目录、`PixivRenamer_vX.Y.Z_便携版.zip`、安装/卸载 bat | 1、2 |

## 三、重要决策记录（含理由）

### D1. 识图引擎优先级：SauceNAO 主力 → ascii2d 补漏（会话 2）

理由：**SauceNAO 是唯一能直接返回 `pixiv_id` 的引擎**（另有 `member_id` / `member_name` / `title` / `similarity`），Pixiv 插图场景基本靠它；ascii2d 能给出 pixiv/twitter 来源页 URL 但**不给 PID**，只能补漏。
Yandex / Google Lens 属于抓取，**稳定性差且可能违反 ToS，明确不进核心链路**；AnimeTrace 只能识别角色/作品、**给不了 PID**，对"重命名"只是提示，而且实测接口返回 403；IQDB / 3D IQDB 只在 Pixiv 图被转到 booru 时有用。

### D2. 库索引优先排序，`pixivhistorical` 必须算高优先（会话 2）

不猜索引号，而是抓 `https://saucenao.com/index.php` 搜索表单的 checkbox `value`（即 API 结果里的 `index_id` / db 位掩码），再对照官方 `index_details.txt`。排序：`pixiv(#5) → pixivhistorical(#6) → Twitter(#41) → Pawoo(#35) → Skeb(#44) → Nijie(#11) → Nico Seiga(#8) →` 各 booru 转载站。
**理由**：`pixivhistorical` 命中的作品在 pixiv 上通常已删除（回查 API 会 404），这时要直接用 SauceNAO 返回的 `member_name` / `title` 兜底，**很多人会漏掉这个库**。

### D3. 双阈值 + dHash 像素级复核（会话 2）

**核心认知：SauceNAO 的 `similarity` 是匹配置信度，不是视觉相似度。** 在 50~75 区间，构图相似的图（同角色、同姿势）会被误判成同一张，对"自动重命名"是灾难。
所以：`≥85` 直接采纳；`60~85` **必须**下载来源缩略图做 dHash 比对（汉明距离 ≤ 上限才采纳）；`<60` 丢弃。实现位置 `RenamerEngine.JudgeAsync`。

### D4. 感知分组：近似重复图只查一次（会话 2）

同一张图的不同分辨率/重存版本按 dHash 归一组，一组只发一次识图请求，结果映射回全组。
**注意实现细节**（会话 2 之后的当前代码，过程未记录在会话中）：先按**完整文件哈希**分桶，再对每个桶的代表图算 dHash，与已有代表图比较（距离 ≤ `HashMaxDistance` 即并入）；所以是"精确哈希分桶 + dHash 桶内近似合并"。

### D5. pixiv 挂了也要继续识图（会话 2）

原实现里 pixiv 连通性预检失败会连识图一起跳过；而用户机器上 pixiv 直连**必然超时**，等于识图完全不可用。改为只跳过 pixiv 元数据查询，识图照跑（开关 `ReverseWhenPixivDown`，默认开）。

### D6. 上传用文件字节，不用 `url=` 参数（会话 2）

理由：pximg 有防盗链，SauceNAO 服务端抓不到。上传前统一转 JPEG、长边压到 ~1200px（对匹配率几乎无损，上传更快）。

### D7. 限频读服务端配额，自适应节流（会话 2）

不自己猜频率：响应 JSON 的 `header` 里有 `short_limit` / `short_remaining`（短周期）与 `long_limit` / `long_remaining`（每日），按它节流；`header.status != 0` 单独处理。（骨架示例：`short_remaining <= 1` 等 30 秒、`<= 3` 等 5 秒）

### D8. 识图请求改走统一缓冲队列 + 全局冷却（会话 3，用户拍板）

用户明确选择"请求排队缓冲：统一队列 + 并发上限可分开启用 + 失败退避重试 + 配额/进度提示"。
实现理由（代码注释）：SauceNAO / ascii2d 对频率非常敏感，用固定并发（默认 **1 = 完全串行**）+ 统一冷却，被 429/503 限流时**全局暂停**再继续，避免"越打越被拦"；成功一次就把冷却等级回落一级。

### D9. 一次识图调用只交给一个服务（会话 3）

被限流时**整组重试同一个服务**，而不是把半截结果丢给下一个服务——"那样既费配额又容易拿到低质量候选"。

### D10. 未识别图片单独分类（会话 3，用户拍板）

用户选择"在输出目录下建「未识别」文件夹，未识别文件**按原名**移动/复制进去，并**记录原因**"；原因归类为 `文件过大或无法读取` / `识图复核未通过` / 限流超时 / `无 PID` / `识图无结果` / `pixiv 查询失败` / `目标路径过长`。

### D11. 输出目录排除逻辑只在"真子目录"时生效（会话 1）

**理由**：`OutputDir = SourceDir` 时源目录里所有文件都在输出目录下，旧逻辑会把它们全部排除、扫描出 0 个文件。改为只在 `OutputDir` / `CacheDir` 是 `SourceDir` 的**真子目录**时排除其内容（`IsStrictSubdirectory` 注释明确"两者相同不算"），递归扫描时直接跳过真正的输出子树。

### D12. 用本地桩服务做可验证性（会话 2）

真实约束是"SauceNAO 必须 API Key、ascii2d 被 Cloudflare 拦、pixiv 不可达"，因此写了按真实 API JSON 结构返回的 Python 桩服务，端到端验证解析 / 排序 / dHash 复核 / 感知分组；会话结束时**把桩服务留在项目里**（`scripts\mock-saucenao.py`）作为离线回归工具（放 `%TEMP%` 会被清理）。配套新增 `SauceNaoApiBase`（可指向桩服务）与 `SauceNaoTestMode`（testmode=1，不消耗额度）。

### D13. 改动源码时保持 UTF-8 BOM，并逐次回读校验（会话 1、2）

仓库里所有 `.cs` 都是**带 BOM 的 UTF-8**（前三字节 `239,187,191`），写作前先探测；`String.Replace` 因换行符/BOM 不匹配会**静默失败**，所以每次替换后都回读确认（会话 2 就因此踩了两次坑：using 没插进去、模板 JSON 没写进新字段）。

## 四、已知坑与规避

| # | 坑 | 现象 / 证据 | 规避 | 来源 |
| --- | --- | --- | --- | --- |
| K1 | 输出目录被误判为"要排除的内容" | `SourceDir = OutputDir` 时日志"扫描到 0 个图片文件 / 未找到可处理的图片文件" | 只在真子目录时排除；新增 5 个路径用例 | 会话 1 |
| K2 | 覆盖正在运行的 exe 会失败 | 旧程序占用 `dist\...\PixivRenamer.exe`（PID 24940） | 先更新 `publish`，等程序关闭后再复制 `dist` | 会话 1 |
| K3 | **pixiv.net 在本机不可达**（请求超时） | 真实测试第一步即超时 | 填代理（如 `http://127.0.0.1:7890`）或勾选系统代理 + 点"测试网络"；代码侧保证"pixiv 挂时仍识图" | 会话 2 |
| K4 | **SauceNAO 匿名调用被拒** | `{"header":{"status":-1,"message":"The anonymous account type does not permit API usage."}}` | 必须填免费 API Key；代码识别 `status != 0` 并提示 | 会话 2 |
| K5 | **ascii2d 被 Cloudflare 拦** | `HTTP 403` + 220,752 字节 `Just a moment...` | 需要能过校验的网络/代理；代码明确提示，不再伪装成"没找到" | 会话 2 |
| K6 | `api.animetrace.com/v1/search` 403 | POST `{"model":"anime"}` → 403 | 未接入 | 会话 2 |
| K7 | .NET multipart 的 `boundary=` 带引号；`name=` / `filename=` 只在含特殊字符时带引号 | 桩服务解析全部失败，所有请求落到同一 variant，导致 dHash 复核"看起来没生效" | 桩服务正则放宽为 `boundary=(?:"([^"]+)"|([^;]+))`、`name="?([^";\r\n]+)"?`、`filename="?([^";\r\n]*)"?` | 会话 2 |
| K8 | `JsonElement.TryGetInt32(out int)` 调用错误 → `CS7036` | 编译失败 | 用标准 `out var` | 会话 2 |
| K9 | 新加的 `using` 没插进文件（静默失败）→ `CS0103/CS0246` | 编译失败 | 用 `-notmatch` 显式判断后补 using | 会话 2 |
| K10 | `scripts\package.ps1` 第 34 行 here-string 头 `@"` 后跟**字面量** `` `r`n `` | `ParserError: UnexpectedCharactersAfterHereStringHeader`；dist 里的 exe 一直是旧版（自检显示 30/30 而非 37/37） | 改成 `"`r`n" + @"` 拼接，并用 `[Parser]::ParseFile` 做语法检查 | 会话 2 |
| K11 | 版本号散落多处，容易漏改 | `ProductVersion` 仍是 2.1、GUI 标题仍是 2.1 | 全项目搜 `2\.1` 统一；`FileVersion`/`InformationalVersion`/XAML 标题都要改 | 会话 2 |
| K12 | 会话第一轮因账号额度失败 | `Insufficient Balance` / `ACCOUNT_QUOTA` / `status 402` | 与代码无关；用户"重试"即可 | 会话 3 |
| K13 | 队列异常可能让整批整理失败 | —— | `ReverseQueue` 捕获后**降级为串行补跑**剩余项 | 会话 3 |
| K14 | 识图"卡死"看起来像程序无响应 | ascii2d 有时"请求失败：请求超时"，SauceNAO 被拒后逐个文件重试 | 队列进度上报 + 冷却倒计时日志 + 统一超时 `ReverseTimeoutMs` | 会话 2、3 |
| K15 | 本机 shell 是 **Windows PowerShell 5.1**，不支持 `Invoke-WebRequest -Form` | —— | 探测 multipart 用 `curl.exe` | 会话 2 |

## 五、参数与默认值速查表

> 数值取自会话中读到的源码/配置与当前工作区源码（未改动任何源文件）。标注"会话"的表示该值在会话里有明确记录。

### 输入 / 输出

| 配置项 | 默认值 | 说明 | 来源 |
| --- | --- | --- | --- |
| `DryRun` | `true` | **默认预览，安全第一**（源码注释原文） | 当前代码 |
| `Recursive` | `true` | 递归扫描 | 当前代码 |
| `IncludeHidden` | `false` | 是否含隐藏文件 | 当前代码 |
| `CopyMode` / `Overwrite` | `false` / `false` | 复制模式 / 覆盖 | 当前代码 |
| `FileLimit` | `0` | `>0` 只处理前 N 个文件；CLI `--limit N` | 会话 2 |
| `MaxNameLen` | `150` | 文件名长度上限（钳制 20~200） | 会话 1 |

### pixiv / HTTP

| 配置项 | 默认值 | 来源 |
| --- | --- | --- |
| `Threads` | `3`（钳制 1~16） | 会话 1 |
| `PixivDelayMs` | `1200` | 会话 1 |
| `TimeoutMs` | `30000`（钳制 5000~600000） | 会话 1 |
| `Retries` | `3`（钳制 1~8） | 会话 1 |
| `HtmlFallback` | `true` | 当前代码 |
| `CacheEnabled` / `CacheDays` | `true` / `30` | 会话 1 |
| `UseSystemProxy` | `false` | 会话 2（CHANGELOG v2.1） |
| `UserAgent` | Chrome 124 UA | 当前代码 |

### 命名 / 分类

| 配置项 | 默认值 | 来源 |
| --- | --- | --- |
| `Template` | `{author} - {title} ({pid})` | 会话 1 |
| `Organize` | `author`（可选 `none`/`author`/`date`/`author_date`/`date_author`） | 会话 1 |
| `UnknownAuthor` / `UnknownTitle` | `未知作者` / `未命名` | 当前代码 |
| `R18Folder` / `SplitR18` | `R-18` / `false` | 当前代码 |
| `MoveUnresolvedToFolder` / `UnresolvedFolderName` | `true` / `未识别` | 会话 3 |

### 识图

| 配置项 | 默认值 | 说明 | 来源 |
| --- | --- | --- | --- |
| `ReverseEnabled` | `true` | 识图回退总开关 | 会话 1 |
| `ReverseOrder` | `saucenao,ascii2d` | 服务顺序 | 会话 1 |
| `SauceNaoApiKey` | `""` | **必须填**，匿名会被拒 | 会话 2 |
| `SauceNaoDelayMs` / `Ascii2dDelayMs` | `8000` / `5000` | 服务间隔 | 会话 1 |
| `MinSimilarity` | `60`（钳制 0~100） | 相似度下限 | 会话 1 |
| `MaxUploadKB` | `2048`（钳制 ≥64） | 上传体积上限 | 会话 1 |
| `VerifyByHash` | `true` | 中置信度做 dHash 复核 | 会话 2 |
| `HashMaxDistance` | `10`（钳制 0~20） | dHash 汉明距离上限 | 会话 2 |
| `GroupByPerceptualHash` | `true` | 近似重复只查一次 | 会话 2 |
| `ReverseWhenPixivDown` | `true` | pixiv 不通时仍识图 | 会话 2 |
| `SauceNaoApiBase` | `https://saucenao.com` | 可指向镜像/本地桩 | 会话 2 |
| `SauceNaoDbMask` | `0` | `0`=全库 `db=999`；`96`=`0x20|0x40` 只查 pixiv + pixivhistorical | 会话 2 |
| `SauceNaoTestMode` | `false` | `testmode=1`，不消耗配额 | 会话 2 |
| `ReverseQueueEnabled` | `true` | 识图走统一队列（缓冲） | 会话 3 |
| `ReverseConcurrency` | `1`（钳制 1~8） | 识图并发上限 | 会话 3 |
| `ReverseCooldownMs` | `12000`（钳制 1000~600000） | 被限流后的基础冷却 | 会话 3 |
| `ReverseMaxCooldownMs` | `120000`（≥基础值，≤900000） | 冷却上限 | 会话 3 |
| `ReverseMaxRetries` | `2`（钳制 0~8） | 失败重试次数 | 会话 3 |
| `ReverseTimeoutMs` | `90000`（钳制 10000~1800000） | 单次识图调用上限（含退避等待） | 会话 3 |

### 识图链路的判定阈值（代码常量，非配置）

| 阈值 | 值 | 含义 |
| --- | --- | --- |
| dHash 直接采纳 | `similarity ≥ 85` | 视为高置信度，不下载缩略图 |
| dHash 复核区间 | `60 ≤ similarity < 85` | 下载来源缩略图比对，距离 ≤ `HashMaxDistance` 才采纳 |
| 低分丢弃 | `similarity < 60` | 直接丢弃（另有 `minsim=30` 在服务端先过滤一遍） |
| ascii2d 命中 | `Similarity = 90` | ascii2d 不给分值，"命中即视为高置信度；不给分值时按 90 处理" |
| 冷却等级上限 | 6 级 | `ReverseThrottle` 指数退避等级 |

### CLI（会话中记录）

```bat
REM 试跑（预览 + 前 30 个）
PixivRenamer.exe --cli "D:\系统\下载\1\pixiv" --dry-run --limit 30

REM 指定 key / 关掉缓冲 / 自定义未识别文件夹名（会话 3 新增）
PixivRenamer.exe --cli <目录> --sauce-key <KEY> --no-reverse-queue --unresolved-folder 未识别
```

## 六、识图链路细节

### 6.1 整体流程

```
扫描（IsImageFile + IsExcluded + 隐藏文件策略）
  └─ 文件名能提出 PID/作者/标题 → 走 pixiv 元数据（AJAX 优先，HTML 兜底，内存+磁盘缓存）
  └─ 缺作者或标题 → 感知分组 → 识图缓冲队列 → SauceNAO（必要时 ascii2d）→ dHash 复核 → 采纳/拒绝
  └─ 仍未识别 → 归类原因 → 报告 CSV +（2.3）移入「未识别」文件夹
```

### 6.2 服务顺序

1. `ReverseOrder` 默认 `saucenao,ascii2d`。
2. 一次识图调用**只交给一个服务**；被限流时整组重试同一个服务（D9）。
3. 顺序的起点会跟随 `LastProvider`：若上次实际用的是序列中第 `idx` 个服务（`idx > 0`），本次从该服务开始，避免每次都从头打。

### 6.3 SauceNAO 请求与索引优先级

- 端点：`POST {SauceNaoApiBase}/search.php?output_type=2&db=999&numres=10&minsim=30&hide=0&api_key=...`，`multipart/form-data`，字段名 **`file`**（上传字节，不用 `url=`）。
- `dbmask=96`（`0x20|0x40`）= 只查 pixiv + pixivhistorical；不传 `dbmask` 用 `db=999` 全库。
- `testmode=1` = dry-run，不消耗额度。
- **库索引优先级（越小越可信）**：

| rank | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13~18 | 其他 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| index_id | 5 | 6 | 41 | 35 | 44 | 11 | 8 | 9 | 25 | 26 | 27 | 12 | 28 | 43,34,39,20,19,31 | 99 |
| 名称 | pixiv | pixivhistorical | Twitter | Pawoo | Skeb | Nijie | Nico Seiga | Danbooru | Gelbooru | Konachan | Sankaku | Yande.re | Anime-Pictures | Kemono, deviantArt, ArtStation, MediBang, 2D-Market, bcy.net | 未知 |

- `header.status != 0` 要单独处理（key 无效 / 超限 / 维护）；**匿名拒绝（`status=-1`）属永久性错误，不进入冷却**。
- 配额：读 `header.short_limit` / `short_remaining` / `long_limit` / `long_remaining` 自适应节流。

### 6.4 ascii2d 协议（核实自 PicImageSearch 源码）

- 按图上传：`POST https://ascii2d.net/search/file`，multipart 字段名 `file`。
- 按 URL：`POST /search/uri`，字段 `uri`。
- 返回 HTML 结果页，解析 `div.row > div.detail-box` 里的来源链接。
- 特征检索 `GET /search/bovw/<hash>`；颜色检索 `GET /search/color/<hash>`（默认）。
- **不给 PID**，命中按 `Similarity = 90` 处理。
- Cloudflare 拦截有两种表现：`HTTP 403`（challenge 页）或 `200 + "Just a moment..."`，都要识别。

### 6.5 dHash 复核与感知分组

- dHash：9×8 灰度 → 相邻像素差分 → 64 位指纹；汉明距离用 popcount；无法解码返回 0（`PerceptualHash`，复用 WPF 图像管线）。
- 复核触发条件：`VerifyByHash = true`、本地 dHash 计算成功、`60 ≤ similarity < 85`、且候选带缩略图 URL。
- 缩略图在 `img*.saucenao.com`，用**独立限速键** `saucenao-image`（避免占用搜索接口的间隔）。
- 判定与日志：距离 ≤ `HashMaxDistance`（默认 10/64）→ 采纳，日志 `dHash 复核通过（差异 n/64）`；否则 → 拒绝，日志 `放弃候选 SauceNAO 66.8（pixiv，PID ...）：dHash 复核不通过（差异 32/64，可能只是同角色或相似构图）`。
- 感知分组：**先按完整文件哈希分桶，再对每桶代表图算 dHash 并与已有代表图比较**（距离 ≤ `HashMaxDistance` 即并入）；一组只查一次识图，结果映射回全组。日志示例：`感知分组：14 个文件归并为 13 组（1 组为近似重复），少发 1 次识图请求`。

### 6.6 识图缓冲（2.3）

- 队列：`Parallel.ForEachAsync` + `MaxDegreeOfParallelism = ReverseConcurrency`（默认 1 = 完全串行）；每个任务开始前先等全局冷却。
- 冷却：被限流后 `Block(retryAfter, reason)`；优先 `Retry-After`，否则 `ReverseCooldownMs × 2^(level-1)`，上限 `ReverseMaxCooldownMs`；等级上限 6；成功一次降一级。
- 进度与日志：`识图进度 d/total`（每 20 个或最后一个）、`识图冷却中，还需等待 X 秒`、`识图被限流（原因）：全局冷却 X 秒后继续（缓冲队列会等待）`。
- 超时：识图因限流冷却等待超过 `ReverseTimeoutMs`（默认 90 秒）记为 `识图超时（限流冷却）`，原因写 `限流/冷却等待超时（服务名）`。
- 容错：队列异常 → `识图队列异常，改为串行收尾：...`，串行补跑未完成的组。

## 七、遗留事项与下一步

### 真实网络下仍未验证（这是最大的遗留）

| 项 | 状态 | 需要做什么 |
| --- | --- | --- |
| pixiv 直连 | 本机**请求超时** | 挂代理（示例 `http://127.0.0.1:7890`，或勾选系统代理）后点"测试网络" |
| SauceNAO | **需要 API Key**（匿名 `status=-1`） | 在「pixiv / 识图」页填免费 Key（<https://saucenao.com/user.php?page=account>） |
| ascii2d | **被 Cloudflare 拦** | 需要能过校验的网络/代理，否则该服务实际不可用 |
| 真实命中率 | **未验证**（全部端到端测试都基于本地桩服务） | 填 Key + 挂代理后先小样本：`PixivRenamer.exe --cli "D:\系统\下载\1\pixiv" --dry-run --limit 30`，看 CSV 命中率与 `note` 列（复核差异值） |
| AnimeTrace / IQDB | 只做调研，**未接入** | 如需 booru 图源或角色识别再评估 |

### 会话 2 明确列出的"需要你补的两件事"

1. 填 SauceNAO API Key；
2. 挂代理后点"测试网络"。

### 会话 3 未完成/未验证

- **界面美化（用户四个目标之一）完全没有开始**。
- 识图缓冲与「未识别」文件夹分类：会话在 12:12:48 中断，**没有编译、没有自检、没有回归测试、没有报告样例**；后续是否有别的会话完成编译与测试**未在会话中确认**（工作区文件时间戳显示 12:13 之后仍有改动，但那部分过程不在存档中）。
- 「未识别」文件夹与 `Organize` / `SplitR18` / 命名模板的优先级与冲突处理未讨论。
- `ReverseConcurrency = 1` 对大批量耗时的影响未实测；`ReverseTimeoutMs = 90000` 与最大 120 秒冷却的组合可能更容易触发"限流超时"。
- SauceNAO 是否真的返回 `Retry-After` 头、格式如何，未核实。

### 其他待办（会话中提过但没做）

- `dbmask=96`（只查 pixiv 两库）与 `db=999`（全库）的真实额度/命中率差异未实测；默认仍走全库。
- 缓存 key 的组合方式在会话 2 有两种不同说法（"SHA-256 + dHash" 与"图片 SHA-256"），**最终实现未在会话中确认**。
- 会话 1 遗留的临时目录 `tmp_scan_bug` 的清理未记录。
- `dist` 里同时留着 `v2.1.0` 与 `v2.2.0` 两个便携包（会话 2 结束时如此），是否需要清理未确认。

## 八、可复现的验证手法（供以后回归）

1. **自检**：`PixivRenamer.exe --selftest`（会话 2 时为 37/37）。
2. **离线识图回归**（不消耗配额）：
   ```bat
   python scripts\mock-saucenao.py 8899
   REM 复制 PixivRenamer.json，把 SauceNaoApiBase 改成 http://127.0.0.1:8899，
   REM SauceNaoApiKey 随便填一个非空值（桩服务会模拟匿名拒绝行为）
   PixivRenamer.exe --cli --config 改好的配置.json --dry-run
   ```
   预期（会话 2 实测）：`感知分组：14 个文件归并为 13 组（1 组为近似重复），少发 1 次识图请求`、桩服务收到 **13** 次请求、3 个 66.8 分错配被 dHash 拦下、`可命名 11 / 14`。
3. **修复回归（输出目录）**（会话 1 手法）：建临时目录放一张 `12345678_p0.png`，`SourceDir = OutputDir`，`DryRun = true`，`ReverseEnabled = false`，期望"扫描到 1 个图片文件"而不是 0。
4. **验近似重复分布**（会话 2 手法）：用 Python + Pillow 独立实现 dHash，统计"分组数 / 近似重复组数"，与程序日志对照（当时 180 张 → 160 组、16 组重复）。

---

# 九、v2.3.0 续作（2026-10-08 12:13 起，不在上述会话归档内）

会话 3（`session-45162089`）在 12:12:48 中断，没有编译/自检/收尾。用户在后续会话里重试了同一需求，
四个目标全部落地并验证，版本号从 2.2 提到 **2.3.0**。本节记录续作实际做了什么，供以后追溯。

## 9.1 四个目标的落地情况

| 目标 | 落地情况 |
| --- | --- |
| 界面美化 | **已完成（会话 3 完全没开始的部分）**。侧边导航（整理 / pixiv·识图 / 任务与日志）+ 卡片式分区 + 自定义控件模板 + 底部状态栏（状态点/进度）+ 日志页仪表盘（四个计数卡 + 最近任务卡）。截图见 `docs\界面截图`。 |
| 统筹归档会话 | 已完成，产出就是本目录（纪要 + PROJECT-NOTES + 原始转录），并保留只读检索/导出脚本。 |
| 识图请求缓冲 | 已完成并端到端验证：`ReverseQueue`（排队 + 并发上限）+ `ReverseThrottle`（全局冷却闸门）。 |
| 未识别单独分类 | 已完成并端到端验证：按原名归入 `<输出目录或源目录>\未识别\`，报告与 `_unresolved.txt` 记录原因分类。 |

## 9.2 新增/修改的代码

| 文件 | 变化 |
| --- | --- |
| `Core\ReverseQueue.cs` | **新增**：`ReverseWorkItem`（一组一次查询）、`ReverseQueue`（并发上限 + 冷却等待 + 异常退化为串行收尾）、`ReverseThrottle`（`Block`/`Succeed`/`WaitAsync`，指数退避封顶）。 |
| `Core\ReverseSearchService.cs` | 增加 `ReverseThrottle` 注入、`LastProvider`、`SearchWithRetryAsync`（失败退避重试）、`IsRejected`/`IsPermanentRejection`；限流时 `Block`，匿名拒绝不冷却。 |
| `Core\RenamerEngine.cs` | 用 `ReverseQueue` 跑识图（worker 抽成 `SearchGroupAsync`）；未识别原因分类 `ClassifyUnresolved`、`BuildUnresolvedReason`、`UnresolvedDir()`；计划阶段给未识别项算归档目标；执行阶段移动/复制到「未识别」；扫描时跳过该文件夹；`_unresolved.txt` 加分类统计。 |
| `Core\HttpService.cs` | `HttpResult` 增加 `RetryAfterSeconds` / `IsRateLimited`，解析 `Retry-After`。 |
| `Core\AppConfig.cs` | 新增 `MoveUnresolvedToFolder`、`UnresolvedFolderName`、`ReverseQueueEnabled`、`ReverseConcurrency`、`ReverseCooldownMs`、`ReverseMaxCooldownMs`、`ReverseMaxRetries`、`ReverseTimeoutMs` 及 `Finalize` 收敛。 |
| `Core\Models.cs` | `FileItem` 增加 `UnresolvedReason` / `UnresolvedKind` / `ReverseProvider` / `ReverseTimedOut`。 |
| `Core\SelfTest.cs` | 新增未识别分类 7 项、未识别目录 2 项、配置收敛 2 项、冷却闸门 5 项；`--selftest` 37 → **53**。 |
| `Core\ReportService.cs` | `UndoService` 支持撤销 `unresolved-move`。 |
| `Core\CliRunner.cs` | 新增 `--reverse-concurrency`、`--no-reverse-queue`、`--unresolved-folder`、`--no-unresolved-folder`。 |
| `MainWindow.xaml(.cs)` | 界面整体重做；新增未识别开关/文件夹名、缓冲参数控件、计数卡、最近任务、状态点；`SelectPage`。 |
| `App.xaml.cs` | 新增 `--uitest [页码] [--uitest-exit 秒]` 界面自检；帮助文本与版本号更新。 |
| `scripts\package.ps1` | **修复既有 bug**：第 34 行 here-string 头 `@"` 后跟字面量 `` `r`n `` 导致脚本解析失败（dist 长期是旧 exe）；ZIP 名改 v2.3.0。 |
| 其他 | `scripts\*.ps1` 恢复 UTF-8 BOM（缺 BOM 时中文路径被按 ANSI 解码成乱码）；README / 使用说明 / CHANGELOG / 默认配置同步 2.3。 |

## 9.3 v2.3 的参数与默认值

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| `MoveUnresolvedToFolder` | true | 未识别文件单独归类 |
| `UnresolvedFolderName` | `未识别` | 归档文件夹名（`Finalize` 会做文件名清理） |
| `ReverseQueueEnabled` | true | 识图请求排队缓冲 |
| `ReverseConcurrency` | 1 | 识图并发上限（1 = 完全串行；关闭缓冲时回退到线程数） |
| `ReverseCooldownMs` | 12000 | 被限流后的基础冷却 |
| `ReverseMaxCooldownMs` | 120000 | 冷却上限（指数退避封顶） |
| `ReverseMaxRetries` | 2 | 单图识图失败重试次数 |
| `ReverseTimeoutMs` | 90000 | 单图识图等待上限（含冷却） |

## 9.4 v2.3 的验证记录

- `--selftest` **59/59 通过**（发布出的 dist exe 同样通过；含代理规范化 6 项）。
- **真实网络端到端（2026-10-08 12:48–12:51，本机代理 `127.0.0.1:7897` + 真实 SauceNAO API Key，5 张无 PID 图）**：
  - pixiv 连通性预检通过；SauceNAO 返回真实结果；报告 source 为 `SauceNAO->pixiv-ajax`，
    说明"识图命中 PID → 回查 pixiv 元数据"整条链路可用（**未填 Cookie 也成功**）；
  - SauceNAO 命中 **4/5**（另 1 条为 booru 转载，只有作者/标题没有 pixiv id）；
  - 取回的真实 PID 逐个用 pixiv 作品页核对，`63846901`（Bison倉鼠「✿」）、
    `89572673`（神北小毬「曲绘封面。」）、`59144746`（みゅとん/뮤우톤「がおー!くっちまうぞー!」）
    **作者与标题完全一致** → 实际数据上未出现误命名；
  - SauceNAO 免费账号短周期配额（4 次/30 秒）在下半程被吃满，程序打印「剩余配额：短周期 0」后继续，任务未中断；
  - ascii2d 仍被 Cloudflare 拦截（HTTP 403），提示明确并跳过，不影响 SauceNAO 结果。
- 本地桩 SauceNAO 端到端（7 张图，2 张构造为 `crc32(文件名含扩展名) % 4 == 2` 即"中置信度但缩略图不符"）：
  - 识图请求串行送出、进度推进正常；
  - 66.8 分错配候选被 dHash 拦下（差异 35/64）→ 该文件未识别；
  - 未识别文件被按原名移入 `out\未识别\`，报告 action = `unresolved-move`，`_unresolved.txt` 分类「识图复核未通过」；
  - 其余 4 张正常重命名并归类到 `测试画师A / 测试画师B / 转载画师`。
- 界面：用 `--uitest <页码>` 打开指定页面并截图核对，三个页面布局正常。
  过程中发现并修掉了一个真实布局 bug：共享输入框样式固定 `Height=32` 导致日志框无法撑满可用空间
  （诊断手法：用 `--uitest` 定时把 `ActualWidth/ActualHeight` 写到窗口标题，再读取标题）。
- 本次实测还发现并修掉：运行日志头部硬编码 `=== PixivRenamer 2.2 ===`；
  代理写 `127.0.0.1:7897`（无协议）能工作但太脆弱，现在 `Finalize` 会补 `http://`，
  非法代理会被清空并回退系统代理，`HttpService` 构造也加了兜底 catch。

> 桩服务的一个副作用（不是产品问题）：桩按 `90000000 + crc32 % 100000` 生成 PID，
> 可能撞上真实存在的作品（本次 `90094976` 恰好是真实作品），核对时看到真实页不要误会成产品 bug。

## 9.5 仍未验证 / 待办（更新 9.3 之后的状态）

- SauceNAO 真实**限流**（429 / `Retry-After`）行为这次没触发：短周期配额吃满时服务端只是把
  `short_remaining` 报成 0 并继续返回结果，因此"冷却闸门"在真实环境仍未得到验证（只有单测覆盖）。
- ascii2d 在真实网络下仍被 Cloudflare 拦（HTTP 403），**该引擎的真实命中率无法验证**；
  如需使用只能换网络/自建中转。
- `ReverseTimeoutMs=90000` 与 `ReverseMaxCooldownMs=120000` 的组合下，被限流时确实会出现
  "单图超时 → 归入识图限流待重试"的情况，这是设计行为（不误判成"无结果"），但大批量时的实际影响未实测。
- `dbmask=96` 与 `db=999` 的额度/命中率差异仍未实测。
- 归档目录 `docs\会话归档\_transcripts\` 体积较大（约 2.3 MB 文本），是否长期保留由你决定。
