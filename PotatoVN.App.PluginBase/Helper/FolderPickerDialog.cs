using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 文件夹选择对话框（COM IFileOpenDialog + FOS_PICKFOLDERS）。
/// 不用 Windows.Storage.Pickers.FolderPicker：WinRT 选择器在 MSIX 打包宿主里会静默不弹窗
/// （宿主 PotatoVN 的 PvnFsPicker 就是因此手搓 COM 版）；Microsoft.Windows.Storage.Pickers 依赖
/// 宿主 WindowsAppSDK ≥1.8 的运行时，稳定版宿主不一定有。COM 版 Vista 起全平台可用。
/// 必须在 UI 线程（STA）调用；模态由传入的宿主主窗口句柄保证。
/// </summary>
internal static class FolderPickerDialog
{
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const int SIGDN_FILESYSPATH = unchecked((int)0x80058007);
    private const int HResultCancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    /// <summary>弹系统文件夹选择框；用户取消返回 null。</summary>
    public static string? PickFolder(nint owner, string? title = null, string? initialDirectory = null)
    {
        var dialog = (IFileOpenDialog)Activator.CreateInstance(
            Type.GetTypeFromCLSID(FileOpenDialogClsid)!)!;
        try
        {
            dialog.SetOptions(dialog.GetOptions() | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
            if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
            if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            {
                var shellItemGuid = typeof(IShellItem).GUID;
                dialog.SetFolder(SHCreateItemFromParsingName(initialDirectory, nint.Zero, ref shellItemGuid));
            }
            try
            {
                dialog.Show(owner);
            }
            catch (COMException e) when (e.HResult == HResultCancelled)
            {
                return null;
            }
            var result = dialog.GetResult();
            try
            {
                return result.GetDisplayName(SIGDN_FILESYSPATH);
            }
            finally
            {
                Marshal.FinalReleaseComObject(result);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    private static readonly Guid FileOpenDialogClsid = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IShellItem SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, nint pbc,
        [MarshalAs(UnmanagedType.LPStruct)] ref Guid riid);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        nint BindToHandler(nint pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid);
        [return: MarshalAs(UnmanagedType.Interface)] IShellItem GetParent();
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetDisplayName(int sigdnName);
        uint GetAttributes(uint sfgaoMask);
        int Compare([MarshalAs(UnmanagedType.Interface)] IShellItem psi, uint hint);
    }

    // 方法必须按 vtable 顺序完整声明（IModalWindow → IFileDialog → IFileOpenDialog），用不到的也要占位
    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        void Show(nint parent);
        // IFileDialog
        void SetFileTypes(uint cFileTypes, nint rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        uint GetFileTypeIndex();
        uint Advise(nint pfde);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        uint GetOptions();
        void SetDefaultFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        void SetFolder([MarshalAs(UnmanagedType.Interface)] IShellItem psi);
        [return: MarshalAs(UnmanagedType.Interface)] IShellItem GetFolder();
        [return: MarshalAs(UnmanagedType.Interface)] IShellItem GetCurrentSelection();
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [return: MarshalAs(UnmanagedType.Interface)] IShellItem GetResult();
        void AddPlace([MarshalAs(UnmanagedType.Interface)] IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid([MarshalAs(UnmanagedType.LPStruct)] Guid guid);
        void ClearClientData();
        void SetFilter([MarshalAs(UnmanagedType.Interface)] object pFilter);
        // IFileOpenDialog
        [return: MarshalAs(UnmanagedType.Interface)] object GetResults();
        [return: MarshalAs(UnmanagedType.Interface)] object GetSelectedItems();
    }
}
