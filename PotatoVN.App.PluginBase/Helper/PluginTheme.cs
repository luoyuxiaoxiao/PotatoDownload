using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 宿主主题资源的安全查找。插件UI使用纯C#构建（插件XAML依赖宿主v1.10.1+的
/// PluginXamlHost机制，旧版本宿主会直接XamlParseException），C#里无法使用
/// ThemeResource标记扩展，因此统一从这里查资源；查不到时返回null/回退值，绝不抛异常。
/// </summary>
internal static class PluginTheme
{
    public static Brush? GetBrush(string key) => TryGet(key) as Brush;

    public static Thickness GetThickness(string key, Thickness fallback) =>
        TryGet(key) is Thickness t ? t : fallback;

    private static object? TryGet(string key)
    {
        try
        {
            if (Application.Current?.Resources is not { } resources) return null;
            // TryGetValue自身会遍历MergedDictionaries，但不会进ThemeDictionaries
            if (resources.TryGetValue(key, out var value)) return value;
            return FindInThemeDictionaries(resources, key, 0);
        }
        catch
        {
            return null;
        }
    }

    private static object? FindInThemeDictionaries(ResourceDictionary dictionary, string key, int depth)
    {
        if (depth > 8) return null; //防御字典循环引用
        foreach (var pair in dictionary.ThemeDictionaries)
        {
            if (pair.Value is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue(key, out var value))
                return value;
        }
        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindInThemeDictionaries(merged, key, depth + 1) is { } found) return found;
        }
        return null;
    }
}
