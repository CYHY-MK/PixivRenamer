using System.IO;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixivRenamer.Core;

/// <summary>
/// 感知哈希（dHash）：把图片降为 9x8 灰度做相邻像素差分，得到 64 位指纹。
/// 两个用途：
///   1) 近似重复分组  同一张图的不同分辨率/重新保存版本归为一组，一组只查一次识图，省配额；
///   2) 识图结果校验  置信度处于 60~85 的候选必须与来源缩略图对得上才采纳，避免误命名。
/// 复用 WPF 图像管线，不引入额外 NuGet 依赖。无法解码时返回 0。
/// </summary>
public static class PerceptualHash
{
    private const int Width = 9;
    private const int Height = 8;

    public static ulong Compute(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return Compute(bitmap);
        }
        catch { return 0; }
    }

    public static ulong Compute(byte[]? data)
    {
        if (data == null || data.Length == 0) return 0;
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return Compute(bitmap);
        }
        catch { return 0; }
    }

    private static ulong Compute(BitmapSource source)
    {
        if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return 0;
        var scaled = new TransformedBitmap(source,
            new ScaleTransform((double)Width / source.PixelWidth, (double)Height / source.PixelHeight));
        scaled.Freeze();
        var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        gray.Freeze();

        var buffer = new byte[Width * Height];
        gray.CopyPixels(buffer, Width, 0);

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width - 1; x++)
            {
                if (buffer[y * Width + x] > buffer[y * Width + x + 1]) hash |= 1UL << bit;
                bit++;
            }
        return hash == 0 ? 1UL : hash;
    }

    /// <summary>汉明距离：0 完全一致；任一侧不可用返回 int.MaxValue。</summary>
    public static int Distance(ulong a, ulong b)
        => a == 0 || b == 0 ? int.MaxValue : BitOperations.PopCount(a ^ b);

    public static bool IsSame(ulong a, ulong b, int maxDistance)
        => a != 0 && b != 0 && Distance(a, b) <= maxDistance;
}