using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ZeroSync.Service
{
    public class VssSnapshotInfo
    {
        public string SnapshotId { get; set; } = string.Empty;
        public string VolumeName { get; set; } = string.Empty;
        public char DriveLetter { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class VssSnapshotService : IDisposable
    {
        private VssSnapshotInfo _activeSnapshot;

        public static bool IsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Attempts to copy a locked file using permissive FileShare flags (ReadWrite | Delete)
        /// without requiring VSS elevation. Works for locked databases and logs open with shared reads.
        /// </summary>
        public static bool TryCopyLockedFileShared(string sourceFilePath, string destinationFilePath, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                var destDir = Path.GetDirectoryName(destinationFilePath);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                const int bufferSize = 64 * 1024; // 64 KB
                using (var sourceStream = new FileStream(
                    sourceFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize,
                    FileOptions.SequentialScan))
                using (var destStream = new FileStream(
                    destinationFilePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize))
                {
                    sourceStream.CopyTo(destStream);
                }

                // Match original timestamps
                File.SetLastWriteTimeUtc(destinationFilePath, File.GetLastWriteTimeUtc(sourceFilePath));
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Creates a Volume Shadow Copy for a specified drive letter (e.g. 'C') using Windows vssadmin.
        /// Requires elevated administrator privileges.
        /// </summary>
        public async Task<VssSnapshotInfo> CreateSnapshotAsync(char driveLetter)
        {
            driveLetter = char.ToUpperInvariant(driveLetter);
            if (!IsAdministrator())
            {
                throw new UnauthorizedAccessException("Creating VSS snapshots requires administrative privileges.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = "vssadmin",
                Arguments = $"create shadow /for={driveLetter}:",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to launch vssadmin process.");
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"vssadmin failed with exit code {process.ExitCode}: {error}\n{output}");
            }

            // Extract Volume Name: find (\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy\d+) regardless of OS language
            var volMatch = Regex.Match(output, @"(\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy\d+)", RegexOptions.IgnoreCase);
            if (!volMatch.Success)
            {
                // Fallback in case device naming differs
                volMatch = Regex.Match(output, @"(\\\\\?\\GLOBALROOT\\Device\\[^\s\r\n]+)", RegexOptions.IgnoreCase);
            }

            // Extract Shadow Copy ID: find GUID pattern {[0-9a-fA-F-]{36}} regardless of label language
            var idMatch = Regex.Match(output, @"(\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\})");

            if (!volMatch.Success)
            {
                throw new InvalidOperationException($"Could not locate Shadow Copy Volume Name from vssadmin output:\n{output}");
            }

            var info = new VssSnapshotInfo
            {
                DriveLetter = driveLetter,
                VolumeName = volMatch.Groups[1].Value.Trim().TrimEnd('\\'),
                SnapshotId = idMatch.Success ? idMatch.Groups[1].Value.Trim() : string.Empty
            };

            _activeSnapshot = info;
            return info;
        }

        /// <summary>
        /// Creates a directory symbolic link to the shadow volume so standard .NET file APIs can access files.
        /// </summary>
        public static bool TryCreateShadowMountLink(string linkDirectoryPath, string shadowVolumeName, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                var target = shadowVolumeName.TrimEnd('\\') + "\\";
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /d \"{linkDirectoryPath}\" \"{target}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    errorMessage = "Failed to launch cmd for mklink.";
                    return false;
                }
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    errorMessage = proc.StandardError.ReadToEnd();
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Removes the directory symbolic link previously created for a shadow volume.
        /// </summary>
        public static void RemoveShadowMountLink(string linkDirectoryPath)
        {
            try
            {
                if (Directory.Exists(linkDirectoryPath))
                {
                    Directory.Delete(linkDirectoryPath);
                }
            }
            catch { }
        }

        /// <summary>
        /// Converts a standard path (e.g. C:\Data\app.db) into its shadow volume path.
        /// </summary>
        public static string GetShadowPath(string fullOriginalPath, string shadowVolumeName)
        {
            if (string.IsNullOrWhiteSpace(fullOriginalPath)) return fullOriginalPath;

            var fullPath = Path.GetFullPath(fullOriginalPath);
            var root = Path.GetPathRoot(fullPath); // e.g. "C:\"

            if (string.IsNullOrEmpty(root)) return fullOriginalPath;

            var relativePath = fullPath.Substring(root.Length);
            var normalizedVolume = shadowVolumeName.TrimEnd('\\');

            return $"{normalizedVolume}\\{relativePath}";
        }

        /// <summary>
        /// Deletes the shadow copy snapshot.
        /// </summary>
        public async Task<bool> DeleteSnapshotAsync(string snapshotId)
        {
            if (string.IsNullOrWhiteSpace(snapshotId)) return false;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "vssadmin",
                    Arguments = $"delete shadows /shadow={snapshotId} /quiet",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_activeSnapshot != null && !string.IsNullOrEmpty(_activeSnapshot.SnapshotId))
            {
                try
                {
                    DeleteSnapshotAsync(_activeSnapshot.SnapshotId).GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignore disposal cleanup errors
                }
                _activeSnapshot = null;
            }
        }
    }
}
