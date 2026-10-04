using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Models;

// 宿主 API 的最小桩：让 DownloadManager.cs 能在纯 net8.0 下编译并跑通 下载→校验→解压→入库(桩)→完成/暂停/取消。
// 只声明 DownloadManager 用到的成员；命名空间与宿主契约一致，插件源码原样链接进来。

namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity
    {
        Informational,
        Success,
        Warning,
        Error,
    }
}

namespace GalgameManager.WinApp.Base.Contracts
{
    public interface IPotatoVnApi
    {
        void InvokeOnMainThread(Action action);
        void Log(InfoBarSeverity severity = InfoBarSeverity.Warning, string msg = "");
        void Info(InfoBarSeverity infoBarSeverity, string? title = null, string? msg = null, int? displayTimeMs = 3000);
        void Event(InfoBarSeverity infoBarSeverity, string title, Exception? exception = null, string? msg = null,
            Action? callbackAction = null, string? callbackButtonText = null);
    }
}

namespace PotatoVN.App.PluginBase
{
    /// <summary>插件静态入口的桩：只有 DownloadManager 读到的几个成员。</summary>
    public static class Plugin
    {
        public static string DownloadPath { get; set; } = string.Empty;
        internal static string DefaultDownloadPath => DownloadPath;
        internal static ObservableCollection<DownloadRecord> HistoryCollection { get; } = [];
        internal static void SaveDataNow() { }
    }
}

namespace PotatoVN.App.PluginBase.Services
{
    /// <summary>入库刮削服务的桩（真实实现依赖宿主 WinUI 程序集）。</summary>
    public class LibraryService(GalgameManager.WinApp.Base.Contracts.IPotatoVnApi hostApi)
    {
        public Task<object?> EnsurePlaceholderAsync(InstallRequest request) => Task.FromResult<object?>(hostApi is null ? null : request);
        public Task AddInstallationAsync(InstallRequest request, string gamePath) => Task.CompletedTask;
    }
}

namespace CoreChecks
{
    /// <summary>无 UI 线程：InvokeOnMainThread 直接内联执行，其余通知只计数。</summary>
    internal sealed class HostStub : GalgameManager.WinApp.Base.Contracts.IPotatoVnApi
    {
        public int Errors;
        public void InvokeOnMainThread(Action action) => action();
        public void Log(InfoBarSeverity severity = InfoBarSeverity.Warning, string msg = "") { }
        public void Info(InfoBarSeverity infoBarSeverity, string? title = null, string? msg = null, int? displayTimeMs = 3000) { }
        public void Event(InfoBarSeverity infoBarSeverity, string title, Exception? exception = null, string? msg = null,
            Action? callbackAction = null, string? callbackButtonText = null) => Errors++;
    }
}
