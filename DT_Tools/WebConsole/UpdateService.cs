using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using BepInEx;
using DT_Tools.Core;

namespace DT_Tools.WebConsole
{
    /// <summary>
    /// 更新检测与下载服务：请求 GitHub Releases latest，与插件当前版本比较
    /// （当前版本来自 DT_Tools.csproj 的 &lt;Version&gt;，编译期注入 MyPluginInfo.PLUGIN_VERSION）。
    /// 线程模型与 SteamApi 同源：全部工作在 HTTP 线程 / 专用后台线程完成，不触碰 Unity API。
    /// 检测结果缓存 3 分钟（与 WebUI 前端轮询节奏一致，多标签页共享，也压住 GitHub 匿名
    /// 60 次/时的限流）；手动检查绕过缓存。下载在专用线程进行，进度经状态轮询读取。
    /// </summary>
    public static class UpdateService
    {
        private const string ReleaseApiUrl =
            "https://api.github.com/repos/Yume-Hatsuyuki/DT_Tools/releases/latest";

        /// <summary>自动检测的缓存有效期：与前端 3 分钟轮询对齐，失败也推进，避免频繁打 GitHub。</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);
        /// <summary>自动检测失败日志的节流窗口：离线环境下不刷屏 BepInEx。</summary>
        private static readonly TimeSpan FailLogThrottle = TimeSpan.FromMinutes(30);
        private const int RequestTimeoutMs = 8000;
        private const int DownloadChunkBytes = 256 * 1024;

        private static readonly object Gate = new object();

        static UpdateService()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { /* 运行时不支持时忽略 */ }
        }

        // ---- 检测状态（仅持锁读写）----

        private static ReleaseSnapshot _cached;   // 最近一次成功结果（null=从未成功）
        private static DateTime _checkedAt;       // 最近一次尝试时间（成功失败都推进）
        private static string _lastError;         // 最近一次失败原因（成功时清空）
        private static DateTime _lastFailLogAt;

        // ---- 下载状态：不可变快照整体换引用，读侧无锁 ----

        private sealed class DownloadSnapshot
        {
            public readonly bool Active;
            public readonly bool Done;
            public readonly long Received;
            public readonly long Total;
            public readonly string SavePath;
            public readonly string Error;

            public DownloadSnapshot(bool active, bool done, long received, long total, string savePath, string error)
            {
                Active = active; Done = done; Received = received;
                Total = total; SavePath = savePath; Error = error;
            }
        }

        private static volatile DownloadSnapshot _download =
            new DownloadSnapshot(false, false, 0, 0, null, null);

        /// <summary>插件当前版本（csproj &lt;Version&gt; 经 BepInEx.PluginInfoProps 生成）。</summary>
        public static string CurrentVersion => MyPluginInfo.PLUGIN_VERSION;

        // ---- 对外接口（UpdateApi 调用）----

        /// <summary>
        /// 返回状态 JSON（经 Json.To 序列化）。force=false 走缓存；force=true 绕过缓存
        /// 立即请求 GitHub（手动检查）。GitHub 拉取在持锁内同步进行——并发的状态轮询
        /// 会排队等结果，与 SteamApi 的锁内请求策略一致。
        ///
        /// <see cref="WebConsoleOptions.AutoUpdateCheck"/> 关闭时，**自动**路径既不请求 GitHub
        /// 也不提示（省掉每 3 分钟一次的轮询，徽标/气泡不再出现）；手动检查（force=true）不受影响。
        /// </summary>
        public static object GetStatus(bool force)
        {
            if (!force && !WebConsoleOptions.AutoUpdateCheck)
                return BuildStatus(promptAllowed: false);   // 关闭自动提示：不请求、也不报"有新版本"

