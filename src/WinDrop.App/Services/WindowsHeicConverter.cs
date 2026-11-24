using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinDrop.Core.Security;
using WinDrop.Core.Services;

namespace WinDrop.App.Services;

/// <summary>
/// 调用 Windows 原生 WIC / WinRT 图像解码器将 iPhone HEIC 照片无损/高画质转换为 JPG
/// </summary>
public sealed class WindowsHeicConverter : IHeicConverter
{
    public async Task<string?> TryConvertHeicToJpgAsync(string heicFilePath, bool keepOriginal)
    {
        try
        {
            if (!File.Exists(heicFilePath))
                return null;

            string dir = Path.GetDirectoryName(heicFilePath) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(heicFilePath);
            string targetJpgPath = FileNameSanitizer.ResolveUniquePath(dir, $"{stem}.jpg");

            StorageFile inputFile = await StorageFile.GetFileFromPathAsync(heicFilePath);
            using IRandomAccessStream inputStream = await inputFile.OpenAsync(FileAccessMode.Read);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(inputStream);
            using SoftwareBitmap softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            await using var fs = new FileStream(targetJpgPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using IRandomAccessStream outputStream = fs.AsRandomAccessStream();

            var propertySet = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(0.92d, Windows.Foundation.PropertyType.Single) }
            };

            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream, propertySet);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            if (!keepOriginal)
            {
                try { File.Delete(heicFilePath); } catch { }
            }

            return targetJpgPath;
        }
        catch
        {
            // 若系统未安装微软 HEIF 图像扩展，优雅保留原始 HEIC 文件，绝不抛异常中断传输
            return null;
        }
    }
}
