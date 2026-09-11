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

        private Task OnPushRequestReceived(InstallRequest request)
        {
            // 下载/解压/刮削流程将在后续步骤实现
            _hostApi.Info(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
                "PotatoDownload", $"收到推送: {request.Title}");
            return Task.CompletedTask;
        }

        protected Guid Id => Info.Id;
    }
}