            lock (Gate)
            {
                bool expired = _cached == null || DateTime.UtcNow - _checkedAt >= CacheTtl;
                if (force || expired)
                {
                    _checkedAt = DateTime.UtcNow;
                    try
                    {
                        _cached = FetchLatest();
                        _lastError = null;
                        if (force)
                            Log.Info("WebConsole", $"手动检查更新完成：最新版 {Describe(_cached)}。");
                    }
                    catch (Exception ex)
                    {
                        _lastError = DescribeError(ex);
                        bool forced = force;
                        if (forced || DateTime.UtcNow - _lastFailLogAt >= FailLogThrottle)
                        {
                            _lastFailLogAt = DateTime.UtcNow;
                            Log.Warn("WebConsole",
                                $"更新检测失败{(forced ? "（手动）" : "")}：{_lastError}" +
                                (_cached != null ? "（沿用上次结果）" : ""));
                        }
                    }
                }
                return BuildStatus(promptAllowed: true);
            }
        }

        /// <summary>
        /// 启动新版 zip 的后台下载。必须先有检测结果（资产地址来自 Release 缓存）；
        /// 已在进行中或没有可用资产时返回失败信封。成功受理立即返回，进度走状态轮询。
        /// </summary>
        public static object StartDownload()
        {
            ReleaseSnapshot release;
            ReleaseAsset asset;
            string target;
            lock (Gate)
            {
                if (_download.Active)
                    return new { ok = false, error = "下载已在进行中" };
                release = _cached;
                if (release == null)
                    return new { ok = false, error = "尚未获取到版本信息，请先检查更新" };
                asset = PickZipAsset(release);
                if (asset == null)
                    return new { ok = false, error = "该版本没有可下载的 zip 安装包" };
                target = ResolveSavePath(asset.Name);
                _download = new DownloadSnapshot(true, false, 0, asset.Size, target, null);
            }

            var job = new DownloadJob { Url = asset.DownloadUrl, SavePath = target, Total = asset.Size };
            var thread = new Thread(RunDownload) { IsBackground = true, Name = "DT_UpdateDownload" };
            thread.Start(job);
            Log.Info("WebConsole", $"开始下载 {Describe(release)} 安装包 → {target}");
            return new { ok = true };
        }

        /// <summary>在资源管理器中定位最近一次下载的 zip（仅 Windows；其余平台明确报错）。</summary>
        public static string RevealDownload()
        {
            var snap = _download;
            if (snap.SavePath == null)
                return "尚未下载过安装包";
            if (!File.Exists(snap.SavePath))
                return "文件已不存在：" + snap.SavePath;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return "仅支持 Windows 资源管理器定位：" + snap.SavePath;
            try
            {
                // explorer /select：打开所在文件夹并选中文件（比只开文件夹更直观）
                var psi = new ProcessStartInfo("explorer.exe", "/select,\"" + snap.SavePath + "\"")
                {
                    UseShellExecute = true,
                };
                Process.Start(psi);
                return null;
            }
            catch (Exception ex)
            {
                return "打开资源管理器失败：" + ex.Message;
            }
        }

        // ---- 状态组装 ----

        /// <summary>
        /// 组装状态 JSON。<paramref name="promptAllowed"/> = false 时强制 <c>hasUpdate=false</c>：
        /// 关了自动提示后，即使缓存里还留着上次手动检查的结果，也不该因此弹出更新徽标。
        /// </summary>
        private static object BuildStatus(bool promptAllowed)
        {
            bool hasUpdate = false;
            if (promptAllowed && _cached != null)
                hasUpdate = IsNewer(_cached.TagName, CurrentVersion);
            return new
            {
                ok = _lastError == null,
                current = CurrentVersion,
                hasUpdate,
                latest = _cached == null ? null : new
                {
                    tag = _cached.TagName,
                    name = _cached.Name,
                    publishedAt = _cached.PublishedAt,
                    url = _cached.HtmlUrl,
                    notes = _cached.Body,
                    assets = _cached.Assets.ConvertAll(a => new
                    {
                        name = a.Name,
                        size = a.Size,
                        downloadUrl = a.DownloadUrl,
                        downloadCount = a.DownloadCount,
                    }),
                },
                lastChecked = _checkedAt == default ? (DateTime?)null : _checkedAt.ToLocalTime(),
                error = _lastError,
                download = new
                {
                    active = _download.Active,
                    done = _download.Done,
                    received = _download.Received,
                    total = _download.Total,
                    savePath = _download.SavePath,
                    error = _download.Error,
                },
            };
        }

