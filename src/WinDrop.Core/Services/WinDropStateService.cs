using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using WinDrop.Core.Models;

namespace WinDrop.Core.Services;

/// <summary>
/// WinDrop 配置持久化与已授权分享文件安全白名单管理服务
/// </summary>
public sealed class WinDropStateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly string _stagingDir;
    private readonly object _configLock = new();

    /// <summary>
    /// 允许通过 /file/{encodedPath} 下载的文件绝对路径白名单（仅限通过电脑复制或右键分享过的文件，杜绝任意路径读取漏洞）
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _allowedSharedFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// v2 短 Ticket 下载映射池（隐藏本机盘符与目录路径，24 小时过期）
    /// </summary>
    private readonly ConcurrentDictionary<string, (string FilePath, DateTimeOffset ExpiresAt)> _downloadTickets = new(StringComparer.OrdinalIgnoreCase);

    public AppConfig Config { get; private set; }
    public string ConfigFilePath => _configPath;
    public string StagingDirectory => _stagingDir;

    public event Action<TransferNotificationEvent>? NotificationRaised;
    public event Action? ConfigChanged;

    public WinDropStateService(string? customDataDirectory = null)
    {
        _dataDir = customDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinDrop");
        Directory.CreateDirectory(_dataDir);

        _configPath = Path.Combine(_dataDir, "config.json");
        _stagingDir = Path.Combine(Path.GetTempPath(), "WinDropStaging");
        Directory.CreateDirectory(_stagingDir);

        Config = LoadOrInitConfig();
    }

    private AppConfig LoadOrInitConfig()
    {
        AppConfig? loaded = null;
        if (File.Exists(_configPath))
        {
            try
            {
                string json = File.ReadAllText(_configPath);
                loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            }
            catch { }
        }

        var cfg = loaded ?? new AppConfig();
        bool dirty = loaded == null;

        if (loaded == null && string.IsNullOrWhiteSpace(cfg.Key))
        {
            // 首次生成简短易填的 6 位访问密钥
            cfg.Key = GenerateDefaultShortKey();
            dirty = true;
        }
        if (cfg.Port is < 1024 or > 65535)
        {
            cfg.Port = 58446;
            dirty = true;
        }
        if (string.IsNullOrWhiteSpace(cfg.SaveDirectory))
        {
            cfg.SaveDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "WinDrop");
            dirty = true;
        }

        Directory.CreateDirectory(cfg.SaveDirectory);
        if (dirty)
        {
            SaveConfigInternal(cfg);
        }
        return cfg;
    }

    public void UpdateConfig(Action<AppConfig> mutator)
    {
        lock (_configLock)
        {
            mutator(Config);
            Directory.CreateDirectory(Config.SaveDirectory);
            SaveConfigInternal(Config);
        }
        ConfigChanged?.Invoke();
    }

    private void SaveConfigInternal(AppConfig cfg)
    {
        string tmp = _configPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOptions));
        File.Move(tmp, _configPath, overwrite: true);
    }

    /// <summary>
    /// 登记通过「电脑复制（Ctrl+C）」或「右键菜单分享」授权给手机拉取的文件路径（有效期 24 小时）
    /// </summary>
    public void AuthorizeFilesForDownload(IEnumerable<string> filePaths)
    {
        CleanupExpiredAuthorizedFiles();
        var exp = DateTimeOffset.UtcNow.AddHours(24);
        foreach (string raw in filePaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            try
            {
                string full = Path.GetFullPath(raw);
                if (File.Exists(full))
                {
                    _allowedSharedFiles[full] = exp;
                }
            }
            catch { }
        }
    }

    public bool IsFileAuthorizedForDownload(string filePath, out string normalizedFullPath)
    {
        normalizedFullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            CleanupExpiredAuthorizedFiles();
            normalizedFullPath = Path.GetFullPath(filePath);
            return File.Exists(normalizedFullPath) &&
                   _allowedSharedFiles.TryGetValue(normalizedFullPath, out var exp) &&
                   exp > DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
    }

    private void CleanupExpiredAuthorizedFiles()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _allowedSharedFiles)
        {
            if (kv.Value <= now)
            {
                _allowedSharedFiles.TryRemove(kv.Key, out _);
            }
        }
        foreach (var kv in _downloadTickets)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                _downloadTickets.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    /// 为已授权文件签发 16 位密码学随机短 Ticket（供 /v2/dl/{ticket} 直接拉取）
    /// </summary>
    public string IssueDownloadTicket(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("待签发下载凭证的文件不存在。", full);
        }

        AuthorizeFilesForDownload(new[] { full });

        Span<byte> rnd = stackalloc byte[8];
        RandomNumberGenerator.Fill(rnd);
        string ticket = Convert.ToHexString(rnd).ToLowerInvariant();
        _downloadTickets[ticket] = (full, DateTimeOffset.UtcNow.AddHours(24));
        return ticket;
    }

    /// <summary>
    /// 校验短 Ticket 并解析出实际本地文件路径
    /// </summary>
    public bool TryResolveDownloadTicket(string? ticket, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(ticket))
            return false;

        CleanupExpiredAuthorizedFiles();
        string clean = ticket.Trim();
        if (_downloadTickets.TryGetValue(clean, out var entry) &&
            entry.ExpiresAt > DateTimeOffset.UtcNow &&
            File.Exists(entry.FilePath))
        {
            fullPath = entry.FilePath;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 按文件扩展名推断 v2 智能归档类别（image / video / file）
    /// </summary>
    public static string ClassifyFileCategory(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic" or ".heif" or ".bmp" or ".tiff" or ".avif"
                => V2ContentCategory.Image,
            ".mp4" or ".mov" or ".m4v" or ".mkv" or ".avi" or ".webm"
                => V2ContentCategory.Video,
            _ => V2ContentCategory.File
        };
    }

    public void Notify(string title, string message, string? savedFilePath = null, bool isError = false)
    {
        NotificationRaised?.Invoke(new TransferNotificationEvent
        {
            Title = title,
            Message = message,
            SavedFilePath = savedFilePath,
            IsError = isError
        });
    }

    public static string GenerateDefaultShortKey()
    {
        const string chars = "23456789abcdefghjkmnpqrstuvwxyz";
        Span<byte> rnd = stackalloc byte[6];
        RandomNumberGenerator.Fill(rnd);
        char[] res = new char[6];
        for (int i = 0; i < 6; i++)
        {
            res[i] = chars[rnd[i] % chars.Length];
        }
        return new string(res);
    }

    public static string GuessMimeType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }
}
