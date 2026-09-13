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

            // Parse Shadow Copy Volume Name: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy{N}
            var volMatch = Regex.Match(output, @"Shadow Copy Volume Name:\s*(\\\\\?\\GLOBALROOT\\Device\\[^\r\n]+)", RegexOptions.IgnoreCase);
            // Parse Shadow Copy ID: {GUID}
            var idMatch = Regex.Match(output, @"Shadow Copy ID:\s*(\{[0-9a-fA-F\-]+\})", RegexOptions.IgnoreCase);

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