        private static string Describe(ReleaseSnapshot r) => string.IsNullOrEmpty(r.TagName) ? "未知版本" : r.TagName;

        // ---- GitHub 拉取与解析 ----

        private static ReleaseSnapshot FetchLatest()
        {
            var request = (HttpWebRequest)WebRequest.Create(ReleaseApiUrl);
            request.Method = "GET";
            // 单次尝试短超时：失败交由下一次轮询/手动重试，不在线程里堆重试
            request.Timeout = RequestTimeoutMs;
            request.ReadWriteTimeout = RequestTimeoutMs;
            request.UserAgent = "DT_Tools-WebConsole";

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
            {
                var release = Json.From<GitHubRelease>(reader.ReadToEnd());
                if (release == null || string.IsNullOrEmpty(release.TagName))
                    throw new Exception("GitHub 返回了无法识别的数据");
                var snapshot = new ReleaseSnapshot
                {
                    TagName = release.TagName,
                    Name = release.Name,
                    PublishedAt = release.PublishedAt,
                    HtmlUrl = release.HtmlUrl,
                    Body = release.Body,
                    Assets = new List<ReleaseAsset>(),
                };
                if (release.Assets != null)
                    foreach (var a in release.Assets)
                        snapshot.Assets.Add(new ReleaseAsset
                        {
                            Name = a.Name,
                            Size = a.Size,
                            DownloadUrl = a.DownloadUrl,
                            DownloadCount = a.DownloadCount,
                        });
                return snapshot;
            }
        }

