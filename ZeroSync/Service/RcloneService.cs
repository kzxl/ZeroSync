using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZeroSync.Model;

namespace ZeroSync.Service
{
    public class RcloneService
    {
        private string _rcloneExe;
        private readonly string _appPath;
        private readonly string _logDir;
        private Process _currentProcess;
        private CancellationTokenSource _cts;

        public event Action<string> OutputReceived;
        public event Action<int> ProcessExited;

        public bool IsRunning => _currentProcess != null && !_currentProcess.HasExited;
        public string RcloneExePath => _rcloneExe;

        public RcloneService(string appPath, string customRclonePath = null)
        {
            _appPath = appPath;
            _logDir = Path.Combine(appPath, "logs");
            UpdateRclonePath(customRclonePath);
        }

        public void UpdateRclonePath(string customPath)
        {
            _rcloneExe = !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath)
                ? customPath
                : Path.Combine(_appPath, "rclone.exe");
        }

        public bool RcloneExists() => File.Exists(_rcloneExe);

        public async Task<string> GetVersionAsync()
        {
            if (!RcloneExists()) return "rclone.exe không tìm thấy";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _rcloneExe,
                    Arguments = "version",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return "Không thể chạy rclone";
                    var output = await Task.Run(() => p.StandardOutput.ReadToEnd());
                    p.WaitForExit(5000);
                    // Lấy dòng đầu tiên: "rclone v1.68.2"
                    var line = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    return line.Length > 0 ? line[0].Trim() : "unknown";
                }
            }
            catch (Exception ex) { return "Lỗi: " + ex.Message; }
        }

        public void OpenConfig()
        {
            if (!RcloneExists()) { OutputReceived?.Invoke("✖ rclone.exe không tìm thấy"); return; }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/k \"{_rcloneExe}\" config",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { OutputReceived?.Invoke("✖ Lỗi mở config: " + ex.Message); }
        }

        /// <summary>Chạy `rclone config file` để lấy đường dẫn file cấu hình</summary>
        public async Task<string> GetConfigFilePathAsync()
        {
            if (!RcloneExists()) return null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _rcloneExe,
                    Arguments = "config file",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return null;
                    var output = await Task.Run(() => p.StandardOutput.ReadToEnd());
                    p.WaitForExit(5000);
                    // Output format: "Configuration file is stored at:\nC:\Users\...\rclone.conf"
                    foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var l = line.Trim();
                        if (l.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) || l.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
                            return l;
                    }
                    // fallback: lấy dòng cuối có ký tự path
                    var lines = output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    return lines.Length > 0 ? lines[lines.Length - 1].Trim() : null;
                }
            }
            catch { return null; }
        }

        /// <summary>Trả về danh sách remote đã cài (vd: "ggdrive:", "onedrive:")</summary>
        public async Task<List<string>> ListRemotesAsync()
        {
            var result = new List<string>();
            if (!RcloneExists()) return result;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _rcloneExe,
                    Arguments = "listremotes",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return result;
                    var output = await Task.Run(() => p.StandardOutput.ReadToEnd());
                    p.WaitForExit(5000);
                    foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var remote = line.Trim();
                        if (!string.IsNullOrEmpty(remote))
                            result.Add(remote);
                    }
                }
            }
            catch { }
            return result;
        }

        public async Task<Tuple<bool, string>> TestConnectionAsync(string remotePath)
        {
            if (!RcloneExists())
                return Tuple.Create(false, "rclone.exe không tìm thấy: " + _rcloneExe);

            if (string.IsNullOrWhiteSpace(remotePath))
                return Tuple.Create(false, "Remote path không được trống");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _rcloneExe,
                    Arguments = "lsd " + remotePath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null) return Tuple.Create(false, "Không thể start rclone");

                    var exited = await Task.Run(() => p.WaitForExit(10000));
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        return Tuple.Create(false, "Kết nối timeout (10s)");
                    }

                    var output = p.StandardOutput.ReadToEnd();
                    var error = p.StandardError.ReadToEnd();

                    return p.ExitCode == 0
                        ? Tuple.Create(true, string.IsNullOrWhiteSpace(output) ? "Kết nối thành công!" : output)
                        : Tuple.Create(false, error);
                }
            }
            catch (Exception ex)
            {
                return Tuple.Create(false, ex.Message);
            }
        }

        public async Task RunSyncAsync(SyncProfile profile)
        {
            if (!RcloneExists())
            {
                OutputReceived?.Invoke("✖ rclone.exe không tìm thấy: " + _rcloneExe);
                return;
            }

            _cts = new CancellationTokenSource();

            try
            {
                if (!Directory.Exists(_logDir))
                    Directory.CreateDirectory(_logDir);

                var logFile = GetDailyLogPath();

                var tasks = profile.Tasks ?? new List<BackupTaskItem>();

                // Tương thích ngược: Nếu Tasks rỗng, tự tạo từ BackupPath/RemotePath
                if (tasks.Count == 0 && (!string.IsNullOrEmpty(profile.BackupPath) || !string.IsNullOrEmpty(profile.RemotePath)))
                {
                    tasks.Add(new BackupTaskItem { Source = profile.BackupPath, Destination = profile.RemotePath });
                }

                // Lọc các task hợp lệ (không trống cả nguồn lẫn đích)
                var validTasks = tasks.Where(t => !string.IsNullOrWhiteSpace(t.Source) && !string.IsNullOrWhiteSpace(t.Destination)).ToList();

                if (validTasks.Count == 0)
                {
                    OutputReceived?.Invoke("⚠ Không có task đồng bộ hợp lệ (nguồn và đích không được trống).");
                    ProcessExited?.Invoke(-1);
                    return;
                }

                int overallExitCode = 0;
                int taskIndex = 1;
                int totalTasks = validTasks.Count;

                foreach (var task in validTasks)
                {
                    if (_cts != null && _cts.Token.IsCancellationRequested)
                    {
                        OutputReceived?.Invoke("⚠ Đã hủy bởi người dùng");
                        break;
                    }

                    var source = task.Source.Trim();
                    var destination = task.Destination.Trim();
                    if (destination.Contains(":"))
                    {
                        destination = destination.Replace('\\', '/');
                    }

                    // Không tự động tạo thư mục con (người dùng tự quyết định thư mục đích trên cloud)
                    string finalDest = destination;

                    // Ghi header log cho cặp hiện tại
                    File.AppendAllText(logFile,
                        $"\r\n===== RUN ({taskIndex}/{totalTasks}) {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {source} -> {finalDest} =====\r\n");

                    var args = BuildArguments(profile, task, source, finalDest, logFile);
                    OutputReceived?.Invoke($"▶ [{taskIndex}/{totalTasks}] rclone {args}");

                    var psi = new ProcessStartInfo
                    {
                        FileName = _rcloneExe,
                        Arguments = args,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(_rcloneExe)
                    };

                    _currentProcess = Process.Start(psi);
                    if (_currentProcess == null)
                    {
                        OutputReceived?.Invoke($"✖ Không thể start rclone process cho task {taskIndex}");
                        overallExitCode = -1;
                        taskIndex++;
                        continue;
                    }

                    // Stream output real-time và tự ghi log vào file bằng C#
                    _currentProcess.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            OutputReceived?.Invoke(e.Data);
                            try { File.AppendAllText(logFile, e.Data + "\r\n"); } catch { }
                        }
                    };
                    _currentProcess.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            OutputReceived?.Invoke(e.Data);
                            try { File.AppendAllText(logFile, e.Data + "\r\n"); } catch { }
                        }
                    };
                    _currentProcess.BeginOutputReadLine();
                    _currentProcess.BeginErrorReadLine();

                    await Task.Run(() => _currentProcess.WaitForExit());

                    var exitCode = _currentProcess.ExitCode;
                    if (exitCode != 0)
                    {
                        overallExitCode = exitCode;
                    }

                    OutputReceived?.Invoke(exitCode == 0
                        ? $"✔ Hoàn thành task {taskIndex}/{totalTasks} (exit code 0)"
                        : $"✖ Task {taskIndex}/{totalTasks} kết thúc với exit code {exitCode}");

                    AppendLog($"Source: {source} -> Dest: {finalDest} | Exit code: {exitCode}");
                    taskIndex++;
                }

                ProcessExited?.Invoke(overallExitCode);
                OutputReceived?.Invoke(overallExitCode == 0
                    ? "✔ Đồng bộ toàn bộ hoàn thành thành công!"
                    : "✖ Đồng bộ hoàn thành với một số lỗi (exit code " + overallExitCode + ")");
            }
            catch (OperationCanceledException)
            {
                OutputReceived?.Invoke("⚠ Đã hủy bởi người dùng");
            }
            catch (Exception ex)
            {
                OutputReceived?.Invoke("✖ ERROR: " + ex.Message);
                AppendLog("ERROR: " + ex.Message);
            }
            finally
            {
                _currentProcess = null;
                _cts = null;
            }
        }

        public void Cancel()
        {
            try
            {
                _cts?.Cancel();
                if (_currentProcess != null && !_currentProcess.HasExited)
                    _currentProcess.Kill();
            }
            catch { }
        }

        private string BuildArguments(SyncProfile profile, BackupTaskItem task, string source, string destination, string logFile)
        {
            var sb = new StringBuilder();
            sb.Append(task.SyncMode.ToString().ToLower());
            sb.AppendFormat(" \"{0}\" \"{1}\"", source, destination);

            if (profile.IgnoreExisting) sb.Append(" --ignore-existing");
            if (profile.DryRun) sb.Append(" --dry-run");
            if (profile.Transfers > 0) sb.AppendFormat(" --transfers {0}", profile.Transfers);
            if (profile.Checkers > 0) sb.AppendFormat(" --checkers {0}", profile.Checkers);
            if (!string.IsNullOrWhiteSpace(profile.BandwidthLimit))
                sb.AppendFormat(" --bwlimit {0}", profile.BandwidthLimit);

            // Bỏ --log-file để rclone ghi ra stdout/stderr cho app C# parse tiến độ, tự ghi file log bằng C#
            sb.AppendFormat(" --log-level {0}", profile.LogLevel);
            sb.Append(" --stats 1s --stats-log-level NOTICE --progress");

            // Xử lý bộ lọc tệp tin riêng của Task
            var filter = (task.FileFilter ?? "").Trim();
            if (!string.IsNullOrEmpty(filter) && filter != "*.*" && filter != "*")
            {
                var parts = filter.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(p => p.Trim())
                                  .Where(p => !string.IsNullOrEmpty(p))
                                  .ToList();

                foreach (var part in parts)
                {
                    sb.AppendFormat(" --include \"{0}\"", part);
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.ExtraFlags))
                sb.Append(" " + profile.ExtraFlags.Trim());

            return sb.ToString();
        }

        private void AppendLog(string msg)
        {
            try
            {
                if (!Directory.Exists(_logDir))
                    Directory.CreateDirectory(_logDir);

                File.AppendAllText(
                    Path.Combine(_logDir, "app.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + "\r\n");
            }
            catch { }
        }

        public string GetDailyLogPath()
        {
            if (!Directory.Exists(_logDir))
                Directory.CreateDirectory(_logDir);
            return Path.Combine(_logDir, "rclone_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
        }

        public string LogDirectory => _logDir;
    }
}
