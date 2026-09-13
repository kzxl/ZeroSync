using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Timers;

namespace ZeroSync.Service
{
    public class WatcherTarget
    {
        public string Path { get; set; } = "";
        public string Filter { get; set; } = "*.*";
    }

    public class WatcherService : IDisposable
    {
        private List<FileSystemWatcher> _watchers;
        private System.Timers.Timer _debounceTimer;
        private string _lastDetectedFile;
        private DateTime _lastDetectTime;

        private int _lockTimeoutSec = 60;
        private int _lockRetryMs = 500;

        public event Action<string> FileDetected;
        public event Action DebounceTrigger;
        public event Action<string> LogMessage; // để gửi timing log về UI

        public bool IsWatching => _watchers != null && _watchers.Count > 0;

        public void Start(List<WatcherTarget> targets, int debounceSec,
                          int lockTimeoutSec = 60, int lockRetryMs = 500)
        {
            Stop();

            if (targets == null || targets.Count == 0) return;

            _lockTimeoutSec = lockTimeoutSec;
            _lockRetryMs = lockRetryMs;

            _watchers = new List<FileSystemWatcher>();

            foreach (var target in targets)
            {
                var p = target.Path;
                if (!Directory.Exists(p)) continue;

                var w = new FileSystemWatcher(p)
                {
                    IncludeSubdirectories = true,
                    Filter = "*.*",
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.DirectoryName
                };

                w.Created += (s, e) => OnChanged(target, e);
                w.Changed += (s, e) => OnChanged(target, e);
                w.EnableRaisingEvents = true;

                _watchers.Add(w);
            }

            _debounceTimer = new System.Timers.Timer(debounceSec * 1000);
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += OnDebounceElapsed;
        }

        public void Stop()
        {
            if (_debounceTimer != null)
            {
                _debounceTimer.Stop();
                _debounceTimer.Elapsed -= OnDebounceElapsed;
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }

            if (_watchers != null)
            {
                foreach (var w in _watchers)
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                }
                _watchers.Clear();
                _watchers = null;
            }

            _lastDetectedFile = null;
        }

        private void OnChanged(WatcherTarget target, FileSystemEventArgs e)
        {
            var ext = Path.GetExtension(e.FullPath).ToLower();
            
            // Tự lọc theo filter riêng của target
            var filter = (target.Filter ?? "").Trim();
            if (!string.IsNullOrEmpty(filter) && filter != "*.*" && filter != "*")
            {
                var parts = filter.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                  .Select(f => f.Trim().TrimStart('*').ToLower())
                                  .ToList();

                if (!parts.Contains(ext))
                    return; // File thay đổi không khớp filter của task này
            }

            var detectTime = DateTime.Now;
            LogMessage?.Invoke($"📁 [{detectTime:HH:mm:ss}] Phát hiện: {Path.GetFileName(e.FullPath)} — kiểm tra file lock...");

            // Chờ file không còn bị lock (configurable timeout)
            bool ready = WaitUntilFileReady(e.FullPath, detectTime);
            if (!ready)
            {
                LogMessage?.Invoke($"⚠ [{DateTime.Now:HH:mm:ss}] Bỏ qua — file vẫn bị lock sau {_lockTimeoutSec}s: {Path.GetFileName(e.FullPath)}");
                return;
            }

            var readyTime = DateTime.Now;
            var waitSec = (readyTime - detectTime).TotalSeconds;
            LogMessage?.Invoke($"✔ [{readyTime:HH:mm:ss}] File sẵn sàng (chờ {waitSec:F1}s) — reset debounce timer {_debounceTimer?.Interval / 1000}s");

            _lastDetectedFile = e.FullPath;
            _lastDetectTime = detectTime;
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }

        private void OnDebounceElapsed(object sender, ElapsedEventArgs e)
        {
            var triggerTime = DateTime.Now;
            if (_lastDetectedFile != null)
            {
                FileDetected?.Invoke(_lastDetectedFile);
                var totalSec = (triggerTime - _lastDetectTime).TotalSeconds;
                LogMessage?.Invoke($"⚡ [{triggerTime:HH:mm:ss}] Trigger sync — tổng thời gian từ detect: {totalSec:F1}s");
            }

            _lastDetectedFile = null;
            DebounceTrigger?.Invoke();
        }

        /// <summary>
        /// Chờ cho đến khi file không còn bị lock hoặc timeout.
        /// Retry mỗi lockRetryMs, tối đa lockTimeoutSec giây.
        /// </summary>
        private bool WaitUntilFileReady(string path, DateTime startTime)
        {
            while (true)
            {
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                        return true;
                }
                catch
                {
                    if ((DateTime.Now - startTime).TotalSeconds >= _lockTimeoutSec)
                        return false;

                    Thread.Sleep(_lockRetryMs);
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
