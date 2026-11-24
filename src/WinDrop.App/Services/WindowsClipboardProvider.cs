using System.Collections.Specialized;
using System.Drawing.Imaging;
using WinDrop.Core.Models;
using WinDrop.Core.Services;

namespace WinDrop.App.Services;

/// <summary>
/// Windows 原生剪贴板实现：完整支持「Ctrl+C 复制文件列表 / 右键分享文件」「截图图片」「纯文本」三合一读取与写入
/// </summary>
public sealed class WindowsClipboardProvider : IClipboardProvider
{
    public Task<ClipboardSnapshot> GetClipboardSnapshotAsync()
    {
        return RunOnStaAsync(() =>
        {
            try
            {
                // 1. 优先检查剪贴板中是否有复制的文件（Ctrl+C 复制的文件或右键「通过 WinDrop 分享」放入的文件）
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var dropList = System.Windows.Clipboard.GetFileDropList();
                    var files = new List<string>();
                    foreach (string? item in dropList)
                    {
                        if (string.IsNullOrWhiteSpace(item))
                            continue;
                        if (File.Exists(item))
                        {
                            files.Add(Path.GetFullPath(item));
                        }
                        else if (Directory.Exists(item))
                        {
                            files.AddRange(Directory.EnumerateFiles(item).Select(Path.GetFullPath));
                        }
                    }

                    if (files.Count > 0)
                    {
                        return new ClipboardSnapshot
                        {
                            Type = ClipboardDataType.File,
                            FilePaths = files
                        };
                    }
                }

                // 2. 检查是否有复制的图片或截图（如 Win+Shift+S）
                if (System.Windows.Forms.Clipboard.ContainsImage())
                {
                    using var img = System.Windows.Forms.Clipboard.GetImage();
                    if (img != null)
                    {
                        using var ms = new MemoryStream();
                        img.Save(ms, ImageFormat.Png);
                        return new ClipboardSnapshot
                        {
                            Type = ClipboardDataType.Image,
                            ImagePngBase64 = Convert.ToBase64String(ms.ToArray())
                        };
                    }
                }

                // 3. 检查是否有复制的文本或网址
                if (System.Windows.Forms.Clipboard.ContainsText())
                {
                    string txt = System.Windows.Forms.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(txt))
                    {
                        return new ClipboardSnapshot
                        {
                            Type = ClipboardDataType.Text,
                            Text = txt
                        };
                    }
                }
            }
            catch { }

            return ClipboardSnapshot.Empty;
        });
    }

    public Task<bool> SetTextAsync(string text)
    {
        return RunOnStaAsync(() =>
        {
            try
            {
                System.Windows.Forms.Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 50);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public Task<bool> SetFilesAsync(IReadOnlyList<string> filePaths)
    {
        return RunOnStaAsync(() =>
        {
            try
            {
                var sc = new StringCollection();
                foreach (string p in filePaths)
                {
                    if (File.Exists(p))
                    {
                        sc.Add(Path.GetFullPath(p));
                    }
                }
                if (sc.Count == 0)
                    return false;

                System.Windows.Clipboard.SetFileDropList(sc);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    public Task<bool> SetImagePngAsync(byte[] pngBytes)
    {
        return RunOnStaAsync(() =>
        {
            try
            {
                using var ms = new MemoryStream(pngBytes);
                using var img = System.Drawing.Image.FromStream(ms);
                System.Windows.Forms.Clipboard.SetImage(img);
                return true;
            }
            catch
            {
                return false;
            }
        });
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> action)
    {
        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;
        }

        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
