using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SyncDB.Core;
using SyncDB.Model;
using SyncDB.Service;

namespace SyncDB.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private readonly ConfigService _configService;
        private readonly RcloneService _rcloneService;
        private readonly WatcherService _watcherService;
        private readonly RcloneInstaller _installer;
        private AppConfig _appConfig;
        
        private readonly ConcurrentQueue<string> _pendingLogs = new ConcurrentQueue<string>();
        private readonly DispatcherTimer _logFlushTimer;
        private static readonly char[] EmptyCharArray = new char[0];

        // ═══ Profile ═══
        private ObservableCollection<SyncProfile> _profiles;
        public ObservableCollection<SyncProfile> Profiles
        {
            get { return _profiles; }
            set { SetProperty(ref _profiles, value); }
        }

        private SyncProfile _selectedProfile;
        public SyncProfile SelectedProfile
        {
            get { return _selectedProfile; }
            set
            {
                if (SetProperty(ref _selectedProfile, value))
                    RefreshProfileBindings();
            }
        }

        // ═══ Sync Tab ═══
        private string _backupPath = "";
        public string BackupPath
        {
            get { return _backupPath; }
            set
            {
                if (SetProperty(ref _backupPath, value))
                {
                    if (SelectedProfile != null) SelectedProfile.BackupPath = value;
                    ValidateProfile();
                }
            }
        }

        private string _remotePath = "";
        public string RemotePath
        {
            get { return _remotePath; }
            set
            {
                if (SetProperty(ref _remotePath, value))
                {
                    if (SelectedProfile != null) SelectedProfile.RemotePath = value;
                    ValidateProfile();
                }
            }
        }

        private int _selectedSyncMode;
        public int SelectedSyncMode
        {
            get { return _selectedSyncMode; }
            set
            {
                if (SetProperty(ref _selectedSyncMode, value) && SelectedProfile != null)
                    SelectedProfile.SyncMode = (RcloneSyncMode)value;
            }
        }

        // ═══ Options Tab ═══
        private bool _ignoreExisting;
        public bool IgnoreExisting
        {
            get { return _ignoreExisting; }
            set { if (SetProperty(ref _ignoreExisting, value) && SelectedProfile != null) SelectedProfile.IgnoreExisting = value; }
        }

        private bool _dryRun;
        public bool DryRun
        {
            get { return _dryRun; }
            set { if (SetProperty(ref _dryRun, value) && SelectedProfile != null) SelectedProfile.DryRun = value; }
        }

        private int _transfers = 4;
        public int Transfers
        {
            get { return _transfers; }
            set { if (SetProperty(ref _transfers, value) && SelectedProfile != null) SelectedProfile.Transfers = value; }
        }

        private int _checkers = 8;
        public int Checkers
        {
            get { return _checkers; }
            set { if (SetProperty(ref _checkers, value) && SelectedProfile != null) SelectedProfile.Checkers = value; }
        }

        private string _bandwidthLimit = "";
        public string BandwidthLimit
        {
            get { return _bandwidthLimit; }
            set { if (SetProperty(ref _bandwidthLimit, value) && SelectedProfile != null) SelectedProfile.BandwidthLimit = value; }
        }

        private int _selectedLogLevel = 1;
        public int SelectedLogLevel
        {
            get { return _selectedLogLevel; }
            set
            {
                if (SetProperty(ref _selectedLogLevel, value) && SelectedProfile != null)
                    SelectedProfile.LogLevel = LogLevels[value];
            }
        }

        private string _extraFlags = "";
        public string ExtraFlags
        {
            get { return _extraFlags; }
            set { if (SetProperty(ref _extraFlags, value) && SelectedProfile != null) SelectedProfile.ExtraFlags = value; }
        }

        // ═══ Watch ═══
        private bool _watchEnabled;
        public bool WatchEnabled
        {
            get { return _watchEnabled; }
            set { if (SetProperty(ref _watchEnabled, value) && SelectedProfile != null) SelectedProfile.WatchEnabled = value; }
        }

        private int _debounceSec = 15;
        public int DebounceSec
        {
            get { return _debounceSec; }
            set { if (SetProperty(ref _debounceSec, value) && SelectedProfile != null) SelectedProfile.DebounceSec = value; }
        }

        private string _fileFilter = "*.bak;*.txt";
        public string FileFilter
        {
            get { return _fileFilter; }
            set
            {
                if (SetProperty(ref _fileFilter, value))
                {
                    if (SelectedProfile != null) SelectedProfile.FileFilter = value;
                    UpdateCheckboxesFromFileFilter();
                    ValidateProfile();
                }
            }
        }

        private int _fileLockTimeoutSec = 60;
        public int FileLockTimeoutSec
        {
            get { return _fileLockTimeoutSec; }
            set { if (SetProperty(ref _fileLockTimeoutSec, value) && SelectedProfile != null) SelectedProfile.FileLockTimeoutSec = value; }
        }

        private int _fileLockRetryMs = 500;
        public int FileLockRetryMs
        {
            get { return _fileLockRetryMs; }
            set { if (SetProperty(ref _fileLockRetryMs, value) && SelectedProfile != null) SelectedProfile.FileLockRetryMs = value; }
        }

        private bool _syncOnStartWatch = true;
        public bool SyncOnStartWatch
        {
            get { return _syncOnStartWatch; }
            set { if (SetProperty(ref _syncOnStartWatch, value) && SelectedProfile != null) SelectedProfile.SyncOnStartWatch = value; }
        }

        // ═══ Profile Validation & Error ═══
        private string _profileError = "";
        public string ProfileError
        {
            get => _profileError;
            set => SetProperty(ref _profileError, value);
        }

        private bool _hasProfileError = false;
        public bool HasProfileError
        {
            get => _hasProfileError;
            set => SetProperty(ref _hasProfileError, value);
        }

        public bool CanChangeProfile => !IsRunning && !IsWatching;

        // ═══ Rename Profile ═══
        private bool _isRenamingProfile = false;
        public bool IsRenamingProfile
        {
            get => _isRenamingProfile;
            set => SetProperty(ref _isRenamingProfile, value);
        }

        private string _newProfileName = "";
        public string NewProfileName
        {
            get => _newProfileName;
            set => SetProperty(ref _newProfileName, value);
        }

        // ═══ Backup Tasks list ═══
        private ObservableCollection<BackupTaskItem> _backupTasks = new ObservableCollection<BackupTaskItem>();
        public ObservableCollection<BackupTaskItem> BackupTasks
        {
            get => _backupTasks;
            set => SetProperty(ref _backupTasks, value);
        }

        // ═══ Real-time Sync Queue ═══
        public ObservableCollection<QueueItem> SyncQueue { get; } = new ObservableCollection<QueueItem>();

        // ═══ File Extensions filter options ═══
        public ObservableCollection<FileExtensionOption> FileExtensionOptions { get; set; }
        private bool _isUpdatingFileFilter = false;

        // ═══ Settings Tab — Rclone ═══
        private string _rclonePath = "";
        public string RclonePath
        {
            get { return _rclonePath; }
            set { SetProperty(ref _rclonePath, value); }
        }

        private string _rcloneCurrentVersion = "chưa kiểm tra";
        public string RcloneCurrentVersion
        {
            get { return _rcloneCurrentVersion; }
            set { SetProperty(ref _rcloneCurrentVersion, value); }
        }

        private string _rcloneLatestVersion = "chưa kiểm tra";
        public string RcloneLatestVersion
        {
            get { return _rcloneLatestVersion; }
            set { SetProperty(ref _rcloneLatestVersion, value); }
        }

        private bool _isInstalling;
        public bool IsInstalling
        {
            get { return _isInstalling; }
            set { SetProperty(ref _isInstalling, value); }
        }

        private int _installProgress;
        public int InstallProgress
        {
            get { return _installProgress; }
            set { SetProperty(ref _installProgress, value); }
        }

        private string _installStatusText = "";
        public string InstallStatusText
        {
            get { return _installStatusText; }
            set { SetProperty(ref _installStatusText, value); }
        }

        // ═══ Settings Tab — App ═══
        private bool _minimizeToTray;
        public bool MinimizeToTray
        {
            get { return _minimizeToTray; }
            set { SetProperty(ref _minimizeToTray, value); }
        }

        private bool _startMinimized;
        public bool StartMinimized
        {
            get { return _startMinimized; }
            set { SetProperty(ref _startMinimized, value); }
        }

        private bool _runOnStartup;
        public bool RunOnStartup
        {
            get { return _runOnStartup; }
            set { SetProperty(ref _runOnStartup, value); }
        }

        // ═══ Status ═══
        private bool _isRunning;
        public bool IsRunning
        {
            get { return _isRunning; }
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    OnPropertyChanged(nameof(CanChangeProfile));
                    OnPropertyChanged(nameof(CanEditTasks));
                }
            }
        }

        private bool _isWatching;
        public bool IsWatching
        {
            get { return _isWatching; }
            set
            {
                if (SetProperty(ref _isWatching, value))
                {
                    OnPropertyChanged(nameof(CanChangeProfile));
                    OnPropertyChanged(nameof(CanEditTasks));
                }
            }
        }

        public bool CanEditTasks => !IsRunning && !IsWatching;

        private string _statusText = "Sẵn sàng";
        public string StatusText
        {
            get { return _statusText; }
            set { SetProperty(ref _statusText, value); }
        }

        private string _lastSyncTime = "—";
        public string LastSyncTime
        {
            get { return _lastSyncTime; }
            set { SetProperty(ref _lastSyncTime, value); }
        }

        // ═══ Log ═══
        private ObservableCollection<string> _logEntries = new ObservableCollection<string>();
        public ObservableCollection<string> LogEntries
        {
            get { return _logEntries; }
            set { SetProperty(ref _logEntries, value); }
        }

        // ═══ Lookup data ═══
        public List<string> SyncModes { get; } = new List<string> { "Copy", "Sync", "Move" };
        public List<string> LogLevels { get; } = new List<string> { "DEBUG", "INFO", "NOTICE", "ERROR" };

        // ═══ Commands ═══
        public ICommand BrowseCommand { get; }
        public ICommand TestConnectionCommand { get; }
        public ICommand RunSyncCommand { get; }
        public ICommand CancelSyncCommand { get; }
        public ICommand StartWatchCommand { get; }
        public ICommand StopWatchCommand { get; }
        public ICommand AddProfileCommand { get; }
        public ICommand DeleteProfileCommand { get; }
        public ICommand ClearLogCommand { get; }
        public ICommand SaveConfigCommand { get; }
        public ICommand BrowseRcloneCommand { get; }
        public ICommand CheckVersionCommand { get; }
        public ICommand InstallRcloneCommand { get; }
        public ICommand OpenRcloneConfigCommand { get; }
        public ICommand OpenRcloneConfigDirCommand { get; }
        public ICommand SaveAppSettingsCommand { get; }
        public ICommand LoadRemotesCommand { get; }
        public ICommand LoadBackupLogCommand { get; }
        public ICommand AddTaskCommand { get; }
        public ICommand DeleteTaskCommand { get; }
        public ICommand AddTaskWithFolderCommand { get; }
        public ICommand ScanExtensionsCommand { get; }
        public ICommand ClearQueueCommand { get; }
        
        public ICommand StartRenameProfileCommand { get; }
        public ICommand SaveRenameProfileCommand { get; }
        public ICommand CancelRenameProfileCommand { get; }

        // ═══ Rclone Remotes ═══
        private ObservableCollection<string> _rcloneRemotes = new ObservableCollection<string>();
        public ObservableCollection<string> RcloneRemotes
        {
            get { return _rcloneRemotes; }
            set { SetProperty(ref _rcloneRemotes, value); }
        }

        private string _selectedRemote;
        public string SelectedRemote
        {
            get { return _selectedRemote; }
            set
            {
                if (SetProperty(ref _selectedRemote, value) && !string.IsNullOrEmpty(value))
                    RemotePath = value;
            }
        }

        // ═══ Backup Log Viewer ═══
        private string _backupLogContent = "(Nhấn Làm mới để tải log)";
        public string BackupLogContent
        {
            get { return _backupLogContent; }
            set { SetProperty(ref _backupLogContent, value); }
        }

        private bool _isLoadingLog;
        public bool IsLoadingLog
        {
            get { return _isLoadingLog; }
            set { SetProperty(ref _isLoadingLog, value); }
        }

        private ObservableCollection<string> _logFiles = new ObservableCollection<string>();
        public ObservableCollection<string> LogFiles
        {
            get { return _logFiles; }
            set { SetProperty(ref _logFiles, value); }
        }

        private string _selectedLogFile;
        public string SelectedLogFile
        {
            get { return _selectedLogFile; }
            set { SetProperty(ref _selectedLogFile, value); }
        }

        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource _installCts;

        public MainViewModel()
        {
            _dispatcher = Application.Current.Dispatcher;

            var appPath = AppDomain.CurrentDomain.BaseDirectory;
            _configService = new ConfigService(appPath);
            _installer = new RcloneInstaller();

            _appConfig = _configService.Load();
            _rcloneService = new RcloneService(appPath, _appConfig.RclonePath);
            _watcherService = new WatcherService();

            FileExtensionOptions = new ObservableCollection<FileExtensionOption>
            {
                new FileExtensionOption("*.*", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".bak", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".sql", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".zip", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".rar", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".7z", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".mdf", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".ldf", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".txt", UpdateFileFilterFromCheckboxes),
                new FileExtensionOption(".log", UpdateFileFilterFromCheckboxes)
            };

            _rcloneService.OutputReceived += OnOutputReceived;
            _rcloneService.ProcessExited += code =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    IsRunning = false;
                    LastSyncTime = DateTime.Now.ToString("HH:mm:ss");
                    StatusText = code == 0 ? "✔ Đồng bộ thành công" : "✖ Lỗi (code " + code + ")";
                });
            };

            _watcherService.FileDetected += file =>
            {
                _dispatcher.InvokeAsync(() =>
                    AddLog("📁 Phát hiện file: " + System.IO.Path.GetFileName(file)));
            };
            _watcherService.LogMessage += msg =>
            {
                _dispatcher.InvokeAsync(() => AddLog(msg));
            };
            _watcherService.DebounceTrigger += () =>
            {
                _dispatcher.InvokeAsync(async () =>
                {
                    if (!IsRunning && SelectedProfile != null)
                    {
                        AddLog("🔄 Auto sync triggered...");
                        await DoRunSyncAsync();
                    }
                });
            };

            BrowseCommand = new RelayCommand(BrowseFolder);
            TestConnectionCommand = new RelayCommand(async () => await TestConnectionAsync(), () => !IsRunning);
            RunSyncCommand = new RelayCommand(async () => await DoRunSyncAsync(), () => !IsRunning);
            CancelSyncCommand = new RelayCommand(CancelSync, () => IsRunning);
            StartWatchCommand = new RelayCommand(async () => await StartWatch(), () => !IsWatching);
            StopWatchCommand = new RelayCommand(StopWatch, () => IsWatching);
            AddProfileCommand = new RelayCommand(AddProfile);
            DeleteProfileCommand = new RelayCommand(DeleteProfile, () => Profiles != null && Profiles.Count > 1);
            ClearLogCommand = new RelayCommand(() => LogEntries.Clear());
            SaveConfigCommand = new RelayCommand(SaveConfig);
            BrowseRcloneCommand = new RelayCommand(BrowseRclone);
            CheckVersionCommand = new RelayCommand(async () => await CheckVersionAsync(), () => !IsInstalling);
            InstallRcloneCommand = new RelayCommand(async () => await InstallRcloneAsync(), () => !IsInstalling);
            OpenRcloneConfigCommand = new RelayCommand(() => _rcloneService.OpenConfig(), () => !IsRunning);
            OpenRcloneConfigDirCommand = new RelayCommand(async () => await OpenRcloneConfigDirAsync());
            SaveAppSettingsCommand = new RelayCommand(SaveAppSettings);
            LoadRemotesCommand = new RelayCommand(async () => await LoadRemotesAsync());
            LoadBackupLogCommand = new RelayCommand(async () => await LoadBackupLogAsync());
            AddTaskCommand = new RelayCommand(AddTask);
            DeleteTaskCommand = new RelayCommand(param => DeleteTask(param as BackupTaskItem));
            AddTaskWithFolderCommand = new RelayCommand(AddTaskWithFolder);
            ScanExtensionsCommand = new RelayCommand(async () => await ScanExtensionsAsync());
            ClearQueueCommand = new RelayCommand(() => SyncQueue.Clear());

            StartRenameProfileCommand = new RelayCommand(() =>
            {
                if (SelectedProfile != null)
                {
                    NewProfileName = SelectedProfile.Name;
                    IsRenamingProfile = true;
                }
            });

            SaveRenameProfileCommand = new RelayCommand(() =>
            {
                if (SelectedProfile == null) return;
                var trimmed = (NewProfileName ?? "").Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    MessageBox.Show("Tên profile không được để trống.", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                foreach (var p in Profiles)
                {
                    if (p != SelectedProfile && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("Tên profile đã tồn tại.", "Cảnh báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                var current = SelectedProfile;
                var idx = Profiles.IndexOf(current);
                if (idx >= 0)
                {
                    current.Name = trimmed;
                    Profiles[idx] = null;
                    Profiles[idx] = current;
                    SelectedProfile = current;
                }

                IsRenamingProfile = false;
                SaveConfig();
                AddLog($"✏ Đổi tên profile thành: {trimmed}");
            });

            CancelRenameProfileCommand = new RelayCommand(() =>
            {
                IsRenamingProfile = false;
            });

            LoadConfig();

            _logFlushTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _logFlushTimer.Tick += FlushLogs;
            _logFlushTimer.Start();

            AddLog("SyncDB v2.0 khởi động");
            if (!_rcloneService.RcloneExists())
                AddLog("⚠ rclone.exe không tìm thấy — vào tab Cài đặt để tải về");
            else
                Task.Run(async () => await LoadRemotesAsync());
        }

        // ═══ Load / Save ═══
        private void LoadConfig()
        {
            Profiles = new ObservableCollection<SyncProfile>(_appConfig.Profiles);
            if (Profiles.Count == 0)
                Profiles.Add(new SyncProfile());

            var idx = Math.Max(0, Math.Min(_appConfig.ActiveProfileIndex, Profiles.Count - 1));
            SelectedProfile = Profiles[idx];

            RclonePath = _appConfig.RclonePath ?? "";
            MinimizeToTray = _appConfig.MinimizeToTray;
            StartMinimized = _appConfig.StartMinimized;
            RunOnStartup = _appConfig.RunOnStartup;
        }

        private void SaveConfig()
        {
            if (SelectedProfile != null)
            {
                SelectedProfile.Tasks = BackupTasks.ToList();
                // Đồng bộ ngược BackupPath/RemotePath để tương thích với cấu hình cũ nếu cần
                if (BackupTasks.Count > 0)
                {
                    SelectedProfile.BackupPath = string.Join(Environment.NewLine, BackupTasks.Select(t => t.Source));
                    SelectedProfile.RemotePath = string.Join(Environment.NewLine, BackupTasks.Select(t => t.Destination));
                }
                else
                {
                    SelectedProfile.BackupPath = "";
                    SelectedProfile.RemotePath = "";
                }
            }
            _appConfig.Profiles = Profiles.ToList();
            _appConfig.ActiveProfileIndex = Profiles.IndexOf(SelectedProfile);
            _configService.Save(_appConfig);
            AddLog("💾 Config đã lưu");
        }

        private void SaveAppSettings()
        {
            _appConfig.RclonePath = RclonePath;
            _appConfig.MinimizeToTray = MinimizeToTray;
            _appConfig.StartMinimized = StartMinimized;
            _appConfig.RunOnStartup = RunOnStartup;
            _configService.Save(_appConfig);
            _rcloneService.UpdateRclonePath(RclonePath);
            ApplyRunOnStartup(RunOnStartup);
            AddLog("✔ Đã lưu cài đặt ứng dụng");
        }

        private void ApplyRunOnStartup(bool enable)
        {
            const string regKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            const string appName = "SyncDB";
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(regKey, writable: true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        var exePath = Environment.ProcessPath ?? System.IO.Path.Combine(System.AppContext.BaseDirectory, "ZeroSync.exe");
                        key.SetValue(appName, "\"" + exePath + "\"");
                    }
                    else
                        key.DeleteValue(appName, throwOnMissingValue: false);
                }
            }
            catch (Exception ex) { AddLog("⚠ Registry startup: " + ex.Message); }
        }

        // ═══ Profile ═══
        private void RefreshProfileBindings()
        {
            if (SelectedProfile == null) return;
            var p = SelectedProfile;

            _backupPath = p.BackupPath;
            _remotePath = p.RemotePath;
            _selectedSyncMode = (int)p.SyncMode;
            _ignoreExisting = p.IgnoreExisting;
            _dryRun = p.DryRun;
            _transfers = p.Transfers;
            _checkers = p.Checkers;
            _bandwidthLimit = p.BandwidthLimit;
            _selectedLogLevel = LogLevels.IndexOf(p.LogLevel);
            if (_selectedLogLevel < 0) _selectedLogLevel = 1;
            _extraFlags = p.ExtraFlags;
            _watchEnabled = p.WatchEnabled;
            _debounceSec = p.DebounceSec;
            _fileFilter = p.FileFilter;
            _fileLockTimeoutSec = p.FileLockTimeoutSec;
            _fileLockRetryMs = p.FileLockRetryMs;
            _syncOnStartWatch = p.SyncOnStartWatch;

            OnPropertyChanged(nameof(BackupPath));
            OnPropertyChanged(nameof(RemotePath));
            OnPropertyChanged(nameof(SelectedSyncMode));
            OnPropertyChanged(nameof(IgnoreExisting));
            OnPropertyChanged(nameof(DryRun));
            OnPropertyChanged(nameof(Transfers));
            OnPropertyChanged(nameof(Checkers));
            OnPropertyChanged(nameof(BandwidthLimit));
            OnPropertyChanged(nameof(SelectedLogLevel));
            OnPropertyChanged(nameof(ExtraFlags));
            OnPropertyChanged(nameof(WatchEnabled));
            OnPropertyChanged(nameof(DebounceSec));
            OnPropertyChanged(nameof(FileFilter));
            OnPropertyChanged(nameof(FileLockTimeoutSec));
            OnPropertyChanged(nameof(FileLockRetryMs));
            OnPropertyChanged(nameof(SyncOnStartWatch));

            // Nạp danh sách BackupTasks từ Profile
            BackupTasks.Clear();
            if (p.Tasks != null && p.Tasks.Count > 0)
            {
                foreach (var t in p.Tasks)
                {
                    // Nếu là task cũ chưa có filter hay sync mode riêng, map từ profile sang
                    if (t.SyncMode == RcloneSyncMode.Copy && t.FileFilter == "*.*")
                    {
                        t.SyncMode = p.SyncMode;
                        if (!string.IsNullOrEmpty(p.FileFilter))
                            t.FileFilter = p.FileFilter;
                    }
                    t.PropertyChanged += (s, e) => ValidateProfile();
                    BackupTasks.Add(t);
                }
            }
            else
            {
                // Tương thích ngược: Sinh task từ BackupPath/RemotePath cũ
                var sources = (p.BackupPath ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                var dests = (p.RemotePath ?? "").Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
                for (int i = 0; i < Math.Max(sources.Count, dests.Count); i++)
                {
                    var src = i < sources.Count ? sources[i] : "";
                    var dest = i < dests.Count ? dests[i] : "";
                    if (!string.IsNullOrEmpty(src) || !string.IsNullOrEmpty(dest))
                    {
                        var task = new BackupTaskItem
                        {
                            Source = src,
                            Destination = dest,
                            SyncMode = p.SyncMode,
                            FileFilter = p.FileFilter ?? "*.*"
                        };
                        task.PropertyChanged += (s, e) => ValidateProfile();
                        BackupTasks.Add(task);
                    }
                }
                p.Tasks = BackupTasks.ToList();
            }

            UpdateCheckboxesFromFileFilter();
            ValidateProfile();
        }

        private void ValidateProfile()
        {
            if (SelectedProfile == null)
            {
                ProfileError = "";
                HasProfileError = false;
                return;
            }

            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(BackupPath))
            {
                errors.Add("Nguồn trống");
            }
            else
            {
                var sources = BackupPath.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(s => s.Trim())
                                        .ToList();
                if (sources.Count == 0)
                {
                    errors.Add("Nguồn trống");
                }
                else
                {
                    foreach (var src in sources)
                    {
                        if (!src.Contains(":") || (src.Length > 1 && src[1] == ':'))
                        {
                            if (!Directory.Exists(src))
                            {
                                errors.Add($"Nguồn không tồn tại: {Path.GetFileName(src)}");
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(RemotePath))
            {
                errors.Add("Đích trống");
            }
            else
            {
                var dests = RemotePath.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(d => d.Trim())
                                      .ToList();
                if (dests.Count == 0)
                {
                    errors.Add("Đích trống");
                }
            }

            if (errors.Count > 0)
            {
                ProfileError = string.Join(" | ", errors);
                HasProfileError = true;
            }
            else
            {
                ProfileError = "";
                HasProfileError = false;
            }
        }

        private void UpdateFileFilterFromCheckboxes()
        {
            if (_isUpdatingFileFilter) return;
            _isUpdatingFileFilter = true;
            try
            {
                var allOption = FileExtensionOptions.FirstOrDefault(o => o.Extension == "*.*");
                var checkedExts = FileExtensionOptions
                    .Where(o => o.IsChecked && o.Extension != "*.*")
                    .Select(o => "*" + o.Extension)
                    .ToList();

                if (allOption != null && allOption.IsChecked)
                {
                    // Nếu người dùng vừa check "Tất cả", ta bỏ check tất cả đuôi cụ thể
                    if (FileFilter != "*.*")
                    {
                        foreach (var opt in FileExtensionOptions.Where(o => o.Extension != "*.*"))
                        {
                            opt.SetCheckedQuietly(false);
                        }
                        FileFilter = "*.*";
                        return;
                    }
                    else if (checkedExts.Count > 0)
                    {
                        // Nếu đang ở chế độ "Tất cả" mà check thêm 1 đuôi cụ thể, ta bỏ check "Tất cả"
                        allOption.SetCheckedQuietly(false);
                    }
                }

                if (checkedExts.Count > 0)
                {
                    FileFilter = string.Join(";", checkedExts);
                }
                else
                {
                    // Không có đuôi cụ thể nào được check, tự động check "Tất cả"
                    if (allOption != null && !allOption.IsChecked)
                    {
                        allOption.SetCheckedQuietly(true);
                    }
                    FileFilter = "*.*";
                }
            }
            finally { _isUpdatingFileFilter = false; }
        }

        private void UpdateCheckboxesFromFileFilter()
        {
            if (_isUpdatingFileFilter || FileExtensionOptions == null) return;
            _isUpdatingFileFilter = true;
            try
            {
                var filter = (FileFilter ?? "").Trim();
                var allOption = FileExtensionOptions.FirstOrDefault(o => o.Extension == "*.*");

                if (string.IsNullOrEmpty(filter) || filter == "*.*" || filter == "*")
                {
                    if (allOption != null) allOption.SetCheckedQuietly(true);
                    foreach (var opt in FileExtensionOptions.Where(o => o.Extension != "*.*"))
                    {
                        opt.SetCheckedQuietly(false);
                    }
                }
                else
                {
                    if (allOption != null) allOption.SetCheckedQuietly(false);
                    var parts = filter.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(p => p.Trim().TrimStart('*').ToLower())
                                      .ToList();

                    foreach (var opt in FileExtensionOptions.Where(o => o.Extension != "*.*"))
                    {
                        opt.SetCheckedQuietly(parts.Contains(opt.Extension.ToLower()));
                    }
                }
            }
            finally { _isUpdatingFileFilter = false; }
        }

        private void AddProfile()
        {
            var profile = new SyncProfile { Name = "Profile " + (Profiles.Count + 1) };
            Profiles.Add(profile);
            SelectedProfile = profile;
            AddLog("➕ Tạo profile mới: " + profile.Name);
        }

        private void DeleteProfile()
        {
            if (Profiles.Count <= 1) return;
            var name = SelectedProfile.Name;
            Profiles.Remove(SelectedProfile);
            SelectedProfile = Profiles[0];
            AddLog("🗑 Xóa profile: " + name);
        }

        // ═══ Actions ═══
        private void BrowseFolder()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Chọn thư mục backup",
                ShowNewFolderButton = false
            };

            if (!string.IsNullOrEmpty(BackupPath))
            {
                var paths = BackupPath.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (paths.Length > 0 && Directory.Exists(paths[0].Trim()))
                    dialog.SelectedPath = paths[0].Trim();
            }

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (!string.IsNullOrEmpty(BackupPath))
                {
                    var result = MessageBox.Show(
                        "Bạn có muốn THÊM thư mục này vào danh sách nguồn hiện tại không?\n\n(Chọn 'Yes' để thêm mới vào dòng tiếp theo, chọn 'No' để thay thế hoàn toàn)",
                        "Thêm nguồn backup",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        var list = BackupPath.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        if (!list.Contains(dialog.SelectedPath))
                        {
                            BackupPath = (BackupPath.TrimEnd('\r', '\n') + Environment.NewLine + dialog.SelectedPath).Trim();
                        }
                    }
                    else if (result == MessageBoxResult.No)
                    {
                        BackupPath = dialog.SelectedPath;
                    }
                }
                else
                {
                    BackupPath = dialog.SelectedPath;
                }
            }
        }

        private void BrowseRclone()
        {
            var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Chọn rclone.exe",
                Filter = "rclone.exe|rclone.exe|Executable (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                RclonePath = dialog.FileName;
        }

        private async Task CheckVersionAsync()
        {
            IsInstalling = true;
            InstallStatusText = "Đang kiểm tra...";
            try
            {
                RcloneCurrentVersion = await _rcloneService.GetVersionAsync();
                InstallStatusText = "Đang lấy version mới nhất...";
                RcloneLatestVersion = await _installer.GetLatestVersionAsync();
                InstallStatusText = "";
                AddLog($"📋 Hiện tại: {RcloneCurrentVersion} | Mới nhất: {RcloneLatestVersion}");
            }
            finally { IsInstalling = false; }
        }

        private async Task InstallRcloneAsync()
        {
            IsInstalling = true;
            InstallProgress = 0;
            _installCts = new CancellationTokenSource();

            var progress = new Progress<Tuple<int, string>>(p =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    InstallProgress = p.Item1;
                    InstallStatusText = p.Item2;
                });
            });

            try
            {
                var destDir = AppDomain.CurrentDomain.BaseDirectory;
                var result = await _installer.DownloadAndInstallAsync(destDir, progress, _installCts.Token);
                if (result.Item1)
                {
                    AddLog("✔ Cài xong: " + result.Item2);
                    RclonePath = "";
                    _rcloneService.UpdateRclonePath("");
                    RcloneCurrentVersion = await _rcloneService.GetVersionAsync();
                }
                else
                {
                    AddLog("✖ Cài rclone thất bại: " + result.Item2);
                }
            }
            finally { IsInstalling = false; _installCts = null; }
        }

        private async Task OpenRcloneConfigDirAsync()
        {
            var configPath = await _rcloneService.GetConfigFilePathAsync();

            // Fallback: đường dẫn mặc định Windows nếu rclone chưa cài
            if (string.IsNullOrWhiteSpace(configPath))
                configPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "rclone", "rclone.conf");

            var dir = System.IO.Path.GetDirectoryName(configPath);
            AddLog($"📂 Thư mục config rclone: {dir}");

            try
            {
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);

                // Chọn file nếu tồn tại, không thì mở thư mục
                if (System.IO.File.Exists(configPath))
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{configPath}\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception ex) { AddLog("✖ Lỗi mở thư mục: " + ex.Message); }
        }

        private async Task TestConnectionAsync()
        {
            if (string.IsNullOrWhiteSpace(RemotePath))
            {
                AddLog("⚠ Remote path không được trống");
                return;
            }
            StatusText = "🔌 Đang test kết nối...";
            AddLog("🔌 Test kết nối: " + RemotePath);
            var result = await _rcloneService.TestConnectionAsync(RemotePath);
            if (result.Item1)
            {
                StatusText = "✔ Kết nối thành công";
                AddLog("✔ " + result.Item2);
            }
            else
            {
                StatusText = "✖ Kết nối thất bại";
                AddLog("✖ " + result.Item2);
            }
        }

        private void AddTask()
        {
            var task = new BackupTaskItem { Source = "", Destination = "" };
            task.PropertyChanged += (s, e) => ValidateProfile();
            BackupTasks.Add(task);
            if (SelectedProfile != null)
            {
                SelectedProfile.Tasks = BackupTasks.ToList();
            }
            ValidateProfile();
        }

        private void DeleteTask(BackupTaskItem task)
        {
            if (task == null) return;
            BackupTasks.Remove(task);
            if (SelectedProfile != null)
            {
                SelectedProfile.Tasks = BackupTasks.ToList();
            }
            ValidateProfile();
        }

        private void AddTaskWithFolder()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Chọn thư mục nguồn backup",
                ShowNewFolderButton = false
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string defaultDest = SelectedRemote ?? "";
                var task = new BackupTaskItem { Source = dialog.SelectedPath, Destination = defaultDest };
                task.PropertyChanged += (s, e) => ValidateProfile();
                BackupTasks.Add(task);
                if (SelectedProfile != null)
                {
                    SelectedProfile.Tasks = BackupTasks.ToList();
                }
                ValidateProfile();
            }
        }

        private async Task ScanExtensionsAsync()
        {
            var sources = BackupTasks
                .Select(t => t.Source)
                .Where(src => !string.IsNullOrWhiteSpace(src) && (!src.Contains(":") || (src.Length > 1 && src[1] == ':')))
                .ToList();

            if (sources.Count == 0)
            {
                MessageBox.Show("Vui lòng thêm ít nhất một thư mục nguồn cục bộ hợp lệ trong bảng trước khi quét.", 
                                "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            StatusText = "🔍 Đang quét định dạng file...";
            AddLog("🔍 Bắt đầu quét các định dạng file thực tế từ các thư mục nguồn...");

            try
            {
                var extensions = await Task.Run(() =>
                {
                    var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var src in sources)
                    {
                        if (Directory.Exists(src))
                        {
                            try
                            {
                                foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories).Take(10000))
                                {
                                    var ext = Path.GetExtension(file);
                                    if (!string.IsNullOrEmpty(ext))
                                        exts.Add(ext.ToLower());
                                }
                            }
                            catch { }
                        }
                    }
                    return exts;
                });

                var currentChecked = FileExtensionOptions.Where(o => o.IsChecked).Select(o => o.Extension).ToList();
                var defaultExts = new[] { ".bak", ".sql", ".zip", ".rar", ".7z", ".mdf", ".ldf", ".txt", ".log" };

                var allExts = extensions.Concat(defaultExts)
                                        .Select(e => e.ToLower())
                                        .Distinct()
                                        .OrderBy(e => e)
                                        .ToList();

                _dispatcher.Invoke(() =>
                {
                    FileExtensionOptions.Clear();
                    
                    // Thêm option Tất cả (*.*) đầu tiên
                    var allChecked = string.IsNullOrEmpty(FileFilter) || FileFilter == "*.*" || FileFilter == "*";
                    FileExtensionOptions.Add(new FileExtensionOption("*.*", UpdateFileFilterFromCheckboxes)
                    {
                        IsChecked = allChecked
                    });

                    foreach (var ext in allExts)
                    {
                        if (ext == "*.*") continue;
                        var opt = new FileExtensionOption(ext, UpdateFileFilterFromCheckboxes)
                        {
                            IsChecked = !allChecked && currentChecked.Contains(ext)
                        };
                        FileExtensionOptions.Add(opt);
                    }
                    StatusText = "✔ Quét định dạng thành công";
                    AddLog($"✔ Quét xong! Tìm thấy {extensions.Count} định dạng file thực tế từ nguồn.");
                });
            }
            catch (Exception ex)
            {
                StatusText = "✖ Quét định dạng thất bại";
                AddLog("✖ Lỗi khi quét định dạng file: " + ex.Message);
            }
        }

        private async Task DoRunSyncAsync()
        {
            if (IsRunning || SelectedProfile == null) return;

            ValidateProfile();
            if (HasProfileError)
            {
                AddLog("⚠ Vui lòng sửa các lỗi cấu hình trước khi chạy: " + ProfileError);
                return;
            }

            IsRunning = true;
            StatusText = "🔄 Đang đồng bộ...";
            SaveConfig();
            try
            {
                await _rcloneService.RunSyncAsync(SelectedProfile);
            }
            catch (Exception ex)
            {
                AddLog($"✖ Lỗi đồng bộ: {ex.ToString()}");
            }
            finally
            {
                IsRunning = false;
            }
        }

        private void CancelSync()
        {
            _rcloneService.Cancel();
            IsRunning = false;
            StatusText = "⚠ Đã hủy";
            AddLog("⚠ Sync đã bị hủy");
        }

        private async Task StartWatch()
        {
            var validTargets = BackupTasks
                .Where(t => !string.IsNullOrWhiteSpace(t.Source) && Directory.Exists(t.Source))
                .Select(t => new WatcherTarget { Path = t.Source, Filter = t.FileFilter })
                .ToList();

            if (validTargets.Count == 0)
            {
                AddLog("⚠ Không có thư mục nguồn cục bộ hợp lệ để watch");
                return;
            }

            _watcherService.Start(validTargets, DebounceSec, FileLockTimeoutSec, FileLockRetryMs);
            IsWatching = true;
            StatusText = "👁 Đang theo dõi thư mục...";
            SaveConfig();
            AddLog($"👁 Bắt đầu watch: {validTargets.Count} thư mục | Debounce: {DebounceSec}s | Lock timeout: {FileLockTimeoutSec}s | Retry: {FileLockRetryMs}ms");

            if (SyncOnStartWatch)
            {
                AddLog("🔄 Đồng bộ tất cả file hiện có trước khi watch...");
                await DoRunSyncAsync();
            }
        }

        private void StopWatch()
        {
            _watcherService.Stop();
            IsWatching = false;
            StatusText = "Sẵn sàng";
            AddLog("⏹ Dừng watch");
        }

        private void OnOutputReceived(string msg)
        {
            _pendingLogs.Enqueue(msg);
            ParseRcloneLogForQueue(msg);
        }

        private void ParseRcloneLogForQueue(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            try
            {
                // 1. Phân tích các dòng log thành công (Copied, Moved, Deleted)
                if (msg.Contains("INFO  :") || msg.Contains("NOTICE:"))
                {
                    string term = msg.Contains("INFO  :") ? "INFO  :" : "NOTICE:";
                    if (msg.Contains(": Copied (") || msg.Contains(": Moved") || msg.Contains(": Deleted") || msg.Contains(": Updated"))
                    {
                        int idx = msg.IndexOf(term);
                        if (idx >= 0)
                        {
                            string content = msg.Substring(idx + term.Length).Trim();
                            int colonIdx = content.LastIndexOf(':');
                            if (colonIdx > 0)
                            {
                                string fileName = content.Substring(0, colonIdx).Trim();
                                string detail = content.Substring(colonIdx + 1).Trim();
                                AddQueueItem(fileName, "Thành công", detail);
                            }
                        }
                    }
                    else if (msg.Contains("Failed to copy:") || msg.Contains("Failed to move:") || msg.Contains("Failed to delete:"))
                    {
                        int idx = msg.IndexOf(term);
                        if (idx >= 0)
                        {
                            string content = msg.Substring(idx + term.Length).Trim();
                            int colonIdx = content.LastIndexOf(':');
                            if (colonIdx > 0)
                            {
                                string fileName = content.Substring(0, colonIdx).Trim();
                                string detail = content.Substring(colonIdx + 1).Trim();
                                AddQueueItem(fileName, "Thất bại", detail);
                            }
                        }
                    }
                }
                // 2. Phân tích các dòng log lỗi (ERROR)
                else if (msg.Contains("ERROR :"))
                {
                    int idx = msg.IndexOf("ERROR :");
                    if (idx >= 0)
                    {
                        string content = msg.Substring(idx + 7).Trim();
                        int colonIdx = content.IndexOf(':');
                        if (colonIdx > 0)
                        {
                            string fileName = content.Substring(0, colonIdx).Trim();
                            string detail = content.Substring(colonIdx + 1).Trim();
                            AddQueueItem(fileName, "Thất bại", detail);
                        }
                    }
                }
                // 3. Phân tích tiến trình đang truyền (* file: 45% /1.2M/s, 2s)
                else
                {
                    var trimmed = msg.TrimStart(EmptyCharArray);
                    if (trimmed.StartsWith("*") && trimmed.Contains("%"))
                    {
                        string content = trimmed.Substring(1).Trim(); // Bỏ dấu *
                        int colonIdx = content.LastIndexOf(':');
                        if (colonIdx > 0)
                        {
                            string fileName = content.Substring(0, colonIdx).Trim();
                            string detail = content.Substring(colonIdx + 1).Trim();
                            AddQueueItem(fileName, "Đang truyền", detail);
                        }
                    }
                }
            }
            catch { }
        }

        private void AddQueueItem(string fileName, string status, string details)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return;

            // Loại bỏ các dòng text hiển thị tóm tắt thống kê của rclone
            if (fileName.Contains("Checking:") || fileName.Contains("Transferred:") || fileName.Contains("Errors:") || fileName.Contains("Elapsed time:") || fileName.Contains("Remaining:"))
                return;

            _dispatcher.InvokeAsync(() =>
            {
                var existing = SyncQueue.FirstOrDefault(q => q.FileName == fileName);
                if (existing != null)
                {
                    existing.Status = status;
                    existing.Details = details;
                    existing.Time = DateTime.Now.ToString("HH:mm:ss");
                }
                else
                {
                    var item = new QueueItem
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        FileName = fileName,
                        Status = status,
                        Details = details
                    };
                    SyncQueue.Add(item);

                    // Giới hạn 100 dòng, ưu tiên xóa dòng đã hoàn thành trước
                    while (SyncQueue.Count > 100)
                    {
                        var toRemove = SyncQueue.FirstOrDefault(q => q.Status == "Thành công" || q.Status == "Thất bại");
                        if (toRemove != null)
                            SyncQueue.Remove(toRemove);
                        else
                            SyncQueue.RemoveAt(0);
                    }
                }
            });
        }

        private void AddLog(string msg)
        {
            _pendingLogs.Enqueue(msg);
        }

        private void FlushLogs(object sender, EventArgs e)
        {
            if (_pendingLogs.IsEmpty) return;

            bool added = false;
            while (_pendingLogs.TryDequeue(out var log))
            {
                var entry = DateTime.Now.ToString("HH:mm:ss") + "  " + log;
                LogEntries.Add(entry);
                added = true;
            }

            if (added)
            {
                while (LogEntries.Count > 1000)
                    LogEntries.RemoveAt(0);
            }
        }

        private async Task LoadRemotesAsync()
        {
            AddLog("🔄 Đang tải danh sách remote...");
            List<string> remotes;
            try { remotes = await _rcloneService.ListRemotesAsync(); }
            catch { remotes = new List<string>(); }

            // Phải update collection trên UI thread
            await _dispatcher.InvokeAsync(() =>
            {
                RcloneRemotes = new ObservableCollection<string>(remotes);
                if (remotes.Count == 0)
                    AddLog("⚠ Không tìm thấy remote nào — vào tab Cài đặt → mở rclone config");
                else
                    AddLog($"✔ Tìm thấy {remotes.Count} remote: {string.Join(", ", remotes)}");
            });
        }

        private async Task LoadBackupLogAsync()
        {
            IsLoadingLog = true;
            try
            {
                var logDir = _rcloneService.LogDirectory;

                // Scan tất cả file log trong thư mục
                var files = await Task.Run(() =>
                {
                    if (!Directory.Exists(logDir)) return new List<string>();
                    return Directory.GetFiles(logDir, "rclone_*.log")
                        .OrderByDescending(f => f)
                        .Select(f => Path.GetFileName(f))
                        .ToList();
                });

                var prevSelected = SelectedLogFile;
                LogFiles = new ObservableCollection<string>(files);

                // Chọn file: giữ nguyên nếu vẫn tồn tại, không thì chọn mới nhất
                if (files.Contains(prevSelected))
                    SelectedLogFile = prevSelected;
                else
                    SelectedLogFile = files.FirstOrDefault();

                if (SelectedLogFile == null)
                {
                    BackupLogContent = $"Chưa có file log.\nThư mục kiểm tra: {logDir}";
                    return;
                }

                var logPath = Path.Combine(logDir, SelectedLogFile);
                var content = await Task.Run(() =>
                {
                    var lines = new List<string>();
                    using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            lines.Add(line);
                            if (lines.Count > 5000) lines.RemoveAt(0);
                        }
                    }
                    return string.Join("\n", lines);
                });
                BackupLogContent = content;
            }
            catch (Exception ex) { BackupLogContent = "Lỗi đọc log: " + ex.Message; }
            finally { IsLoadingLog = false; }
        }

        public void OnClosing()
        {
            _logFlushTimer?.Stop();
            _watcherService.Dispose();
            _rcloneService.Cancel();
            SaveConfig();
        }
    }

    public class FileExtensionOption : ViewModelBase
    {
        private string _extension;
        public string Extension
        {
            get => _extension;
            set => SetProperty(ref _extension, value);
        }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (SetProperty(ref _isChecked, value))
                {
                    OnCheckedChanged?.Invoke();
                }
            }
        }

        public Action OnCheckedChanged { get; set; }

        public FileExtensionOption(string ext, Action onCheckedChanged)
        {
            _extension = ext;
            OnCheckedChanged = onCheckedChanged;
        }

        public void SetCheckedQuietly(bool value)
        {
            if (_isChecked != value)
            {
                _isChecked = value;
                OnPropertyChanged(nameof(IsChecked));
            }
        }
    }

    public class QueueItem : ViewModelBase
    {
        private string _time = "";
        public string Time
        {
            get => _time;
            set => SetProperty(ref _time, value);
        }

        private string _fileName = "";
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        private string _details = "";
        public string Details
        {
            get => _details;
            set => SetProperty(ref _details, value);
        }
    }
}
