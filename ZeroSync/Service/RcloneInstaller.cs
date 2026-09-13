using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ZeroSync.Service
{
    public class RcloneInstaller
    {
        private const string GithubApiUrl =
            "https://api.github.com/repos/rclone/rclone/releases/latest";

        private readonly HttpClient _http;

        public RcloneInstaller()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.Add("User-Agent", "ZeroSync-App");
            _http.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>Trả về tag version mới nhất từ GitHub, ví dụ "v1.68.2"</summary>
        public async Task<string> GetLatestVersionAsync()
        {
            try
            {
                var json = await _http.GetStringAsync(GithubApiUrl);
                var tag = ExtractJsonString(json, "tag_name");
                return tag ?? "unknown";
            }
            catch (Exception ex)
            {
                return "Lỗi: " + ex.Message;
            }
        }

        /// <summary>
        /// Download rclone-win zip từ GitHub và giải nén rclone.exe vào destDir.
        /// Trả về Tuple(success, message/path).
        /// </summary>
        public async Task<Tuple<bool, string>> DownloadAndInstallAsync(
            string destDir,
            IProgress<Tuple<int, string>> progress,
            CancellationToken ct = default(CancellationToken))
        {
            try
            {
                progress?.Report(Tuple.Create(0, "Đang lấy thông tin release..."));

                var json = await _http.GetStringAsync(GithubApiUrl);
                ct.ThrowIfCancellationRequested();

                var tag = ExtractJsonString(json, "tag_name");
                if (string.IsNullOrEmpty(tag))
                    return Tuple.Create(false, "Không lấy được tag version từ GitHub");

                var assetUrl = ExtractWindowsZipUrl(json);
                if (string.IsNullOrEmpty(assetUrl))
                    return Tuple.Create(false, "Không tìm thấy asset windows-amd64.zip trong release");

                progress?.Report(Tuple.Create(5, "Đang tải " + tag + "..."));

                var response = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;
                var zipPath = Path.Combine(Path.GetTempPath(), "rclone_install.zip");

                using (var fs = File.Create(zipPath))
                using (var stream = await response.Content.ReadAsStreamAsync())
                {
                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read, ct);
                        downloaded += read;
                        if (total > 0)
                        {
                            var pct = (int)(5 + downloaded * 80 / total);
                            progress?.Report(Tuple.Create(pct,
                                "Đang tải... " + (downloaded / 1024) + "KB / " + (total / 1024) + "KB"));
                        }
                    }
                }

                progress?.Report(Tuple.Create(85, "Đang giải nén..."));

                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Name.Equals("rclone.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var dest = Path.Combine(destDir, "rclone.exe");
                            entry.ExtractToFile(dest, overwrite: true);
                            break;
                        }
                    }
                }

                try { File.Delete(zipPath); } catch { }

                var exePath = Path.Combine(destDir, "rclone.exe");
                if (!File.Exists(exePath))
                    return Tuple.Create(false, "Giải nén xong nhưng không tìm thấy rclone.exe");

                progress?.Report(Tuple.Create(100, "✔ Đã cài rclone " + tag + " vào " + destDir));
                return Tuple.Create(true, exePath);
            }
            catch (OperationCanceledException)
            {
                return Tuple.Create(false, "Đã hủy");
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, "Lỗi: " + ex.Message);
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string ExtractJsonString(string json, string key)
        {
            var search = "\"" + key + "\":\"";
            var start = json.IndexOf(search, StringComparison.Ordinal);
            if (start < 0) return null;
            start += search.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }

        private static string ExtractWindowsZipUrl(string json)
        {
            const string marker = "windows-amd64.zip";
            var idx = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var urlKey = "\"browser_download_url\":\"";
            var keyIdx = json.LastIndexOf(urlKey, idx, StringComparison.Ordinal);
            if (keyIdx < 0) return null;

            var start = keyIdx + urlKey.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }
    }
}
