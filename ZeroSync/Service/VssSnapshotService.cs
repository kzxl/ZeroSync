using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        public char? MountedDriveLetter { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class VssSnapshotService : IDisposable
    {
        private VssSnapshotInfo _activeSnapshot;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool DefineDosDevice(uint dwFlags, string lpDeviceName, string lpTargetPath);

        private const uint DDD_RAW_TARGET_PATH = 0x00000001;
        private const uint DDD_REMOVE_DEFINITION = 0x00000002;
        private const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

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
        /// Scans available drive letters from 'Z' down to 'D' (leaving A, B, C standard) to locate an unused letter.
        /// </summary>
        public static char? FindAvailableDriveLetter()
        {
            var taken = new HashSet<char>();
            foreach (var d in Directory.GetLogicalDrives())
            {
                if (!string.IsNullOrEmpty(d))
                {
                    taken.Add(char.ToUpperInvariant(d[0]));
                }
            }

            for (char c = 'Z'; c >= 'D'; c--)
            {
                if (!taken.Contains(c))
                    return c;
            }
            if (!taken.Contains('B')) return 'B';

            return null;
        }

        /// <summary>
        /// Mounts a Volume Shadow Copy to an unused DOS drive letter (e.g. 'Z:', 'Y:') using Win32 DefineDosDevice (DDD_RAW_TARGET_PATH).
        /// This enables standard .NET file APIs (File.OpenRead, Directory.GetFiles) to read locked files without path canonicalization errors.
        /// </summary>
        public static bool TryMountSnapshotAsDrive(string shadowVolumeName, out char mountedDriveLetter, out string errorMessage)
        {
            mountedDriveLetter = '\0';
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(shadowVolumeName))
            {
                errorMessage = "Shadow volume name cannot be empty.";
                return false;
            }

            var driveOpt = FindAvailableDriveLetter();
            if (!driveOpt.HasValue)
            {
                errorMessage = "No unused drive letters available to mount the shadow copy.";
                return false;
            }

            var driveLetter = driveOpt.Value;
            var deviceName = $"{driveLetter}:";

            // Target NT device path: strip \\?\GLOBALROOT prefix if present
            var targetDevice = shadowVolumeName.TrimEnd('\\');
            if (targetDevice.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            {
                targetDevice = targetDevice.Substring(@"\\?\GLOBALROOT".Length);
            }

            bool result = DefineDosDevice(DDD_RAW_TARGET_PATH, deviceName, targetDevice);
            if (!result)
            {
                int errCode = Marshal.GetLastWin32Error();
                errorMessage = $"DefineDosDevice failed with Win32 error code {errCode}.";
                return false;
            }

            mountedDriveLetter = driveLetter;
            return true;
        }

        /// <summary>
        /// Unmounts a previously mounted DOS drive letter for a shadow copy snapshot.
        /// </summary>
        public static bool TryUnmountSnapshotDrive(char driveLetter, string shadowVolumeName, out string errorMessage)
        {
            errorMessage = string.Empty;
            var deviceName = $"{char.ToUpperInvariant(driveLetter)}:";

            var targetDevice = shadowVolumeName.TrimEnd('\\');
            if (targetDevice.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            {
                targetDevice = targetDevice.Substring(@"\\?\GLOBALROOT".Length);
            }

            bool result = DefineDosDevice(DDD_REMOVE_DEFINITION | DDD_EXACT_MATCH_ON_REMOVE, deviceName, targetDevice);
            if (!result)
            {
                result = DefineDosDevice(DDD_REMOVE_DEFINITION, deviceName, null);
            }

            if (!result)
            {
                int errCode = Marshal.GetLastWin32Error();
                errorMessage = $"Failed to remove DOS device for '{deviceName}' (Win32 error: {errCode}).";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Resolves a full local file path (e.g. C:\Data\app.db) to its counterpart on the mounted snapshot drive (e.g. Z:\Data\app.db).
        /// </summary>
        public static string GetMountedSnapshotPath(string fullOriginalPath, char mountedDriveLetter)
        {
            if (string.IsNullOrWhiteSpace(fullOriginalPath)) return fullOriginalPath;

            var fullPath = Path.GetFullPath(fullOriginalPath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return fullOriginalPath;

            var relativePath = fullPath.Substring(root.Length);
            return $"{char.ToUpperInvariant(mountedDriveLetter)}:\\{relativePath}";
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
            if (_activeSnapshot != null)
            {
                if (_activeSnapshot.MountedDriveLetter.HasValue)
                {
                    TryUnmountSnapshotDrive(_activeSnapshot.MountedDriveLetter.Value, _activeSnapshot.VolumeName, out _);
                    _activeSnapshot.MountedDriveLetter = null;
                }

                if (!string.IsNullOrEmpty(_activeSnapshot.SnapshotId))
                {
                    try
                    {
                        DeleteSnapshotAsync(_activeSnapshot.SnapshotId).GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Ignore disposal cleanup errors
                    }
                }
                _activeSnapshot = null;
            }
        }
    }
}

