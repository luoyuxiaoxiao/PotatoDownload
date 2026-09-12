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
- PotatoVN 已注册 `potato-vn://` scheme（manifest）；插件可订阅 `AppInstance.GetCurrent().Activated` 捕获协议激活（WinAppSDK API，插件可用）；`IPotatoVnApi.ActivationArgs` 只反映初始化时参数
- 插件不能调用主程序 `UnpackGameTask`（在主程序程序集）；解压需插件自引 SharpCompress/SevenZip
- 精确刮削：构造 Galgame 设 `Ids[(int)RssType.Bangumi/Vndb/Hikarinagi]` → `AddVirtualGameAsync` 建占位 → `AddGameInstallation(path)` 关联路径（UID 精确命中）
- 参考实现：ReinaManager `src-tauri/src/install/`（protocol/download/workflow），Shionlib `apps/frontend/components/game/download/helpers/reina.ts`
- 下载架构：DownloadService 多线程分块（4 连接 × 4MB 块，Range 探测失败退化为单连接续传），.part + .part.watermark 断点续传；DownloadManager 串行队列 + ObservableCollection<DownloadTask> 供 UI 绑定
- UI：侧边栏按钮"下载"→ ContentDialog 弹窗（DownloadProgressDialog，纯C#），无独立页面；设置页 UserControl1（纯C#，下载目录 + 自动下载开关）；主题资源查找走 Helper/PluginTheme
- DevReportInfo 已在 Plugin.cs 实现（上传到应用市场/正式版前必须改为空实现）

### Feedback / Lessons
<!-- 用户纠正过的做法 + 原因。例：- 不要 mock 数据库测试，原因：上次 mock 通过但生产迁移失败 -->
- 仓库根必须有 NuGet.Config（globalPackagesFolder=C:\pvn-vibe\nuget-cache），否则 terminal restore 与 MCP build 缓存分裂（新包 MCP 找不到）
- 插件 csproj 必须含模板的 PackPlugin/_StampPluginNamespace target 才能产出 artifacts/plugin.pvnplugin.zip；模板在 C:\pvn-vibe\plugin-base
- 模板 csproj 无 ImplicitUsings，新 .cs 文件需手写 using System/IO/Linq/Net.Http 等
- 带 BOM 的 .cs 文件 file_editor 会误判为二进制，用 PowerShell ReadAllText 确认内容
- BLAKE3 用 Blake3.Managed NuGet（纯托管）；SharpCompress 用 0.50.4（0.38 有漏洞 NU1902）
- **插件绝不能订阅宿主进程静态事件**（AppInstance.Activated 等）：事件委托锁定插件程序集 → 更新/卸载时 DLL 删除失败（UnauthorizedAccessException）。改用轮询 IPotatoVnApi.ActivationArgs（宿主每次激活更新该属性）；后台 Task 停止时必须 await 完成并清空事件委托
- **插件 UI 禁用 XAML，一律纯 C#**：插件 XAML 依赖宿主 v1.10.1+ 的 PluginXamlHost（注册插件 IXamlMetadataProvider + ms-appx 绝对路径 LoadComponent），旧宿主 CreateSettingUi 直接 XamlParseException（2026-09 用户实测崩溃）；不要调用 ResourceLoader.Initialize/加载 Styles 字典；C# 取主题资源用 PluginTheme（ResourceDictionary.TryGetValue 不进 ThemeDictionaries，需递归且必须带回退值）

### References
<!-- 外部资源指针。例：- 报错日志查 Grafana: grafana.internal/d/plugin-runtime -->
- Shionlib 仓库：github.com/Ringyuki/shionlib；ReinaManager：github.com/huoshen80/ReinaManager

<!-- MEMORY END -->
