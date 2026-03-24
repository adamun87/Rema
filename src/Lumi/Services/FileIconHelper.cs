using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Lumi.Services;

/// <summary>
/// Extracts OS file-type icons via the Windows Shell API and converts them
/// to Avalonia bitmaps. For image files, generates a thumbnail from the file
/// content instead. Results are cached by file extension (icons) or full path (thumbnails).
/// </summary>
internal static class FileIconHelper
{
    private static readonly ConcurrentDictionary<string, Avalonia.Media.Imaging.Bitmap?> IconCache = new();
    private static readonly ConcurrentDictionary<string, Avalonia.Media.Imaging.Bitmap?> ThumbnailCache = new();

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tiff", ".tif"
    };

    public static Avalonia.Media.Imaging.Bitmap? GetFileIcon(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";

        // For image files, generate a thumbnail from the file content
        if (ImageExtensions.Contains(ext) && File.Exists(filePath))
            return ThumbnailCache.GetOrAdd(filePath, LoadThumbnail);

        return IconCache.GetOrAdd(ext, GetIconForExtension);
    }

    private static Avalonia.Media.Imaging.Bitmap? LoadThumbnail(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var full = new Avalonia.Media.Imaging.Bitmap(stream);
            // Decode at a small size to save memory (max 32px on longest side)
            var maxDim = Math.Max(full.PixelSize.Width, full.PixelSize.Height);
            if (maxDim <= 32) return full;

            stream.Seek(0, SeekOrigin.Begin);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream,
                Math.Min(32, full.PixelSize.Width));
        }
        catch
        {
            // Fall back to OS icon
            var ext = Path.GetExtension(filePath)?.ToLowerInvariant() ?? "";
            return IconCache.GetOrAdd(ext, GetIconForExtension);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416")]
    private static Avalonia.Media.Imaging.Bitmap? GetIconForExtension(string ext)
    {
        try
        {
            var shfi = new SHFILEINFO();
            var result = SHGetFileInfo(
                $"*{ext}",
                FILE_ATTRIBUTE_NORMAL,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);

            if (result == IntPtr.Zero || shfi.hIcon == IntPtr.Zero)
                return null;

            try
            {
                using var icon = Icon.FromHandle(shfi.hIcon);
                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Seek(0, SeekOrigin.Begin);
                return new Avalonia.Media.Imaging.Bitmap(ms);
            }
            finally
            {
                DestroyIcon(shfi.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    // ── P/Invoke ──

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
