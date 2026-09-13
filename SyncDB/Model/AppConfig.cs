using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SyncDB.Model
{
    public class AppConfig
    {
        public List<SyncProfile> Profiles { get; set; } = new List<SyncProfile> { new SyncProfile() };
        public int ActiveProfileIndex { get; set; }

        // Rclone
        public string RclonePath { get; set; } = ""; // trống = dùng rclone.exe cạnh app

        // App behavior
        public bool MinimizeToTray { get; set; }
        public bool StartMinimized { get; set; }
        public bool AutoStartWatch { get; set; }
        public bool RunOnStartup { get; set; }
    }

    public class SyncProfile
    {
        public string Name { get; set; } = "Default";
        public string BackupPath { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public List<BackupTaskItem> Tasks { get; set; } = new List<BackupTaskItem>();

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RcloneSyncMode SyncMode { get; set; } = RcloneSyncMode.Copy;

        // Performance
        public int Transfers { get; set; } = 4;
        public int Checkers { get; set; } = 8;
        public string BandwidthLimit { get; set; } = "";

        // Behavior
        public bool IgnoreExisting { get; set; }
        public bool DryRun { get; set; }
        public string LogLevel { get; set; } = "INFO";
        public string ExtraFlags { get; set; } = "";

        // Watch
        public bool WatchEnabled { get; set; }
        public int DebounceSec { get; set; } = 15;
        public string FileFilter { get; set; } = "*.bak;*.txt";

        // File lock check
        public int FileLockTimeoutSec { get; set; } = 60;  // chờ tối đa bao lâu
        public int FileLockRetryMs { get; set; } = 500;    // thử lại mỗi bao ms
        public bool SyncOnStartWatch { get; set; } = true; // đồng bộ tất cả file khi bắt đầu watch

        // Schedule
        public bool ScheduleEnabled { get; set; }
        public int ScheduleIntervalMin { get; set; } = 60;
    }

    public class BackupTaskItem : INotifyPropertyChanged
    {
        private string _source = "";
        public string Source
        {
            get => _source;
            set
            {
                if (_source != value)
                {
                    _source = value;
                    OnPropertyChanged(nameof(Source));
                }
            }
        }

        private string _destination = "";
        public string Destination
        {
            get => _destination;
            set
            {
                if (_destination != value)
                {
                    _destination = value;
                    OnPropertyChanged(nameof(Destination));
                }
            }
        }

        private RcloneSyncMode _syncMode = RcloneSyncMode.Copy;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public RcloneSyncMode SyncMode
        {
            get => _syncMode;
            set
            {
                if (_syncMode != value)
                {
                    _syncMode = value;
                    OnPropertyChanged(nameof(SyncMode));
                }
            }
        }

        private string _fileFilter = "*.*";
        public string FileFilter
        {
            get => _fileFilter;
            set
            {
                if (_fileFilter != value)
                {
                    _fileFilter = value;
                    OnPropertyChanged(nameof(FileFilter));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public enum RcloneSyncMode
    {
        Copy,
        Sync,
        Move
    }
}
