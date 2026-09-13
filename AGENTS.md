# AGENTS.md

> 本文件由 AI 自维护，是这个插件仓库**跨对话的长期记忆**。
> OpenHands 会把整份文件作为 repo skill 注入每次对话的 `<REPO_CONTEXT>` 系统提示，
> 因此你写进去的内容下一次对话依然看得到。

---

## 第一部分：协议（DO NOT EDIT — 协议区，禁止修改）

### 启动协议（每个新任务的第一步，先于一切其他操作）

1. 你正在阅读的这段就在 `<REPO_CONTEXT>` 里 —— 启动时已经被注入，**不需要再用 file_editor 读 AGENTS.md**。
2. 直接跳到下方「## 第二部分：记忆区」并扫读，然后用一两行简述「与本次任务相关的记忆条目」，再开始干活。
3. 记忆区为空 / 与任务无关时，明确说一句 `memory: nothing relevant`，再继续。

### 收尾协议（在你认为任务已完成、准备给用户最终回复之前）

按以下规则更新**记忆区**（仅记忆区！协议区一字不动），用 file_editor 工具写回 `./AGENTS.md`：

- **必须保存**：
  - 用户偏好/约定（"以后都用 X 风格"、"不要再做 Y"）
  - 项目事实（架构决策、依赖版本约束、外部系统位置、为什么这么做）
  - 校正/反馈（用户纠正过你的做法 —— 记下规则 + 原因）
  - 非显然的踩坑结论（从代码 / git log 看不出来的）
- **不要保存**：
  - 代码本身能表达的事（文件路径、函数签名、目录结构）
  - git log / git blame 已记录的变更历史
  - 本次会话内的临时状态、TODO 进度（这些走 TaskTracker）
  - 重复条目 —— 先查再写，能更新就别新增
- **写法**：
  - 用 file_editor 的精确替换，**只改记忆区内的对应小节**，不要重写整份文件。
  - 每条 ≤ 3 行；记忆区总长控制在 200 行内，超出时合并/精简旧条目。
  - 不在记忆里粘贴大段代码或日志 —— 留指针即可。
- **空更新也要说**：本次任务没有产生需要长期记住的新信息，
  在最终回复里加一行 `AGENTS.md memory: no update.`（不写文件）。

### 硬性规则

- **协议区禁止修改**：本节及上方，到 `<!-- MEMORY START -->` 之间的任何字节都不能动。
  Agent 一旦修改协议区，视为协议违反 —— 后端会告警并回滚。
- **启动时没扫读记忆区就调其他工具 = 协议违反**。
- **绝对不写入** 密钥 / token / 个人隐私（这是仓库内文件，会进 git）。
- 任何"我觉得不需要这条协议"的修改建议 —— 反馈给用户，由人改，不要自改。

---

## 第二部分：记忆区（你可以在这里更新）

<!-- MEMORY START -->

### User Preferences
<!-- 用户偏好与约定。例：- 提交信息一律使用中文 -->

