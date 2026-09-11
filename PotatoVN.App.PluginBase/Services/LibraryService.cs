using System;
using System.Threading.Tasks;
using GalgameManager.Enums;
using GalgameManager.Models;
using GalgameManager.Models.Sources;
using GalgameManager.WinApp.Base.Contracts;
using PotatoVN.App.PluginBase.Models;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 刮削入库服务：构造带外部 ID 的 Galgame 占位，解压完成后关联本地路径。
/// </summary>
public class LibraryService
{
    private readonly IPotatoVnApi _hostApi;

    public LibraryService(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
    }

    /// <summary>
    /// 创建带外部 ID（Bangumi/VNDB/Hikarinagi）的虚拟游戏占位，用于后续精确匹配。
    /// 若库中已存在同一游戏则直接返回。
    /// </summary>
    public async Task<Galgame> EnsurePlaceholderAsync(InstallRequest request)
    {
        var uid = BuildUid(request);
        if (_hostApi.GetGameByUid(uid) is { } existing)
            return existing;

        var game = new Galgame(request.Title)
        {
            RssType = RssType.None,
        };
        if (!string.IsNullOrEmpty(request.BgmId))
            game.Ids[(int)RssType.Bangumi] = request.BgmId;
        if (!string.IsNullOrEmpty(request.VndbId))
            game.Ids[(int)RssType.Vndb] = request.VndbId;
        if (!string.IsNullOrEmpty(request.HikarinagiId))
            game.Ids[(int)RssType.Hikarinagi] = request.HikarinagiId;

        await _hostApi.AddVirtualGameAsync(game);
        return game;
    }

    /// <summary>
    /// 将解压后的游戏目录关联到游戏库。
    /// 优先走宿主 AddGameInstallation（会触发在线刮削并自动匹配占位）；
    /// 若在线刮削失败（仅名称匹配），则手动把路径关联到占位游戏。
    /// </summary>
    public async Task<Galgame> AddInstallationAsync(InstallRequest request, string gamePath)
    {
        try
        {
            return await _hostApi.AddGameInstallation(gamePath, force: true, requireConfirm: false);
        }
        catch (NameOnlyGameMatchException)
        {
            // 在线刮削失败：手动关联路径到占位游戏
            var placeholder = await EnsurePlaceholderAsync(request);
            var source = await _hostApi.AddSourceAsync(GalgameSourceType.LocalFolder,
                GetSourceRoot(gamePath), scan: false);
            _hostApi.AddGameToSource(source, placeholder, gamePath);
            return placeholder;
        }
    }

    private static GalgameUid BuildUid(InstallRequest request) => new()
    {
        Name = request.Title,
        BangumiId = request.BgmId,
        VndbId = request.VndbId,
    };

    private static string GetSourceRoot(string gamePath)
    {
        var dir = System.IO.Path.GetDirectoryName(gamePath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(dir) ? gamePath : dir;
    }
}
