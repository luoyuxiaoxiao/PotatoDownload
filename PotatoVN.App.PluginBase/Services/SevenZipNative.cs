using System;
using System.Runtime.InteropServices;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 只声明解压需要的 7-Zip COM ABI，不注册 COM、不借用宿主或全局封装的 DLL 状态。
/// GUID/方法顺序来源：7-Zip 26.03 CPP/7zip/{IStream.h,Archive/IArchive.h,IPassword.h}。
/// 全部保留 HRESULT，托管异常由回调上下文保存，不能直接越过原生栈。
/// </summary>
internal static class SevenZipNative
{
    internal static readonly Guid SevenZipClass = new("23170F69-40C1-278A-1000-000110070000");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int CreateObject([In] ref Guid classId, [In] ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IInArchive archive);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeLibrary(IntPtr module);

    [DllImport("ole32.dll")]
    internal static extern int PropVariantClear(ref PropVariant value);

    // PROPVARIANT 的联合体包含指针数组：x86 为 16 字节，x64/arm64 为 24 字节。
    // 不能硬编码为 16 字节，否则 64 位 GetProperty 会越界写托管栈。
    [StructLayout(LayoutKind.Sequential)]
    internal struct VariantArray
    {
        internal uint Count;
        internal IntPtr Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal IntPtr Pointer;
        [FieldOffset(8)] internal ulong Unsigned;
        [FieldOffset(8)] internal uint UInt32;
        [FieldOffset(8)] internal short Boolean;
        [FieldOffset(8)] internal long Signed;
        [FieldOffset(8)] internal VariantArray Array;
    }

    internal enum Property : uint
    {
        Path = 3, IsDirectory = 6, Size = 7, Attributes = 9, ModifiedTime = 12,
        IsAnti = 21, PosixAttributes = 53, SymbolicLink = 54, IsAlternateStream = 63, HardLink = 90,
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600600000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInArchive
    {
        [PreserveSig] int Open([MarshalAs(UnmanagedType.Interface)] IInStream stream,
            [In] ref ulong maxCheckStartPosition, [MarshalAs(UnmanagedType.Interface)] IArchiveOpenCallback callback);
        [PreserveSig] int Close();
        [PreserveSig] int GetNumberOfItems(out uint count);
        [PreserveSig] int GetProperty(uint index, Property property, ref PropVariant value);
        [PreserveSig] int Extract(IntPtr indices, uint count, int testMode,
            [MarshalAs(UnmanagedType.Interface)] IArchiveExtractCallback callback);
        // IInArchive 的剩余属性枚举接口不调用，因此这里只需要 ABI 的前五个方法。
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000300010000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISequentialInStream
    {
        [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000300030000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInStream
    {
        [PreserveSig] int Read(IntPtr data, uint size, IntPtr processedSize);
        [PreserveSig] int Seek(long offset, uint origin, IntPtr newPosition);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000300020000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISequentialOutStream
    {
        [PreserveSig] int Write(IntPtr data, uint size, IntPtr processedSize);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600100000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveOpenCallback
    {
        [PreserveSig] int SetTotal(IntPtr files, IntPtr bytes);
        [PreserveSig] int SetCompleted(IntPtr files, IntPtr bytes);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000600200000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IArchiveExtractCallback
    {
        [PreserveSig] int SetTotal(ulong total);
        [PreserveSig] int SetCompleted(IntPtr completed);
        [PreserveSig] int GetStream(uint index,
            [MarshalAs(UnmanagedType.Interface)] out ISequentialOutStream? stream, int askMode);
        [PreserveSig] int PrepareOperation(int askMode);
        [PreserveSig] int SetOperationResult(int result);
    }

    [ComImport, ComVisible(true), Guid("23170F69-40C1-278A-0000-000500100000")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICryptoGetTextPassword
    {
        [PreserveSig] int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string password);
    }
}
