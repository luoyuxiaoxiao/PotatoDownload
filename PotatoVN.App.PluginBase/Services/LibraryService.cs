using System;
using System.Threading.Tasks;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Contracts;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 刮削入库服务：创建带外部 ID 的游戏占位，解压完成后关联本地路径。
/// 注意：只允许使用宿主稳定版（v1.10.2）就有的插件 API。
/// GetGameByUid / GetGameById / AddVirtualGameAsync(Galgame) / AddSourceAsync / AddGameToSource
/// 均为 dev 版新增，在稳定版宿主上调用会抛 MissingMethodException（2026-09 用户实测）。
/// </summary>
public class LibraryService
{
    private readonly IPotatoVnApi _hostApi;

    public LibraryService(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
    }

    /// <summary>
    /// 创建带外部 ID（Bangumi/VNDB/Hikarinagi）的游戏占位，用于后续 UID 精确匹配。
    /// 若库中已存在同一游戏（任一外部 ID 命中）则直接返回。
    /// 占位创建失败（如标题在信息源中查无此游戏）不阻断主流程，返回 null——
    /// 后续 AddGameInstallation 会按目录名自行刮削匹配。
    /// </summary>
    public async Task<Galgame?> EnsurePlaceholderAsync(InstallRequest request)
    {
        try
        {
            if (FindExisting(request) is { } existing)
                return existing;

            // 稳定版接口没有 AddVirtualGameAsync(Galgame)，先按名称建占位再补外部 ID。
            // 返回的是宿主库中同一对象引用，设置 Ids 立即对宿主的 UID 匹配生效。
            // 注意：宿主的 AddVirtualGame 会按名称在线刮削，查无此游戏时抛 PvnException。
            var game = await _hostApi.AddVirtualGame(request.Title, force: false, requireConfirm: false);
            if (!string.IsNullOrEmpty(request.BgmId))
                game.Ids[(int)RssType.Bangumi] = request.BgmId;
            if (!string.IsNullOrEmpty(request.VndbId))
                game.Ids[(int)RssType.Vndb] = request.VndbId;
            if (!string.IsNullOrEmpty(request.HikarinagiId))
                game.Ids[(int)RssType.Hikarinagi] = request.HikarinagiId;
            return game;
        }
        catch (Exception e)
        {
            _hostApi.Log(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                $"PotatoDownload: create placeholder skipped for '{request.Title}': {e.Message}");
            return null;
        }
    }

    /// <summary>将解压后的游戏目录关联到游戏库（宿主负责在线刮削并自动匹配占位）。</summary>
    public async Task<Galgame> AddInstallationAsync(InstallRequest request, string gamePath)
    {
        try
        {
            return await _hostApi.AddGameInstallation(gamePath, force: true, requireConfirm: false);
        }
        catch (NameOnlyGameMatchException e)
        {
            // dev 版宿主可以通过 AddSourceAsync/AddGameToSource 手动关联路径，稳定版没有这些 API
            throw new InvalidOperationException(
                $"在线刮削仅按名称匹配（{e.Message}），当前宿主版本不支持手动关联到占位游戏，请在 PotatoVN 中手动处理。", e);
        }
    }

    /// <summary>在库中按外部 ID 查找同一游戏（任一 ID 命中即视为同一游戏）。</summary>
    private Galgame? FindExisting(InstallRequest request)
    {
        var vndbId = NormalizeVndbId(request.VndbId);
        foreach (var game in _hostApi.GetAllGames())
        {
            if (!string.IsNullOrEmpty(request.BgmId) &&
                game.Ids[(int)RssType.Bangumi] == request.BgmId)
                return game;
            if (!string.IsNullOrEmpty(vndbId) &&
                NormalizeVndbId(game.Ids[(int)RssType.Vndb]) == vndbId)
                return game;
            if (!string.IsNullOrEmpty(request.HikarinagiId) &&
                game.Ids[(int)RssType.Hikarinagi] == request.HikarinagiId)
                return game;
        }
        return null;
    }

    /// <summary>VNDB ID 在 Shionlib 侧带 v 前缀（v17），宿主库里可能不带（17），比较前统一去掉。</summary>
    private static string? NormalizeVndbId(string? id) =>
        id is { Length: > 1 } && (id[0] is 'v' or 'V') ? id[1..] : id;
}