### Project Facts
<!-- 架构、依赖、外部系统、为什么这么做。例：- 编译目标 net8.0-windows，原因：宿主 PotatoVN 限定 -->
- 本插件目标：接收 Shionlib 推送（potato-vn://install 深链）→ 下载/校验 → 解压 → 刮削入库
- Shionlib 推送协议（v1）：`potato-vn://install?v=1&provider=shionlib&resource_id&url&file_name&archive_format&size&checksum_algo(sha256|blake3)&checksum&expires_at&bgm_id&title[&vndb_id][&hikarinagi_id][&archive_password]`；Shionlib 前端目前只推 `reinamanager://`，需其配合加 PotatoVN 按钮（改 scheme 即可，参数兼容）
- PotatoVN 已注册 `potato-vn://` scheme（manifest）；插件通过订阅 `AppInstance.GetCurrent().Activated` 捕获协议激活（事件订阅安全做法见 Feedback 区）；`IPotatoVnApi.ActivationArgs` 虽随激活更新，但激活参数 COM 代理在回调结束后立即失效，插件轮询读不到
- 插件不能调用主程序 `UnpackGameTask`（在主程序程序集）；解压需插件自引 SharpCompress/SevenZip
- **插件只能调用宿主稳定版（v1.10.2）已有的插件 API**（2026-09 用户实测 MissingMethodException）：dev 版新增的 GetGameByUid/GetGameById/AddVirtualGameAsync(Galgame)/AddSourceAsync/AddGameToSource/SaveGameAsync/InvokeOnMainThreadAsync 一律不能用；精确刮削流程：GetAllGames 按 Ids 手动查重 → AddVirtualGame(name) 建占位后补设 `game.Ids[(int)RssType.*]`（宿主同对象引用，立即生效）→ AddGameInstallation(path)；模型/枚举（Galgame.Ids、RssType 含 Hikarinagi、NameOnlyGameMatchException、GalgameUid）两版一致可用；Plugin.ReportHostApiSurface 启动时上报宿主 API 面（发布前随 DevReportInfo 一并移除）
- **宿主 AddVirtualGame(name) 会按名字联网刮削，查无此游戏直接抛 PvnException**（requireConfirm:false 也一样）→ EnsurePlaceholderAsync 必须容错返回 null 继续主流程；E2E 测试 title 必须是真实可刮游戏（用 CLANNAD=bgm 13），bgm 237 是动漫《攻壳机动队》不是游戏；api.bgm.tv 在本机构建环境被墙，bgm.tv 网页版可达
- 参考实现：ReinaManager `src-tauri/src/install/`（protocol/download/workflow），Shionlib `apps/frontend/components/game/download/helpers/reina.ts`
- 下载架构：DownloadService 多线程分块（4 连接 × 4MB 块，`File.OpenHandle`+`RandomAccess` 定位写，Range 探测失败退化为单连接续传），.part + .part.watermark 断点续传（水位=从头连续已落盘字节）；压缩包与 .part 放在下载目录下 `.potatodownload\` 暂存子目录（不碰用户同名文件）；哈希校验在 DownloadManager 做，失败即删文件；DownloadManager 串行队列 + ObservableCollection<DownloadTask>（增删遍历只在主线程）供 UI 绑定；去重只对活动任务，失败后可再推重试
- UI：侧边栏按钮"下载"→ ContentDialog 弹窗（DownloadProgressDialog，纯C#，Chrome 风格：进行中+历史记录两区，速度 EMA 平滑 + 500ms 限流），无独立页面；设置页 UserControl1（纯C#）；主题资源查找走 Helper/PluginTheme
- 交互语义：自动下载 ON=立即下载并自动弹下载面板；OFF=弹"确认下载"ContentDialog；**全部弹窗（确认/下载面板）经 Plugin_Ui 的 EnqueueDialog 串行协调器**——ContentDialog 同时只能开一个，各自 ShowAsync 会撞车静默吞请求（2026-09 远程报错实证）；推送到达自动弹面板是**宿主 DefaultActivationHandler 导航不可抑制**的替代方案（插件 API 无法阻止跳起始页）
- 断点续传坑：.part+水位在失败中断后会残留；DownloadAsync 开头对"水位与文件大小都达预期"的 .part 直接复用跳过下载；DownloadSequentialAsync 里 committed>0 但响应不是 206（服务端忽略 Range 整包返回）必须清零从头覆盖，否则重复追加成 2 倍大小（2026-09 实测 363→726）
- 下载历史持久化在 PluginData.History（get-only ObservableCollection，STJ 可 populate；集合变更不触发 PropertyChanged，需 Plugin.SaveDataNow() 手动保存）；侧边栏按钮状态切换靠 Unregister+Register（宿主无原地更新接口）
- DevReportInfo 自 v0.1.0 起为空实现（plan/main 一致）；需要远程诊断时临时恢复上报，发布前改回
- 测验网站：repo/docs/index.html，GitHub Pages 从 plan 分支 /docs 发布（https://luoyuxiaoxiao.github.io/PotatoDownload/），也可 file:// 直开；构链/触发逻辑逐字移植 Shionlib helpers/{protocol,potatovn}.ts（URLSearchParams 编码 + 隐藏 a 点击 + 可调"签名等待"延时），**移植段不要改**，测试开关（provider 覆盖/缺参/不校验/密码/格式覆盖）只在 buildInstallUrl 包装层（等价性自检 `Tests/SiteCheck/check.ts`，deno 对照本机 Shionlib 克隆逐字节比对，改页面构链后必跑）；载荷 docs/payload/test_game.zip 经 Pages 公网直链下发（SSRF 修复后本地服务器不可用），改载荷后跑 make-payload.ps1 并更新页面 PAYLOAD 常量；宿主侧安装入口：插件页"从本地压缩包安装"（AddPluginFromLocalZip）
- 插件内推送测试（侧边栏按钮/TestPushDialog/Helper/TestPush.cs）已于 2026-09-13 移除，测验统一走 docs 网站，发布前不再需要删测试代码

### Feedback / Lessons
<!-- 用户纠正过的做法 + 原因。例：- 不要 mock 数据库测试，原因：上次 mock 通过但生产迁移失败 -->
- 仓库根必须有 NuGet.Config（globalPackagesFolder=C:\pvn-vibe\nuget-cache），否则 terminal restore 与 MCP build 缓存分裂（新包 MCP 找不到）
- 插件 csproj 必须含模板的 PackPlugin/_StampPluginNamespace target 才能产出 artifacts/plugin.pvnplugin.zip；模板在 C:\pvn-vibe\plugin-base
- 模板 csproj 无 ImplicitUsings，新 .cs 文件需手写 using System/IO/Linq/Net.Http 等
- 带 BOM 的 .cs 文件 file_editor 会误判为二进制，用 PowerShell ReadAllText 确认内容
- BLAKE3 用 Blake3.Managed NuGet（纯托管）；SharpCompress 用 0.50.4（0.38 有漏洞 NU1902）
- **SharpCompress 0.50.4 用法坑（2026-09-13 自检实证）**：`ExtractAllEntries()` 只能用于 solid 或 7z，其它格式抛异常；`ArchiveFactory` 打不开 tar.gz/bz2/xz/zst，tar 系列必须按 archive_format 用 `options.Providers.CreateDecompressStream` 自己剥外层再交 `ReaderFactory.OpenReader`（让它直接探测压缩 tar 会因 bz2 首块 ~900KB 撞回卷缓冲上限）；GZip 解压流没读到尾就 Dispose 会抛 CRC 异常，清理时吞掉；zip-slip/符号链接越界由库自带目标目录检查拒绝（已验证）
- **多线程共享一个 FileStream Seek+Write 会串位**（旧分块下载的隐藏 bug，只在 >4MB 且服务端支持 Range 时触发）；水位必须是连续前缀，块乱序完成时只推进连续部分，否则续传漏块
- 核心逻辑自检工程 `Tests/CoreChecks`（net8.0 控制台，链接编译 InstallRequest/DownloadService/UnpackService，本地 HttpListener 模拟各种异常服务端；100 项）：`cd Tests/CoreChecks && dotnet run`，`CORECHECKS_ONLINE=1` 额外跑真实 HTTPS；Linux 上须 `NUGET_PACKAGES=~/.nuget/packages` 覆盖根 NuGet.Config 里的 Windows 路径；本机 .NET 8 SDK 装在 ~/.dotnet（`DOTNET_ROOT=~/.dotnet`）；WinUI 相关文件（Plugin*.cs/Controls/PushService）只能在 Windows 构建机编译
- **仓库 .cs 源文件是 CRLF + BOM**：整文件重写后要把行尾/BOM 归一化回去，否则 diff 全文件变红
- 安全审查结论（2026-09-13，已修）：压缩包顶层目录名 `..`/同名目录导致 `Directory.Delete` 误删（现只删带 `.potatodownload-incomplete` 标记的自建目录，其它改用"名称 (2)"）；日志落签名直链与 archive_password（现 RedactForLog 脱敏）；服务端多发数据无上限/分块回 200 越界写/无空闲超时永久卡队列；SSRF 只查字面主机（现 SocketsHttpHandler.ConnectCallback 按解析 IP 拒绝内网含重定向，IPv6 ULA/映射地址覆盖）；size 无上限直接预分配（现 MaxSize 512GiB + 磁盘可用空间检查）；file_name 未拒 Windows 非法字符/保留名；永久去重键阻断失败重试；历史保存与主线程插入竞态；占位游戏在下载前创建留下空条目；卸载不取消下载锁 DLL。AutoDownload 默认值已改为关（2026-09-13，任意网页触发深链不再静默下载+解压+入库；首次推送先弹确认框）
- **激活参数的 COM 代理在激活回调结束后立即失效**（2026-09 远程诊断实证：轮询读 Kind 即抛 0x800706BA 类错误；GetActivatedEventArgs 确实反映热激活，但对象已死）→ **热激活必须订阅 AppInstance.Activated** 并在回调里同步取出 URI 值（只带托管值出来，绝不存 args 引用）；冷启动不触发该事件，由轮询 GetActivatedEventArgs 兜底（初始参数长期有效；同一引用只读一次，失效属预期静默跳过）。宿主 DefaultActivationHandler 对任何激活都导航起始页——用户看到跳转 ≠ 插件收到激活
- **静态事件订阅的安全做法**（旧规则"绝不订阅"源于未做清理时的 DLL 锁定 UnauthorizedAccessException）：StopAsync 必须 退订 + GC.Collect×2/WaitForPendingFinalizers 释放 WinRT CCW + await 后台 Task 停止 + 清空事件委托；验证方式：插件热更新/卸载一次看是否再报文件占用
- **宿主 InfoService.Log 的 Informational/Success 级别和 DeveloperEvent 都会被 DevelopmentMode 开关过滤**：给普通用户排查必须用 Warning/Error 级别（始终落 Logs\log.txt）+ DevReportInfo 远程上报（PushService.ReportThrottled 限流）
- **工作区可能被平台重置**（2026-09-13 实例：工作树被重置为 main 的 Initial commit，源文件全删，但 plan 分支的 git 对象幸存 → git checkout plan 一键全恢复；obj/Stamped 也可作最后退路）。教训：关键节点勤 push 到远程，不要只依赖本地提交
- 公共 base64 echo 端点只有 `httpbingo.org/base64/{base64url}` 字节级可靠（2026-09 实测 sha256 完全一致）；httpbin.org 的 /base64 对含 `+`/`/` 的标准 base64 一律 404（百分号编码也不行）——构造测试下载地址别用 httpbin
- **插件 UI 禁用 XAML，一律纯 C#**：插件 XAML 依赖宿主 v1.10.1+ 的 PluginXamlHost（注册插件 IXamlMetadataProvider + ms-appx 绝对路径 LoadComponent），旧宿主 CreateSettingUi 直接 XamlParseException（2026-09 用户实测崩溃）；不要调用 ResourceLoader.Initialize/加载 Styles 字典；C# 取主题资源用 PluginTheme（ResourceDictionary.TryGetValue 不进 ThemeDictionaries，需递归且必须带回退值）

### References
<!-- 外部资源指针。例：- 报错日志查 Grafana: grafana.internal/d/plugin-runtime -->
- Shionlib 仓库：github.com/Ringyuki/shionlib；ReinaManager：github.com/huoshen80/ReinaManager
- 本插件远程仓库：github.com/luoyuxiaoxiao/PotatoDownload（main 与 plan 均已推送；用户明确要求 push 到 main）
- **分支分工（用户约定）：plan=开发线，main=发布线**；2026-09-13 起两分支插件代码一致（插件内测试功能已移除，DevReportInfo 空实现），plan 额外承载 docs/ 测验网站并作为 GitHub Pages 发布源；v0.1.0 已发布到应用市场（tag v0.1.0，包页 plugin.api.potatovn.net/pvn-plugin/package/dfb57882-7b2f-4db3-8fe8-5f3517d1f4c8/0.1.0）
- publish_plugin 流程：build_plugin → upload_test_build 拿 artifact_id → publish_plugin(artifact_id, version, changelog, plugin_info)；**首次发布必须先随调用提交 plugin_info**（name/description/author/homepage），否则 400 "Plugin info must be submitted before publishing"
- Windows 编译/E2E 由用户手动进行（本机 Linux 无法编译 WinUI 文件；用户有 Azure CLI，可临时开 Windows VM），AI 只交付源码改动并等用户回传构建结果，不要自行搭 CI 或开 VM
- **Shionlib 上游支持 PR（2026-09-13 已在 fork 上完成）**：本地克隆 ~/Projects/scratch/shionlib（origin=fork SSH，upstream=Ringyuki），分支 feat/potatovn-download 已推到 fork；改法完全镜像上游 #13 ReinaManager（helpers/potatovn.ts + ways/PotatoVN.tsx + settings/PotatoVN.tsx + store showPotatoVN + zh/en/ja 文案 + guides/potatovn-download.mdx），PR 描述草稿在 ~/Projects/scratch/shionlib-pr-body.md；**用户要求：不要改动上游已有文件如 reina.ts（别人的源码），只做新增+接线**；已开 PR #18 到上游 `dev` 分支（仓库 CI 只对 main 触发，PR 上无自动检查，靠本地 format/lint/i18n/tsc/test:cov 全过）；frontmatter 作者信息已补（uid 8395、头像 t.shionlib.com/user/8395/avatar/59405d19-…webp），banner 复用 potatovn-sync 的图；feat/partner-download-api 是后端机器鉴权 API，与深链推送无关，不必模仿
- 应用市场包页当前不可访问（发布待审核）——文档里只写"插件市场搜索 PotatoDownload"+GitHub 仓库链接，不放包页 URL
- Shionlib 仓库检查：husky pre-commit 跑 lint-staged，pre-push 跑全仓 typecheck + 前端/后端/og 单测（后端 jest 很慢，push 需数分钟）；CI 前端门槛 = prettier/eslint/i18n:check/tsc/test:cov（覆盖率阈值 statements 70）
- 默认下载目录：系统盘 Galgame 文件夹（Plugin.DefaultDownloadPath，Path.GetPathRoot(Environment.SystemDirectory)，一定存在；留空设置项时的回退）

<!-- MEMORY END -->
