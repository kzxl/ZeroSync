using System;
using System.IO;
using Xunit;
using ZeroSync.Service;

namespace ZeroSync.Tests
{
    public class VssSnapshotTests : IDisposable
    {
        private readonly string _testDir;

        public VssSnapshotTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "ZeroSync_VssTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_testDir);
        }

        [Fact]
        public void GetShadowPath_ConvertsStandardDrivePathCorrectly()
        {
            var original = @"C:\Data\Databases\App.sqlite";
            var shadowVol = @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3";

            var result = VssSnapshotService.GetShadowPath(original, shadowVol);

            Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\Data\Databases\App.sqlite", result);
        }

        [Fact]
        public void TryCopyLockedFileShared_CopiesFile_EvenWhenFileIsOpenWithReadWriteShare()
        {
            var sourceFile = Path.Combine(_testDir, "locked_data.db");
            var destFile = Path.Combine(_testDir, "copied_data.db");

            var expectedContent = "ZEROSYNC_PERSISTENT_DATA_STREAM_TEST";
            File.WriteAllText(sourceFile, expectedContent);

            // Open the file with FileShare.ReadWrite to simulate a running DB engine (SQLite WAL, log engine)
            using (var lockStream = new FileStream(sourceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                bool success = VssSnapshotService.TryCopyLockedFileShared(sourceFile, destFile, out string error);

                Assert.True(success, $"Copy should succeed via shared read stream, error: {error}");
                Assert.True(File.Exists(destFile));
                Assert.Equal(expectedContent, File.ReadAllText(destFile));
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_testDir))
                {
                    Directory.Delete(_testDir, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup error
            }
        }
    }
}
