using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixivRenamer.Core;

/// <summary>使用 WPF 图像管线把大图压成 JPEG，用于识图网站上传。</summary>
public static class Thumbnailer
{
    public static bool TryCreateJpegThumbnail(string path, int maxDim, int quality,
                                              out byte[] data, out string suggestedName, out string error)
    {
        data = Array.Empty<byte>();
        suggestedName = "";
        error = "";
        try
        {
            if (maxDim < 64) maxDim = 64;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            BitmapSource source = image;
            var maxSide = Math.Max(image.PixelWidth, image.PixelHeight);
            if (maxSide > maxDim)
            {
                var scale = (double)maxDim / maxSide;
                var transformed = new TransformedBitmap(image, new ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            if (ms.Length == 0) { error = "JPEG 编码结果为空"; return false; }
            data = ms.ToArray();
            suggestedName = Path.GetFileNameWithoutExtension(path) + ".jpg";
            return true;
        }
        catch (Exception ex)
        {
            error = "生成缩略图失败：" + ex.Message;
            return false;
        }
    }
}