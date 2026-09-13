using System;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;
using PotatoVN.App.PluginBase.Helper;
using PotatoVN.App.PluginBase.Models;
using PotatoVN.App.PluginBase.Services;

namespace PotatoVN.App.PluginBase
{
    public partial class Plugin : IPlugin, IPluginSetting
    {
        public static IPotatoVnApi HostApi { get; private set; } = null!;
        public static DownloadManager DownloadManager { get; private set; } = null!;
        public static string DownloadPath => HostApi != null && PluginDataInstance != null
            ? PluginDataInstance.DownloadPath
            : string.Empty;

        private static PluginData PluginDataInstance { get; set; } = null!;
        private IPotatoVnApi _hostApi = null!;
        private PluginData _data = null!;
        private PushService _pushService = null!;
        
        public PluginInfo Info { get; } = new()
        {
            Id = new Guid("45a3c083-f22a-484f-8dcb-bef1bfd3c076"), 
            Name = "PotatoDownload",
            Description = "从 shionlib 自动推送到 PotatoVN 进行下载、解压和刮削。",
        };

        public async Task InitializeAsync(IPotatoVnApi hostApi)
        {
            _hostApi = hostApi;
            HostApi = hostApi;
            XamlResourceLocatorFactory.PackagePath = _hostApi.GetPluginPath();
            PluginLocalization.Initialize(hostApi); //初始化插件多国语言支持，如果你的插件不需要支持多语言，可以不调用这个方法，直接在代码里写死字符串即可。
            //注意：本插件UI一律使用纯C#构建，不要调用ResourceLoader加载XAML资源字典
            //（插件XAML依赖宿主v1.10.1+的承载机制，旧宿主上会XamlParseException）。
            var dataJson = await _hostApi.GetDataAsync();
            if (!string.IsNullOrWhiteSpace(dataJson))
            {
                try
                {
                    _data = System.Text.Json.JsonSerializer.Deserialize<PluginData>(dataJson) ?? new PluginData();
                }
                catch
                {
                    _data = new PluginData();
                }
            }
            else
            {
                _data = new PluginData();
            }
            _data.PropertyChanged += (_, _) => SaveData(); // 当Observable属性变化时自动保存数据，对于普通属性请手动调用SaveData
            PluginDataInstance = _data;
            InitUi();

            DownloadManager = new DownloadManager(_hostApi);
            DownloadManager.ActiveTaskCountChanged += count => UpdateDownloadSidebarState(count);
            _pushService = new PushService(_hostApi, DevReportInfo);
            _pushService.RequestReceived += OnPushRequestReceived;
            _pushService.Start();
        }

        public async Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
        {
            if (cts.IsCancellationRequested) return;
            if (_pushService is not null) await _pushService.StopAsync();
        }

        private void SaveData()
        {
            var dataJson = System.Text.Json.JsonSerializer.Serialize(_data);
            _ = _hostApi.SaveDataAsync(dataJson);
        }

        /// <summary>默认下载目录：系统盘（一定存在）下的 Galgame 文件夹，使用时自动创建。</summary>
        internal static string DefaultDownloadPath =>
            System.IO.Path.Combine(
                System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "Galgame");

        /// <summary>下载历史集合（持久化在插件数据里，随宿主保存）。</summary>
        internal static System.Collections.ObjectModel.ObservableCollection<DownloadRecord> HistoryCollection =>
            PluginDataInstance.History;

        /// <summary>立即保存插件数据（用于历史记录等不触发 PropertyChanged 的修改）。</summary>
        internal static void SaveDataNow()
        {
            var dataJson = System.Text.Json.JsonSerializer.Serialize(PluginDataInstance);
            _ = HostApi.SaveDataAsync(dataJson);
        }

        /// <summary>
        /// 开发期错误上报。正式版为空实现（避免污染报错库）；
        /// 开发调试时恢复为向 https://plugin.potatovn.net/api/vibe/plugins/{Id}/runtime-errors POST 的实现。
        /// </summary>
        public Task DevReportInfo(Exception? ex, string? msg) => Task.CompletedTask;

        private Task OnPushRequestReceived(InstallRequest request)
        {
            if (!_data.AutoDownload)
            {
                // 自动下载关闭：弹确认框，用户点击「下载」后才开始
                EnqueueConfirmation(request);
                return Task.CompletedTask;
            }
            _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                "PotatoDownload", $"开始下载: {request.Title}");
            _ = DownloadManager.EnqueueAsync(request);
            ShowDownloadDialog(); // Chrome 风格：新下载开始时直接呈现下载面板
            return Task.CompletedTask;
        }

        // ===== 确认下载（自动下载关闭时）=====
        private readonly System.Collections.Concurrent.ConcurrentQueue<InstallRequest> _confirmQueue = new();

        private void EnqueueConfirmation(InstallRequest request)
        {
            _confirmQueue.Enqueue(request);
            _hostApi.InvokeOnMainThread(PumpConfirmationQueue);
        }

        /// <summary>把待确认的推送排进对话框串行队列（与其他弹窗共享同一队列，永不撞车）。</summary>
        private void PumpConfirmationQueue()
        {
            while (_confirmQueue.TryDequeue(out var request))
            {
                var window = _hostApi.GetMainWindow();
                if (window is null)
                {
                    _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                        "PotatoDownload", $"收到推送（自动下载已关闭）: {request.Title}");
                    continue;
                }
                var captured = request;
                EnqueueDialog(async () =>
                {
                    var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
                    {
                        XamlRoot = window.Content.XamlRoot,
                        Title = "确认下载",
                        Content = $"游戏：{captured.Title}\n" +
                                  $"文件：{captured.FileName}（{Services.DownloadManager.FormatBytes((long)captured.Size)}）\n" +
                                  $"来源：{new Uri(captured.Url).Host}\n\n是否开始下载？",
                        PrimaryButtonText = "下载",
                        CloseButtonText = "取消",
                        DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                    };
                    var result = await dialog.ShowAsync();
                    if (result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
                    {
                        _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                            "PotatoDownload", $"开始下载: {captured.Title}");
                        _ = DownloadManager.EnqueueAsync(captured);
                        ShowDownloadDialog();
                    }
                    else
                    {
                        _hostApi.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                            $"PotatoDownload: user declined push ({captured.Title})");
                    }
                });
            }
        }

        protected Guid Id => Info.Id;
    }
}
