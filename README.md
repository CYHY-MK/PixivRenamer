# PixivRenamer 1.0.0

一个 Windows 桌面工具：把大量下载的 pixiv 插画按 **作品 PID → 作者 / 标题** 重命名并分类整理。
文件名没有 PID、或缺少作者 / 标题时，会用 SauceNAO / ascii2d **识图**辅助查询来源。

- 项目主页：<https://github.com/CYHY-MK/PixivRenamer>
- 作者：CYHY-MK
- 许可：[MIT](LICENSE)
- 下载：<https://github.com/CYHY-MK/PixivRenamer/releases>

> **声明**：本工具仅用于整理你自己合法下载、自己有权处理的图片。请遵守 pixiv、SauceNAO、
> ascii2d 等网站的服务条款，控制请求频率，不要用于大规模爬取、商业用途或任何侵犯版权的行为。
> 使用本工具产生的一切后果由使用者自行承担。

## 功能

| 模块 | 说明 |
| --- | --- |
| PID 提取 | 从文件名 / 父目录识别 pixiv 作品号：`12345678_p0`、`illust_7654321`、`pixiv_id=`、`[8765432]`、`artworks-1234567-p1`、7~9 位裸数字；自动排除日期 |
| pixiv 查询 | AJAX 接口取作者 / 标题 / 标签 / 日期，失败抓 HTML 兜底；支持 Cookie（R-18 必需）、账号密码自动获取 Cookie、本地查询缓存、429/5xx 指数退避 |
| 识图回退 | SauceNAO / ascii2d；库索引优先（pixiv #5、pixivhistorical #6 最优先）；置信度 60~85 用 dHash 复核来源缩略图；近似重复图按 dHash 合并只查一次 |
| 请求缓冲 | 识图请求排队送出（并发默认 1），被限流自动全局冷却（12s 起、退避封顶 120s），失败退避重试 |
| 命名分类 | 自定义模板、按作者 / 日期分类、R-18 单独文件夹、多页 `_pN`、文件名非法字符与 Windows 保留名处理 |
| 未识别归类 | 认不出作者/标题的图片按原名收进 `未识别\`，`_unresolved.txt` 按 8 类原因统计 |
| 代理侦测 | 自动读 Clash 系配置端口 → 系统代理 → 常见本地端口，并逐个真实验证；支持四种代理模式 |
| 网络诊断 | 失败自动写 `error-reports\网络诊断_*.txt`，含候选代理检测结果与排查清单 |
| 报告与回滚 | CSV 报告、断点续跑、`--undo` 撤销（含未识别归档的移动） |
| 检查更新 | 启动静默检查 GitHub 最新 Release，有新版弹窗提示；也可手动检查 |
| 界面 | 侧边导航 + 卡片分区 + 日志仪表盘（任务记录 / 已识别 / 未识别 / 失败）+ 状态栏阶段提示，全程可取消 |

## 运行方式

### 便携版（推荐）
1. 到 [Releases](https://github.com/CYHY-MK/PixivRenamer/releases) 下载最新 `PixivRenamer_v1.0.0_portable.zip`；
2. 解压后双击 `PixivRenamer\PixivRenamer.exe`（自包含单文件，**不需要**安装 .NET / Python / VC++ 运行库）；
3. 如果 Windows SmartScreen 提示，右键 exe → 属性 → 解除锁定，或选择"仍要运行"。

### 免安装部署
双击 `安装到本用户.bat`：复制到 `%LOCALAPPDATA%\Programs\PixivRenamer` 并创建开始菜单快捷方式；
卸载运行 `卸载本用户安装.bat`。不需要管理员权限。

### 管理员权限（默认开启）
程序**默认以管理员身份启动**（会弹一次 UAC）。原因是要与浏览器配合（打开 pixiv 登录页、
复制 / 读取 Cookie）；当浏览器是"以管理员身份运行"时，普通权限的程序无法与它交互。

- 「整理」页底部「权限、更新与项目信息」卡片可查看当前权限、点"以管理员重启"、
  或取消勾选"默认以管理员身份启动"（保存配置后下次用普通权限）；
- UAC 里点"否"不会退出，本次以普通权限运行并在日志提示；
- `--cli` / `--selftest` / `--uitest` **不会**自动提权；界面模式可用 `--no-elevate` 跳过。

## 快速开始

1. 打开程序 → 「整理」页选择源文件夹（也可把文件夹拖进窗口）；
2. **第一次务必勾选"预览模式"**（不修改文件）→ 点"开始整理/预览"；
   日志会显示扫描、pixiv 查询、识图进度与限流冷却；
3. 确认日志与 CSV 报告里的命名、分类符合预期 → 取消预览模式再执行一次（会二次确认）；
4. 查询 R-18 / 受限作品时，在「pixiv / 识图」页填写浏览器 Cookie。

## 网络与代理（最常见的问题）

程序默认 **自动侦测本地代理**，大多数情况下什么都不用填：

1. Clash 系客户端配置文件里读到的端口（Clash Verge / Verge Rev 默认 `mixed-port: 7897`，
   Clash for Windows 默认 `7890`，v2rayN 默认 `10809`）；
2. Windows 系统代理设置（WinINET）；
3. 常见本地端口列表。

每个候选都会发一次真实的 CONNECT 请求验证能否当 HTTP 代理用，第一个真能用的才采用。

| 代理模式 | 说明 |
| --- | --- |
| **自动侦测（推荐）** | 依次尝试上述三条来源并逐个验证 |
| 手动指定代理 | 只用手填地址，例如 `127.0.0.1:7897`（不要带路径 / 空格） |
| 使用系统代理 | 读 Windows「Internet 选项」里的代理 |
| 直连 | 不使用代理 |

- 点 **「检测本地代理」** 会列出所有候选端口、来源、可用性，并给出推荐地址；
- 点 **「测试网络」** 失败时弹出详情窗口（代理解析 + 三站点连通性 + 处理建议），
  并把报告写到 `error-reports\网络诊断_时间戳.txt`——**出问题时把这份报告发到
  [Issues](https://github.com/CYHY-MK/PixivRenamer/issues) 即可定位**。

## 识图 API

- **SauceNAO**：必须填写 API Key（匿名调用会被服务端直接拒绝）。免费 Key：
  <https://saucenao.com/user.php?page=account>（界面里已提供可点击链接）；
- **ascii2d**：无需 Key，<https://ascii2d.net/>。该站有 Cloudflare 人机校验，
  过不了校验时程序会明确提示并跳过，不影响 SauceNAO 的结果。

识图判定：置信度 **≥85 直接采纳**；**60~85** 下载来源缩略图与本地图做感知哈希比对，
汉明距离超过阈值（默认 10/64）即丢弃——这一段正是"同角色 / 相似构图"误命名的高发区。

## 未识别图片单独分类

无法识别作者 / 标题的图片，正式执行时按**原名**收进 `<输出目录或源目录>\未识别\`：

- 报告 action 记为 `unresolved-move` / `unresolved-copy`，note 写明原因，`--undo` 可还原；
- `_unresolved.txt` 按原因分类：识图无结果 / 识图复核未通过 / 识图限流待重试 / 识图服务不可用 /
  PID 查询无结果 / pixiv 查询失败 / 文件过大或无法读取 / 无 PID 且识图无结果；
- 扫描时会跳过该归档文件夹，重复执行不会二次处理；想重试就把图片移回源目录再跑一次；
- 不想分类归档：取消勾选"未识别文件单独归类"。

## 检查更新

- **启动时静默检查**：查询 GitHub 最新 Release，只有发现新版本才弹窗（含更新说明与下载链接），
  可在界面里关闭；检查失败不打扰，只写日志；
- **手动检查**：「帮助 → 检查更新」，或「关于」窗口里的"检查更新"按钮；
- **命令行**：`PixivRenamer.exe --check-update`（退出码 0=已是最新 / 1=有新版本或检查失败）。

## 命令行（高级）

```bat
PixivRenamer.exe --selftest                 :: 内置自检（90 项）
PixivRenamer.exe --version                  :: 版本与运行环境
PixivRenamer.exe --check-update             :: 检查更新
PixivRenamer.exe --test-net                 :: 网络 / 代理诊断（报告写入 error-reports）
PixivRenamer.exe --cli "D:\pixiv" --dry-run --limit 20
PixivRenamer.exe --cli "D:\pixiv" --yes
PixivRenamer.exe --cli --undo "D:\pixiv\_pixiv_rename_report.csv"
```

| 参数 | 说明 |
| --- | --- |
| `--cli <目录>` | 命令行整理；默认需要 `--yes` 才真正改文件 |
| `--dry-run` / `-n` | 预览，不改文件 |
| `--limit N` | 只处理前 N 个文件，便于小样本试跑 |
| `--no-reverse` | 关闭识图回退 |
| `--sauce-key KEY` | 临时指定 SauceNAO API Key |
| `--report <csv>` | 指定报告路径 |
| `--undo <csv>` | 按报告撤销 |
| `--proxy 地址` / `--proxy-mode 模式` | 指定代理 / 代理模式（auto\|explicit\|system\|direct） |
| `--reverse-concurrency N` / `--no-reverse-queue` | 识图并发 / 关闭排队缓冲 |
| `--unresolved-folder 名字` / `--no-unresolved-folder` | 未识别归档目录名 / 关闭归类 |
| `--no-elevate` | 界面模式本次不自动提权 |

## 配置

配置文件是 exe 同目录的 `PixivRenamer.json`（界面"保存配置"生成）。常用字段：

| 字段 | 说明 |
| --- | --- |
| `SourceDir` / `OutputDir` | 源目录 / 输出目录（空 = 原地重命名） |
| `CopyMode` / `Overwrite` | 复制 / 覆盖 |
| `DryRun` | 预览模式（默认 `true`，安全第一） |
| `Template` | 命名模板，如 `{author} - {title} ({pid})` |
| `Organize` | `none` / `author` / `date` / `author_date` / `date_author` |
| `SplitR18` / `R18Folder` | R-18 单独文件夹 |
| `MoveUnresolvedToFolder` / `UnresolvedFolderName` | 未识别单独归类 / 文件夹名 |
| `ProxyMode` / `Proxy` | 代理模式 / 手动代理地址 |
| `Cookie` | pixiv Cookie（R-18 必需） |
| `SauceNaoApiKey` / `ReverseOrder` | 识图 Key / 服务顺序 |
| `MinSimilarity` / `VerifyByHash` / `HashMaxDistance` | 相似度下限 / dHash 复核 / 距离上限 |
| `GroupByPerceptualHash` | 近似重复图只查一次识图 |
| `ReverseQueueEnabled` / `ReverseConcurrency` | 识图排队缓冲 / 并发上限 |
| `ReverseCooldownMs` / `ReverseMaxCooldownMs` / `ReverseMaxRetries` | 限流冷却与重试 |
| `RunAsAdmin` | 默认以管理员身份启动 |
| `CheckUpdateOnStartup` | 启动时自动检查更新 |

命名模板变量：`{author} {title} {pid} {page} {date} {tags} {userid} {account} {r18}`。

## 从源码构建

环境：Windows 10/11 + .NET SDK 8.0 或更高。

```powershell
git clone https://github.com/CYHY-MK/PixivRenamer.git
cd PixivRenamer

