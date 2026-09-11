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
        private IPotatoVnApi _hostApi = null!;
        private PluginData _data = new ();
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
            ResourceLoader.Initialize(); //初始化XAML字典加载器，资源用法请参考ResourceLoader类的注释
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
            _data.PropertyChanged += (_, _) => SaveData(); // 当Observable属性变化时自动保存数据，对于普通属性请手动调用SaveData
            InitUi();

            _pushService = new PushService(_hostApi);
            _pushService.RequestReceived += OnPushRequestReceived;
            _pushService.Start();
        }
        
        public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
        {
            if (cts.IsCancellationRequested) return Task.FromCanceled(cts);
            _pushService?.Stop();
            ResourceLoader.Unload(); // 卸载XAML资源字典
            return Task.CompletedTask;
        }
        
        private void SaveData()
        {
            var dataJson = System.Text.Json.JsonSerializer.Serialize(_data);
            _ = _hostApi.SaveDataAsync(dataJson);
        }

        private async Task OnPushRequestReceived(InstallRequest request)
        {
            try
            {
                if (!_data.AutoDownload)
                {
                    _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                        "PotatoDownload", $"收到推送（自动下载已关闭）: {request.Title}");
                    return;
                }

                _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                    "PotatoDownload", $"开始处理推送: {request.Title}");

                // 1. 创建带外部 ID 的占位游戏（用于精确匹配）
                var library = new LibraryService(_hostApi);
                await library.EnsurePlaceholderAsync(request);

                // 2. 下载目录：设置项或插件数据目录下
                var downloadDir = string.IsNullOrWhiteSpace(_data.DownloadPath)
                    ? System.IO.Path.Combine(_hostApi.GetPluginPath(), "downloads")
                    : _data.DownloadPath;
                System.IO.Directory.CreateDirectory(downloadDir);
                var packPath = System.IO.Path.Combine(downloadDir, request.FileName);

                // 3. 下载 + 校验
                var download = new DownloadService();
                await download.DownloadAsync(request, packPath,
                    (current, total) => _hostApi.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
                        $"PotatoDownload: 下载中 {current}/{total}"));
                await DownloadService.VerifyChecksumAsync(request, packPath);

                // 4. 解压
                var gameDirName = UnpackService.ResolveGameDirectoryName(request, packPath);
                var gamePath = System.IO.Path.Combine(downloadDir, gameDirName);
                if (System.IO.Directory.Exists(gamePath))
                    System.IO.Directory.Delete(gamePath, true);
                System.IO.Directory.CreateDirectory(gamePath);
                await UnpackService.UnpackAsync(request, packPath, gamePath);

                // 5. 入库 + 刮削
                await library.AddInstallationAsync(request, gamePath);

                // 6. 清理压缩包
                System.IO.File.Delete(packPath);

                _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
                    "PotatoDownload", $"完成: {request.Title}");
            }
            catch (Exception e)
            {
                _hostApi.Event(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, "PotatoDownload",
                    e, $"处理推送失败: {request.Title}");
            }
        }

        protected Guid Id => Info.Id;
    }
}
