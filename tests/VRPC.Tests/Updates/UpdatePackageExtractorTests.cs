using VRPC.Updates;

namespace VRPC.Tests.Updates
{
    public sealed class UpdatePackageExtractorTests
    {
        private static PackageFixture ValidPackage()
        {
            PackageFixture fixture = new();
            fixture.AddExecutables();
            fixture.Add(System.IO.Path.Combine("runtime", "extra.bin"), TestBytes.Random(64, 4));
            return fixture;
        }

        [Fact]
        public async Task ExtractAsync_ExtractsVerifiedPackage()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("state", "updates", "op1", "package");
            ValidPackage().WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(File.Exists(System.IO.Path.Combine(destination, "VRPC.exe")));
            Assert.True(File.Exists(System.IO.Path.Combine(destination, "vrpc-installer-ui.exe")));
            Assert.True(File.Exists(System.IO.Path.Combine(destination, "payload.json")));
            Assert.True(File.Exists(System.IO.Path.Combine(destination, "runtime", "extra.bin")));
            Assert.True(UpdatePackageExtractor.VerifyPackageDirectory(destination, "1.2.0", out _, out _));
        }

        [Fact]
        public async Task ExtractAsync_AcceptsExistingEmptyDestination()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            Directory.CreateDirectory(destination);
            ValidPackage().WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
        }

        [Theory]
        [InlineData("../evil.txt")]
        [InlineData("..\\evil.txt")]
        [InlineData("/evil.txt")]
        [InlineData("C:/evil.txt")]
        [InlineData("//server/share/evil.txt")]
        [InlineData("dir/../../evil.txt")]
        public async Task ExtractAsync_RejectsUnsafeEntryPaths(string entryName)
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipWriter.Create(archive, new ZipEntrySpec(entryName, TestBytes.Random(4, 1)));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.False(File.Exists(temp.Combine("evil.txt")));
            Assert.False(Directory.Exists(destination));
        }

        [Fact]
        public async Task ExtractAsync_RejectsUnsafeDirectoryEntry()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipWriter.Create(archive, new ZipEntrySpec("../escape/", Array.Empty<byte>()));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
        }

        [Fact]
        public async Task ExtractAsync_RejectsDuplicateCaseInsensitiveEntries()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipWriter.Create(
                archive,
                new ZipEntrySpec("data.dll", TestBytes.Random(8, 1)),
                new ZipEntrySpec("DATA.DLL", TestBytes.Random(8, 2)));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.Contains("duplicate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExtractAsync_RejectsTooManyEntries()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipEntrySpec[] entries = Enumerable.Range(0, UpdatePackageExtractor.MaxEntries + 1)
                .Select(index => new ZipEntrySpec($"file{index}.bin", new byte[1]))
                .ToArray();
            ZipWriter.Create(archive, entries);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.Contains("too many", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExtractAsync_RejectsSymlinkEntry()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            int unixSymlinkAttributes = unchecked((int)0xA1FF0000);
            ZipWriter.Create(archive, new ZipEntrySpec("link", TestBytes.Random(4, 1), unixSymlinkAttributes));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.Contains("link", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExtractAsync_RejectsUnsafeCompressionRatio()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipWriter.Create(archive, new ZipEntrySpec("zeros.bin", new byte[1024 * 1024]));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.Contains("compression ratio", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExtractAsync_RejectsCorruptArchive()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            await File.WriteAllTextAsync(archive, "this is not a zip archive");

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.False(Directory.Exists(destination));
        }

        [Fact]
        public async Task ExtractAsync_RejectsPackageWithoutEntryPoint()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipWriter.Create(
                archive,
                new ZipEntrySpec("payload.json", Array.Empty<byte>()),
                new ZipEntrySpec("vrpc-installer-ui.exe", TestBytes.Random(16, 1)));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.Contains("incomplete", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(destination));
        }

        [Fact]
        public async Task ExtractAsync_RejectsPackageWithoutPayloadJson()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipWriter.Create(
                archive,
                new ZipEntrySpec("VRPC.exe", TestBytes.Random(16, 1)),
                new ZipEntrySpec("vrpc-installer-ui.exe", TestBytes.Random(16, 2)));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
        }

        [Fact]
        public async Task ExtractAsync_RejectsPackageWithoutSetupPanel()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            PackageFixture fixture = new();
            fixture.Add("VRPC.exe", TestBytes.Random(64, 1));
            fixture.WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
        }

        [Fact]
        public async Task ExtractAsync_RejectsVersionMismatch()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            PackageFixture fixture = ValidPackage();
            fixture.Version = "1.1.0";
            fixture.WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.PayloadMismatch, result.ErrorCategory);
            Assert.False(Directory.Exists(destination));
        }

        [Fact]
        public async Task ExtractAsync_RejectsProductMismatch()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            PackageFixture fixture = ValidPackage();
            fixture.Product = "SomethingElse";
            fixture.WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.PayloadMismatch, result.ErrorCategory);
        }

        [Fact]
        public async Task ExtractAsync_RejectsPayloadHashMismatch()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            PackageFixture fixture = ValidPackage();
            fixture.HashOverrides["VRPC.exe"] = new string('0', 64);
            fixture.WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.PayloadMismatch, result.ErrorCategory);
            Assert.Contains("integrity", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExtractAsync_RejectsFileNotListedInPayload()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            PackageFixture fixture = ValidPackage();
            fixture.Add("extra.dll", TestBytes.Random(32, 9));
            fixture.ExcludedFromManifest.Add("extra.dll");
            fixture.WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.Contains("not listed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExtractAsync_AcceptsEmptyFiles()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            PackageFixture fixture = ValidPackage();
            fixture.Add("empty.bin", Array.Empty<byte>());
            fixture.WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(File.Exists(System.IO.Path.Combine(destination, "empty.bin")));
        }

        [Fact]
        public async Task ExtractAsync_RejectsNonEmptyDestination()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            Directory.CreateDirectory(destination);
            string sentinel = System.IO.Path.Combine(destination, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "keep me");
            ValidPackage().WriteZip(archive);

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.True(File.Exists(sentinel));
        }

        [Fact]
        public async Task ExtractAsync_PropagatesPreCancelledToken()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ValidPackage().WriteZip(archive);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", new CancellationToken(canceled: true)));

            Assert.False(Directory.Exists(destination));
        }

        [Theory]
        [InlineData("CON")]
        [InlineData("nul.txt")]
        [InlineData("bad?.dll")]
        [InlineData("bad*.dll")]
        [InlineData("bad|name.dll")]
        public async Task ExtractAsync_RejectsUnsafeWindowsNames(string entryName)
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ZipWriter.Create(archive, new ZipEntrySpec(entryName, TestBytes.Random(4, 1)));

            UpdateOperationResult result = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(UpdateErrorCategory.Extraction, result.ErrorCategory);
            Assert.False(result.ErrorMessage.Contains("could not be extracted", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task VerifyPackageDirectory_DetectsMissingFile()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ValidPackage().WriteZip(archive);
            UpdateOperationResult extraction = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);
            Assert.True(extraction.Success, extraction.ErrorMessage);

            File.Delete(System.IO.Path.Combine(destination, "vrpc-installer-ui.exe"));

            Assert.False(UpdatePackageExtractor.VerifyPackageDirectory(destination, "1.2.0", out string category, out _));
            Assert.Equal(UpdateErrorCategory.Extraction, category);
        }

        [Fact]
        public async Task VerifyPackageDirectory_DetectsUnlistedFile()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ValidPackage().WriteZip(archive);
            UpdateOperationResult extraction = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);
            Assert.True(extraction.Success, extraction.ErrorMessage);

            await File.WriteAllTextAsync(System.IO.Path.Combine(destination, "injected.dll"), "malicious");

            Assert.False(UpdatePackageExtractor.VerifyPackageDirectory(destination, "1.2.0", out string category, out _));
            Assert.Equal(UpdateErrorCategory.Extraction, category);
        }

        [Fact]
        public async Task VerifyPackageDirectory_DetectsVersionMismatch()
        {
            using TempDirectory temp = new();
            string archive = temp.Combine("package.zip");
            string destination = temp.Combine("op", "package");
            ValidPackage().WriteZip(archive);
            UpdateOperationResult extraction = await new UpdatePackageExtractor().ExtractAsync(archive, destination, "1.2.0", CancellationToken.None);
            Assert.True(extraction.Success, extraction.ErrorMessage);

            Assert.False(UpdatePackageExtractor.VerifyPackageDirectory(destination, "1.3.0", out string category, out _));
            Assert.Equal(UpdateErrorCategory.PayloadMismatch, category);
        }
    }
}