# 构建
.\scripts\build.ps1

# 发布自包含单文件 exe 到 publish\win-x64
.\scripts\publish.ps1

# 生成 dist 便携目录 + ZIP + 免安装脚本（需先 publish）
.\scripts\package.ps1
```

工程位置：`src\PixivRenamer\PixivRenamer.csproj`（WPF，`net8.0-windows`）。

### 离线自测（不消耗 SauceNAO 配额）
仓库自带本地 SauceNAO 桩服务，可回归识图链路（上传 / 解析 / 索引排序 / dHash 复核 / 感知分组）：

```powershell
python .\scripts\mock-saucenao.py 8899
# 另开窗口：把配置里的 SauceNaoApiBase 指向 http://127.0.0.1:8899，SauceNaoApiKey 填任意非空值
.\PixivRenamer.exe --cli --config .\test-config.json --dry-run --limit 14
```

桩服务按上传文件名 `crc32 % 4` 返回四种典型结果（高置信度命中 / 中置信度缩略图一致 /
中置信度缩略图不符 / booru 转载）。想验证"未识别归档"，把测试图复制成
`crc32(文件名含扩展名) % 4 == 2` 的名字即可。

## 常见问题

**「pixiv 连接测试失败：请求超时」/「无法访问 pixiv」**
代理没生效。确认代理软件已启动 → 点「检测本地代理」看哪个端口可用 → 点「测试网络」看详细诊断。
端口对照：Clash Verge/Verge Rev `7897`，Clash for Windows `7890`，v2rayN `10809`。

**「填了代理还是连不上」**
端口要和代理软件的 `mixed-port` 一致（不是 `socks-port` / `port`）；手动指定时代理地址只写
`主机:端口`，不要带路径或空格；Clash 的 TUN 模式可能不监听 `mixed-port`，此时用「自动侦测」或「使用系统代理」。

**「全部文件都未识别」**
通常是 pixiv 与识图都没连上：先确认代理与 SauceNAO API Key，再跑预览模式看日志。

**中文 / 特殊字符文件名乱码**
程序使用 UTF-8 读写，文件名会做非法字符与 Windows 保留名处理；如遇到问题请附上 `error-reports`
与 `Logs\PixivRenamer.log` 提交 Issue。

## 目录结构

```
PixivRenamer/
 src\PixivRenamer\          WPF 工程与核心代码
   MainWindow.xaml(.cs)     界面（侧边导航 / 卡片分区 / 状态栏 / 任务统计）
   App.xaml(.cs)            入口 / CLI 路由 / --uitest
   Core\                    PID、HTTP、pixiv、识图、排队缓冲、代理侦测、命名、报告、更新检查、自检
   resources\app.ico        应用图标
 scripts\build.ps1          构建
 scripts\publish.ps1        发布自包含单文件
 scripts\package.ps1        生成 dist 便携包
 scripts\mock-saucenao.py   本地 SauceNAO 桩服务（开发用）
 docs\界面截图\              界面截图
 docs\会话归档\              开发过程中的决策与踩坑记录（PROJECT-NOTES.md 等）
 dist\                      便携目录 / ZIP / 免安装脚本
 CHANGELOG.md               版本更新记录
 LICENSE                    MIT + 免责声明
 README.md
```

## 反馈

- 问题 / 建议：<https://github.com/CYHY-MK/PixivRenamer/issues>
- 提交 Issue 时请附上：程序版本（`--version`）、`error-reports\网络诊断_*.txt`、
  `Logs\PixivRenamer.log`，以及触发问题的截图或步骤。

## 许可

[MIT](LICENSE) © 2026 CYHY-MK，附带免责声明。
