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

        [Fact]
        public void LocalizedOutput_VolumeAndIdRegex_MatchesCorrectlyAcrossLanguages()
        {
            // Simulate Vietnamese Windows vssadmin output
            var vietnameseOutput = @"vssadmin 1.1 - Công cụ dòng lệnh Quản trị Bản sao Bóng Khối lượng
(C) Bản quyền 2001-2013 Microsoft Corp.

Đã tạo thành công bản sao bóng:
    Tên ổ bản sao bóng: \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy15
    ID bản sao bóng: {C346FD78-B590-449D-9964-6E5DA4A29A09}
";

            var volMatch = System.Text.RegularExpressions.Regex.Match(vietnameseOutput, @"(\\\\\?\\GLOBALROOT\\Device\\HarddiskVolumeShadowCopy\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var idMatch = System.Text.RegularExpressions.Regex.Match(vietnameseOutput, @"(\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\})");

            Assert.True(volMatch.Success);
            Assert.Equal(@"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy15", volMatch.Groups[1].Value);

            Assert.True(idMatch.Success);
            Assert.Equal("{C346FD78-B590-449D-9964-6E5DA4A29A09}", idMatch.Groups[1].Value);
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
