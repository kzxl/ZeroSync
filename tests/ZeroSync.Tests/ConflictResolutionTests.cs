using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using ZeroSync.Service;

namespace ZeroSync.Tests
{
    public class ConflictResolutionTests : IDisposable
    {
        private readonly string _testDir;
        private readonly string _sourceDir;
        private readonly string _destDir;
        private readonly ConflictResolutionService _service;

        public ConflictResolutionTests()
        {
            _testDir = Path.Combine(Path.GetTempPath(), "ZeroSync_ConflictTests_" + Guid.NewGuid().ToString("N"));
            _sourceDir = Path.Combine(_testDir, "Source");
            _destDir = Path.Combine(_testDir, "Dest");
            Directory.CreateDirectory(_sourceDir);
            Directory.CreateDirectory(_destDir);
            _service = new ConflictResolutionService();
        }

        [Fact]
        public async Task DetectConflictsAsync_FindsDifferingFiles()
        {
            var sFile = Path.Combine(_sourceDir, "document.txt");
            var dFile = Path.Combine(_destDir, "document.txt");

            File.WriteAllText(sFile, "Content from Source Version A");
            File.WriteAllText(dFile, "Content from Destination Version B (different length)");

            var conflicts = await _service.DetectConflictsAsync(_sourceDir, _destDir);

            Assert.Single(conflicts);
            Assert.Equal("document.txt", conflicts[0].RelativePath);
        }

        [Fact]
        public async Task ResolveConflictAsync_KeepBothRenamed_PreservesBothFiles()
        {
            var sFile = Path.Combine(_sourceDir, "notes.txt");
            var dFile = Path.Combine(_destDir, "notes.txt");

            File.WriteAllText(sFile, "Source Notes");
            File.WriteAllText(dFile, "Dest Notes");

            var conflicts = await _service.DetectConflictsAsync(_sourceDir, _destDir);
            Assert.Single(conflicts);

            bool resolved = await _service.ResolveConflictAsync(conflicts[0], SyncConflictAction.KeepBothRenamed);

            Assert.True(resolved);
            Assert.Equal("Source Notes", File.ReadAllText(dFile));

            // Destination directory should now contain the renamed archive copy
            var archivedFiles = Directory.GetFiles(_destDir, "notes.conflict_*.txt");
            Assert.Single(archivedFiles);
            Assert.Equal("Dest Notes", File.ReadAllText(archivedFiles[0]));
        }

        [Fact]
        public async Task ResolveConflictAsync_KeepSource_OverwritesDestination()
        {
            var sFile = Path.Combine(_sourceDir, "data.json");
            var dFile = Path.Combine(_destDir, "data.json");

            File.WriteAllText(sFile, "{\"version\": 2, \"status\": \"updated\"}");
            File.WriteAllText(dFile, "{\"version\": 1}");

            var conflicts = await _service.DetectConflictsAsync(_sourceDir, _destDir);
            Assert.Single(conflicts);

            bool resolved = await _service.ResolveConflictAsync(conflicts[0], SyncConflictAction.KeepSource);

            Assert.True(resolved);
            Assert.Equal("{\"version\": 2, \"status\": \"updated\"}", File.ReadAllText(dFile));
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
