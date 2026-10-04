# PotatoDownload
从shionlib自动推送到PotatoVN进行下载，解压和刮削。

推送测验网站（与 Shionlib 相同的构链与触发方式）：https://luoyuxiaoxiao.github.io/PotatoDownload/ ，源码在 plan 分支 `docs/`。

下载使用最多 6 个连接，大文件每块 64 MiB；中途断流后从块内已写位置继续，暂停时保存连续前缀。

Windows 上的 7z 解压使用随插件附带的官方 `7z.dll`，无需另外安装 7-Zip，支持 x86、x64、ARM64。其他格式和非 Windows 核心测试继续使用 SharpCompress。原生文件的来源、校验值和许可证见插件目录下的 `Native/7zip/`。

面板中的百分比表示当前下载或解压阶段，向下保留一位小数；校验和入库阶段显示正在执行的动作。解压按实际输出字节更新，并显示当前文件。短暂停顿由三秒滑窗平滑，持续没有新下载数据时显示“等待数据”。

核心检查：`dotnet run --project Tests/CoreChecks/CoreChecks.csproj`。Linux 需设置 `NUGET_PACKAGES="$HOME/.nuget/packages"`，避免根 NuGet.Config 的 Windows 缓存路径；仅安装 .NET 10 运行时的 Windows 构建机还需设置 `DOTNET_ROLL_FORWARD=LatestMajor`。

Windows 验收重点：

- 用包含大文件的 solid 7z（含加密头版本）确认文件内容、密码处理和解压进度持续更新。
- 在下载或解压中暂停、继续、取消，确认续传与清理符合预期；99.5% 不应显示为 100%。
- 解压中卸载或热更新插件，确认等待任务退出后没有 `7z.dll` 文件占用错误。