        /// <summary>WebException 时尽量读出 GitHub 的 {"message": …}，比裸状态码好懂（限流等）。</summary>
        private static string DescribeError(Exception ex)
        {
            if (ex is WebException wex && wex.Response is HttpWebResponse resp)
            {
                try
                {
                    using (var stream = resp.GetResponseStream())
                    using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8))
                    {
                        var body = reader.ReadToEnd();
                        if (Json.TryFrom<GitHubError>(body, out var err) && !string.IsNullOrEmpty(err.Message))
                            return err.Message;
                    }
                }
                catch { /* 退化为基础消息 */ }
            }
            return ex.Message;
        }

        // ---- 版本比较 ----

        /// <summary>比较 "v1.0.8.9" 形式的版本号：逐段数字比较，缺段补 0。tag 更新返回 true。</summary>
        public static bool IsNewer(string tag, string current)
        {
            int[] a = ParseVersion(tag);
            int[] b = ParseVersion(current);
            if (a.Length == 0 || b.Length == 0)
            {
                // 解析不出数字段（异常 tag）：退化为字符串不等即视为有更新
                string na = (tag ?? "").Trim().TrimStart('v', 'V');
                string nb = (current ?? "").Trim();
                return na.Length > 0 && !string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            int len = Math.Max(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        private static int[] ParseVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return Array.Empty<int>();
            string v = version.Trim().TrimStart('v', 'V');
            var list = new List<int>();
            foreach (string part in v.Split('.'))
            {
                // 遇到非数字段（如 1.0.9-beta 的后缀）即截断，前缀仍参与比较
                if (!int.TryParse(part.Trim(), out int n)) break;
                list.Add(n);
            }
            return list.ToArray();
        }

        // ---- 下载 ----

        private sealed class DownloadJob
        {
            public string Url;
            public string SavePath;
            public long Total;
        }

        /// <summary>优先选 zip 资产（发布流水线产物 DT_Tools-vX.Y.Z.zip），没有则不下载。</summary>
        private static ReleaseAsset PickZipAsset(ReleaseSnapshot release)
        {
            foreach (var a in release.Assets)
                if (!string.IsNullOrEmpty(a.Name) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return a;
            return null;
        }

        /// <summary>下载落盘目录：优先用户"下载"文件夹，不存在则退回 BepInEx 根目录。</summary>
        private static string ResolveSavePath(string assetName)
        {
            string fileName = Path.GetFileName(assetName);
            if (string.IsNullOrEmpty(fileName))
                fileName = $"DT_Tools-{CurrentVersion}.zip";
            try
            {
                string downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                if (Directory.Exists(downloads))
                    return Path.Combine(downloads, fileName);
            }
            catch { /* 取不到用户目录时走兜底 */ }
            return Path.Combine(Paths.BepInExRootPath, fileName);
        }

        /// <summary>下载线程主体：流式拷贝，按块发布进度快照；结果（成功/失败）整体换引用。</summary>
        private static void RunDownload(object state)
        {
            var job = (DownloadJob)state;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(job.Url);
                request.Method = "GET";
                request.Timeout = RequestTimeoutMs;
                // 每次读写 30s 无进展即断（下载整体不限时：慢网络也能下完小体积 zip）
                request.ReadWriteTimeout = 30000;
                request.UserAgent = "DT_Tools-WebConsole";

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var source = response.GetResponseStream())
                using (var target = new FileStream(job.SavePath, FileMode.Create, FileAccess.Write))
                {
                    long total = response.ContentLength > 0 ? response.ContentLength : job.Total;
                    var buffer = new byte[DownloadChunkBytes];
                    long received = 0;
                    while (true)
                    {
                        int read = source.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;
                        target.Write(buffer, 0, read);
                        received += read;
                        _download = new DownloadSnapshot(true, false, received, total, job.SavePath, null);
                    }
                    _download = new DownloadSnapshot(false, true, received, total, job.SavePath, null);
                    Log.Info("WebConsole", $"新版安装包下载完成（{received / 1024} KB）→ {job.SavePath}");
                }
            }
            catch (Exception ex)
            {
                string message = DescribeError(ex);
                _download = new DownloadSnapshot(false, true, 0, job.Total, job.SavePath, message);
                Log.Warn("WebConsole", $"新版安装包下载失败：{message}");
                // 半截文件不留给用户误装
                try { File.Delete(job.SavePath); } catch { /* 已不存在则忽略 */ }
            }
        }

        // ---- DTO（GitHub JSON 形状）----

        private sealed class GitHubRelease
        {
            [Newtonsoft.Json.JsonProperty("tag_name")] public string TagName = null;
            [Newtonsoft.Json.JsonProperty("name")] public string Name = null;
            [Newtonsoft.Json.JsonProperty("html_url")] public string HtmlUrl = null;
            [Newtonsoft.Json.JsonProperty("published_at")] public DateTime? PublishedAt = null;
            [Newtonsoft.Json.JsonProperty("body")] public string Body = null;
            [Newtonsoft.Json.JsonProperty("draft")] public bool Draft = false;
            [Newtonsoft.Json.JsonProperty("prerelease")] public bool Prerelease = false;
            [Newtonsoft.Json.JsonProperty("assets")] public GitHubAsset[] Assets = null;
        }

        private sealed class GitHubAsset
        {
            [Newtonsoft.Json.JsonProperty("name")] public string Name = null;
            [Newtonsoft.Json.JsonProperty("size")] public long Size = 0;
            [Newtonsoft.Json.JsonProperty("browser_download_url")] public string DownloadUrl = null;
            [Newtonsoft.Json.JsonProperty("download_count")] public long DownloadCount = 0;
        }

        private sealed class GitHubError
        {
            [Newtonsoft.Json.JsonProperty("message")] public string Message = null;
        }

        // ---- 对内数据快照 ----

        private sealed class ReleaseSnapshot
        {
            public string TagName;
            public string Name;
            public DateTime? PublishedAt;
            public string HtmlUrl;
            public string Body;
            public List<ReleaseAsset> Assets;
        }

        private sealed class ReleaseAsset
        {
            public string Name;
            public long Size;
            public string DownloadUrl;
            public long DownloadCount;
        }
    }
}
