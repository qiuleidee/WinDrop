using System.Text;

namespace WinDrop.Core.Security;

/// <summary>
/// 文件名安全清洗与落盘沙箱防冲突工具
/// 严格防御路径穿越、绝对路径、空字节、NTFS 交替数据流及 Windows 保留设备名
/// </summary>
public static class FileNameSanitizer
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly HashSet<char> InvalidChars = new(
        Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0' }));

    private const int MaxBaseFileNameLength = 180;

    /// <summary>
    /// 清洗外部传入的原始文件名，确保在 Windows 文件系统上 100% 安全可创建
    /// </summary>
    public static string Sanitize(string? rawFileName, string fallbackName = "received_file")
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            return fallbackName;
        }

        // 先尝试对 URL 编码的文件名做解码（兼容 HTTP Header X-File-Name 编码场景）
        string candidate = rawFileName.Trim();
        if (candidate.Contains('%'))
        {
            try
            {
                candidate = Uri.UnescapeDataString(candidate);
            }
            catch
            {
                // 保持原样继续清洗
            }
        }

        // 统一将路径分隔符切分，只保留最后一段文件名，彻底阻断目录穿越（如 ../../evil.exe 或 C:\Windows\...）
        candidate = candidate.Replace('\\', '/');
        int lastSlash = candidate.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            candidate = candidate[(lastSlash + 1)..];
        }

        // 处理 NTFS 交替数据流（如 photo.jpg:$DATA 或 archive.zip:Zone.Identifier）
        // 特征：包含 ":$" 或在已有文件扩展名点号之后跟有冒号流名；而类似 "12:30:00.png" 的时间冒号在点号之前，应保留扩展名
        int adsDollarIdx = candidate.IndexOf(":$", StringComparison.Ordinal);
        if (adsDollarIdx >= 0)
        {
            candidate = candidate[..adsDollarIdx];
        }
        else
        {
            int adsDotIdx = candidate.IndexOf('.');
            int adsColonIdx = candidate.IndexOf(':');
            if (adsDotIdx > 0 && adsColonIdx > adsDotIdx)
            {
                candidate = candidate[..adsColonIdx];
            }
        }

        // 逐字符过滤控制字符与非法字符
        var sb = new StringBuilder(candidate.Length);
        foreach (char ch in candidate)
        {
            if (char.IsControl(ch) || InvalidChars.Contains(ch))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        // Windows 禁止文件名以空格或点结尾，且单独的 "." 或 ".." 非法
        string cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        while (cleaned.Contains("..", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("..", "_", StringComparison.Ordinal);
        }
        cleaned = cleaned.Trim().TrimEnd('.', ' ');

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
        {
            cleaned = fallbackName;
        }

        // 分离主文件名与扩展名，检查 Windows 系统保留设备名（例如 CON、CON.txt、NUL.jpg.png 均非法）
        int firstDot = cleaned.IndexOf('.');
        string stemBeforeFirstDot = firstDot >= 0 ? cleaned[..firstDot] : cleaned;
        string nameWithoutExt = Path.GetFileNameWithoutExtension(cleaned);

        if (ReservedDeviceNames.Contains(stemBeforeFirstDot.Trim()) ||
            ReservedDeviceNames.Contains(nameWithoutExt.Trim()))
        {
            cleaned = "_" + cleaned;
        }

        // 限制超长文件名并保留扩展名
        string ext = Path.GetExtension(cleaned);
        string stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ext.Length > 32)
        {
            ext = ext[..32];
        }
        if (stem.Length > MaxBaseFileNameLength)
        {
            stem = stem[..MaxBaseFileNameLength].TrimEnd('.', ' ');
        }
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = fallbackName;
        }

        return stem + ext;
    }

    /// <summary>
    /// 在目标目录下分配不冲突的唯一文件路径（同名自动追加 (1)、(2)），并确保仍在目标目录沙箱内
    /// </summary>
    public static string ResolveUniquePath(string targetDirectory, string? rawFileName)
    {
        Directory.CreateDirectory(targetDirectory);
        string fullRoot = Path.GetFullPath(targetDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string safeName = Sanitize(rawFileName);
        string stem = Path.GetFileNameWithoutExtension(safeName);
        string ext = Path.GetExtension(safeName);

        string candidatePath = Path.GetFullPath(Path.Combine(fullRoot, safeName));
        if (!candidatePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("检测到非法的目标路径越界访问。");
        }

        int counter = 1;
        while (File.Exists(candidatePath) || Directory.Exists(candidatePath))
        {
            string numberedName = $"{stem} ({counter}){ext}";
            candidatePath = Path.GetFullPath(Path.Combine(fullRoot, numberedName));
            counter++;
        }

        return candidatePath;
    }
}
