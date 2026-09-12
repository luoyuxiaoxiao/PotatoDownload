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
- 下载架构：DownloadService 多线程分块（4 连接 × 4MB 块，Range 探测失败退化为单连接续传），.part + .part.watermark 断点续传；DownloadManager 串行队列 + ObservableCollection<DownloadTask> 供 UI 绑定
- UI：侧边栏按钮"下载"→ ContentDialog 弹窗（DownloadProgressDialog，纯C#，Chrome 风格：进行中+历史记录两区，速度 EMA 平滑 + 500ms 限流），无独立页面；设置页 UserControl1（纯C#）；主题资源查找走 Helper/PluginTheme
- 交互语义：自动下载 ON=立即下载并自动弹下载面板；OFF=弹"确认下载"ContentDialog（ContentDialog 同时只能开一个，用 FIFO 队列串行）；推送到达自动弹面板是**宿主 DefaultActivationHandler 导航不可抑制**的替代方案（插件 API 无法阻止跳起始页）
- 下载历史持久化在 PluginData.History（get-only ObservableCollection，STJ 可 populate；集合变更不触发 PropertyChanged，需 Plugin.SaveDataNow() 手动保存）；侧边栏按钮状态切换靠 Unregister+Register（宿主无原地更新接口）
- DevReportInfo 已在 Plugin.cs 实现（上传到应用市场/正式版前必须改为空实现）
- 测验平台：repo/TestPlatform/index.html（file:// 直开；E2E/过期/坏校验/SSRF/缺参/确认下载预设 + 自定义构造器 + 发送历史）；载荷 payload/test_game.zip（363B，含 CLANNAD/ 顶层目录，改内容后跑 make-payload.ps1 并更新页面 PAYLOAD 常量）；宿主侧安装入口：插件页"从本地压缩包安装"（AddPluginFromLocalZip），可直接选 artifacts/plugin.pvnplugin.zip
- **测试功能已内置进插件**：侧边栏"推送测试"按钮 → TestPushDialog（Helper/TestPush.cs 构造 6 种预设深链，ShellExecute 触发，等效浏览器点击）。**发布应用市场前必须移除**（与 DevReportInfo 空实现同级要求）；用户够不到构建机 C 盘，一切交付只能走 upload_test_build 的公网链接

### Feedback / Lessons
<!-- 用户纠正过的做法 + 原因。例：- 不要 mock 数据库测试，原因：上次 mock 通过但生产迁移失败 -->
- 仓库根必须有 NuGet.Config（globalPackagesFolder=C:\pvn-vibe\nuget-cache），否则 terminal restore 与 MCP build 缓存分裂（新包 MCP 找不到）
- 插件 csproj 必须含模板的 PackPlugin/_StampPluginNamespace target 才能产出 artifacts/plugin.pvnplugin.zip；模板在 C:\pvn-vibe\plugin-base
- 模板 csproj 无 ImplicitUsings，新 .cs 文件需手写 using System/IO/Linq/Net.Http 等
- 带 BOM 的 .cs 文件 file_editor 会误判为二进制，用 PowerShell ReadAllText 确认内容
- BLAKE3 用 Blake3.Managed NuGet（纯托管）；SharpCompress 用 0.50.4（0.38 有漏洞 NU1902）
- **激活参数的 COM 代理在激活回调结束后立即失效**（2026-09 远程诊断实证：轮询读 Kind 即抛 0x800706BA 类错误；GetActivatedEventArgs 确实反映热激活，但对象已死）→ **热激活必须订阅 AppInstance.Activated** 并在回调里同步取出 URI 值（只带托管值出来，绝不存 args 引用）；冷启动不触发该事件，由轮询 GetActivatedEventArgs 兜底（初始参数长期有效；同一引用只读一次，失效属预期静默跳过）。宿主 DefaultActivationHandler 对任何激活都导航起始页——用户看到跳转 ≠ 插件收到激活
- **静态事件订阅的安全做法**（旧规则"绝不订阅"源于未做清理时的 DLL 锁定 UnauthorizedAccessException）：StopAsync 必须 退订 + GC.Collect×2/WaitForPendingFinalizers 释放 WinRT CCW + await 后台 Task 停止 + 清空事件委托；验证方式：插件热更新/卸载一次看是否再报文件占用
- **宿主 InfoService.Log 的 Informational/Success 级别和 DeveloperEvent 都会被 DevelopmentMode 开关过滤**：给普通用户排查必须用 Warning/Error 级别（始终落 Logs\log.txt）+ DevReportInfo 远程上报（PushService.ReportThrottled 限流）
- 公共 base64 echo 端点只有 `httpbingo.org/base64/{base64url}` 字节级可靠（2026-09 实测 sha256 完全一致）；httpbin.org 的 /base64 对含 `+`/`/` 的标准 base64 一律 404（百分号编码也不行）——构造测试下载地址别用 httpbin
- **插件 UI 禁用 XAML，一律纯 C#**：插件 XAML 依赖宿主 v1.10.1+ 的 PluginXamlHost（注册插件 IXamlMetadataProvider + ms-appx 绝对路径 LoadComponent），旧宿主 CreateSettingUi 直接 XamlParseException（2026-09 用户实测崩溃）；不要调用 ResourceLoader.Initialize/加载 Styles 字典；C# 取主题资源用 PluginTheme（ResourceDictionary.TryGetValue 不进 ThemeDictionaries，需递归且必须带回退值）

### References
<!-- 外部资源指针。例：- 报错日志查 Grafana: grafana.internal/d/plugin-runtime -->
- Shionlib 仓库：github.com/Ringyuki/shionlib；ReinaManager：github.com/huoshen80/ReinaManager

<!-- MEMORY END -->
