using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace ZeroSync.Service
{
    public enum SyncConflictAction
    {
        KeepSource,
        KeepDestination,
        KeepBothRenamed,
        NewerWins,
        Skip
    }

    public class SyncConflictItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public long SourceSizeBytes { get; set; }
        public DateTime SourceLastModifiedUtc { get; set; }

        public string DestinationPath { get; set; } = string.Empty;
        public long DestinationSizeBytes { get; set; }
        public DateTime DestinationLastModifiedUtc { get; set; }

        public SyncConflictAction ChosenAction { get; set; } = SyncConflictAction.KeepSource;
        public bool IsResolved { get; set; }
        public string ResolutionDetails { get; set; } = string.Empty;
    }

    public class ConflictResolutionService
    {
        /// <summary>
        /// Scans source and destination directories to detect conflicts where files differ in size or modification time.
        /// </summary>
        public async Task<List<SyncConflictItem>> DetectConflictsAsync(string sourceDir, string destinationDir, string searchPattern = "*.*")
        {
            return await Task.Run(() =>
            {
                var conflicts = new List<SyncConflictItem>();

                if (!Directory.Exists(sourceDir) || !Directory.Exists(destinationDir))
                {
                    return conflicts;
                }

                var sourceFiles = Directory.GetFiles(sourceDir, searchPattern, SearchOption.AllDirectories);

                foreach (var sFile in sourceFiles)
                {
                    var relPath = Path.GetRelativePath(sourceDir, sFile);
                    var dFile = Path.Combine(destinationDir, relPath);

                    if (File.Exists(dFile))
                    {
                        var sInfo = new FileInfo(sFile);
                        var dInfo = new FileInfo(dFile);

                        // If size differs or modification time differs by more than 2 seconds (FAT32/NTFS drift tolerance)
                        bool sizeDifferent = sInfo.Length != dInfo.Length;
                        bool timeDifferent = Math.Abs((sInfo.LastWriteTimeUtc - dInfo.LastWriteTimeUtc).TotalSeconds) > 2.0;

                        if (sizeDifferent || timeDifferent)
                        {
                            conflicts.Add(new SyncConflictItem
                            {
                                RelativePath = relPath,
                                SourcePath = sFile,
                                SourceSizeBytes = sInfo.Length,
                                SourceLastModifiedUtc = sInfo.LastWriteTimeUtc,
                                DestinationPath = dFile,
                                DestinationSizeBytes = dInfo.Length,
                                DestinationLastModifiedUtc = dInfo.LastWriteTimeUtc,
                                ChosenAction = sInfo.LastWriteTimeUtc >= dInfo.LastWriteTimeUtc 
                                    ? SyncConflictAction.KeepSource 
                                    : SyncConflictAction.KeepDestination
                            });
                        }
                    }
                }

                return conflicts;
            });
        }

        /// <summary>
        /// Executes the resolution action for a conflict item.
        /// </summary>
        public async Task<bool> ResolveConflictAsync(SyncConflictItem conflict, SyncConflictAction action)
        {
            return await Task.Run(() =>
            {
                try
                {
                    switch (action)
                    {
                        case SyncConflictAction.KeepSource:
                            EnsureDirectory(conflict.DestinationPath);
                            File.Copy(conflict.SourcePath, conflict.DestinationPath, overwrite: true);
                            File.SetLastWriteTimeUtc(conflict.DestinationPath, conflict.SourceLastModifiedUtc);
                            conflict.ResolutionDetails = "Overwritten destination with source copy.";
                            break;

                        case SyncConflictAction.KeepDestination:
                            EnsureDirectory(conflict.SourcePath);
                            File.Copy(conflict.DestinationPath, conflict.SourcePath, overwrite: true);
                            File.SetLastWriteTimeUtc(conflict.SourcePath, conflict.DestinationLastModifiedUtc);
                            conflict.ResolutionDetails = "Overwritten source with destination copy.";
                            break;

                        case SyncConflictAction.KeepBothRenamed:
                            EnsureDirectory(conflict.DestinationPath);
                            var dir = Path.GetDirectoryName(conflict.DestinationPath) ?? "";
                            var nameWithoutExt = Path.GetFileNameWithoutExtension(conflict.DestinationPath);
                            var ext = Path.GetExtension(conflict.DestinationPath);
                            var timestamp = conflict.DestinationLastModifiedUtc.ToString("yyyyMMdd_HHmmss");
                            var renamedDest = Path.Combine(dir, $"{nameWithoutExt}.conflict_{timestamp}{ext}");

                            File.Move(conflict.DestinationPath, renamedDest, overwrite: true);
                            File.Copy(conflict.SourcePath, conflict.DestinationPath, overwrite: true);
                            File.SetLastWriteTimeUtc(conflict.DestinationPath, conflict.SourceLastModifiedUtc);
                            conflict.ResolutionDetails = $"Archived destination as '{Path.GetFileName(renamedDest)}' and copied source.";
                            break;

                        case SyncConflictAction.NewerWins:
                            if (conflict.SourceLastModifiedUtc >= conflict.DestinationLastModifiedUtc)
                            {
                                File.Copy(conflict.SourcePath, conflict.DestinationPath, overwrite: true);
                                File.SetLastWriteTimeUtc(conflict.DestinationPath, conflict.SourceLastModifiedUtc);
                                conflict.ResolutionDetails = "Source was newer; updated destination.";
                            }
                            else
                            {
                                File.Copy(conflict.DestinationPath, conflict.SourcePath, overwrite: true);
                                File.SetLastWriteTimeUtc(conflict.SourcePath, conflict.DestinationLastModifiedUtc);
                                conflict.ResolutionDetails = "Destination was newer; updated source.";
                            }
                            break;

                        case SyncConflictAction.Skip:
                            conflict.ResolutionDetails = "Conflict skipped by user.";
                            break;
                    }

                    conflict.ChosenAction = action;
                    conflict.IsResolved = true;
                    return true;
                }
                catch (Exception ex)
                {
                    conflict.ResolutionDetails = $"Resolution error: {ex.Message}";
                    conflict.IsResolved = false;
                    return false;
                }
            });
        }

        private static void EnsureDirectory(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
