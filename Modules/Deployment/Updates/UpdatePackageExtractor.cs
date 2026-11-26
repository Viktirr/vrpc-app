using System.IO.Compression;
using VRPC.Deployment.Install;

namespace VRPC.Deployment.Updates
{
    // Extracts a downloaded update archive into a fresh directory and refuses
    // anything the signed payload does not account for. Never leaves a partial
    // directory behind that could later be mistaken for a ready package.
    public sealed class UpdatePackageExtractor
    {
        public const int MaxEntries = 512;
        public const long MaxEntryBytes = 200L * 1024 * 1024;
        public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
        public const double MaxEntryCompressionRatio = 500;
        public const double MaxTotalCompressionRatio = 250;

        private const int BufferSize = 81920;
        private const string PayloadFileName = "payload.json";
        private const int FileAttributeReparsePoint = 0x400;
        private const int UnixFileTypeMask = 0xF000;
        private const int UnixSymbolicLink = 0xA000;
        private static readonly string[] RequiredFiles = { "VRPC.exe", "vrpc-installer-ui.exe" };
        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public async Task<UpdateOperationResult> ExtractAsync(string archivePath, string destinationDir, string expectedVersion, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                throw new ArgumentException("An archive path is required.", nameof(archivePath));
            }
            if (string.IsNullOrWhiteSpace(destinationDir))
            {
                throw new ArgumentException("A destination directory is required.", nameof(destinationDir));
            }
            if (!StrictVersion.IsValid(expectedVersion))
            {
                throw new ArgumentException("The expected version is not a strict numeric version.", nameof(expectedVersion));
            }
            if (!File.Exists(archivePath))
            {
                return Failed(UpdateErrorCategory.Extraction, "The update package could not be found.");
            }

            // The caller promises a fresh directory under state/updates/<id>/. On
            // failure this directory is removed again so a partial extraction can
            // never be promoted to "ready".
            string root = Path.GetFullPath(destinationDir);
            bool started = false;
            bool keep = false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Directory.Exists(root))
                {
                    if (Directory.EnumerateFileSystemEntries(root).Any())
                    {
                        return Failed(UpdateErrorCategory.Extraction, "The update staging directory is not empty.");
                    }
                }
                else
                {
                    Directory.CreateDirectory(root);
                }
                started = true;

                using FileStream archiveStream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);

                ZipArchive archive;
                try
                {
                    archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
                }
                catch (InvalidDataException)
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package is not a valid ZIP archive.");
                }

                using (archive)
                {
                    List<PlannedEntry> planned = new();
                    UpdateOperationResult? planError = PlanEntries(archive, root, planned);
                    if (planError is not null) { return planError; }

                    UpdateOperationResult? extractError = await ExtractEntriesAsync(planned, root, cancellationToken).ConfigureAwait(false);
                    if (extractError is not null) { return extractError; }
                }

                if (!VerifyPackageDirectory(root, expectedVersion, out string errorCategory, out string errorMessage))
                {
                    return Failed(errorCategory, errorMessage);
                }

                keep = true;
                return new UpdateOperationResult { Success = true };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                return Failed(UpdateErrorCategory.Extraction, "The update package is not a valid ZIP archive.");
            }
            catch (ArgumentException)
            {
                return Failed(UpdateErrorCategory.Extraction, "The update package contains an unsafe entry path.");
            }
            catch (IOException)
            {
                return Failed(UpdateErrorCategory.Extraction, "The update package could not be extracted.");
            }
            catch (UnauthorizedAccessException)
            {
                return Failed(UpdateErrorCategory.Extraction, "The update package could not be extracted.");
            }
            finally
            {
                if (started && !keep) { TryDeleteDirectory(root); }
            }
        }

        // Re-validates an extracted package against its own payload.json and the
        // signed version. Used after extraction and again before every launch.
        public static bool VerifyPackageDirectory(string directory, string expectedVersion, out string errorCategory, out string errorMessage)
        {
            errorCategory = UpdateErrorCategory.PayloadMismatch;
            errorMessage = "";

            if (!StrictVersion.IsValid(expectedVersion))
            {
                errorCategory = UpdateErrorCategory.Internal;
                errorMessage = "The expected update version is invalid.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                errorCategory = UpdateErrorCategory.Extraction;
                errorMessage = "The update package directory is missing.";
                return false;
            }

            try
            {
                foreach (string required in RequiredFiles)
                {
                    if (!File.Exists(Path.Combine(directory, required)))
                    {
                        errorCategory = UpdateErrorCategory.Extraction;
                        errorMessage = "The update package is incomplete.";
                        return false;
                    }
                }
                if (!File.Exists(Path.Combine(directory, PayloadFileName)))
                {
                    errorCategory = UpdateErrorCategory.Extraction;
                    errorMessage = "The update package is incomplete.";
                    return false;
                }

                PayloadManifest manifest;
                try
                {
                    manifest = PayloadManifest.Load(directory);
                }
                catch (Exception exception) when (exception is PayloadException or IOException or UnauthorizedAccessException)
                {
                    errorCategory = UpdateErrorCategory.PayloadMismatch;
                    errorMessage = "The update package payload manifest is invalid.";
                    return false;
                }

                if (!string.Equals(manifest.Product, UpdateContract.Product, StringComparison.Ordinal))
                {
                    errorCategory = UpdateErrorCategory.PayloadMismatch;
                    errorMessage = "The update package is for a different product.";
                    return false;
                }
                if (!string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal))
                {
                    errorCategory = UpdateErrorCategory.PayloadMismatch;
                    errorMessage = "The update package version does not match the update feed.";
                    return false;
                }

                IReadOnlyList<string> verificationErrors = manifest.VerifyDirectory(directory);
                if (verificationErrors.Count > 0)
                {
                    errorCategory = UpdateErrorCategory.PayloadMismatch;
                    errorMessage = $"The update package failed integrity verification ({verificationErrors.Count} file(s)).";
                    return false;
                }

                HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase) { PayloadFileName };
                foreach (PayloadFile file in manifest.Files) { declared.Add(NormalizeRelative(file.Path)); }

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string relative = NormalizeRelative(Path.GetRelativePath(directory, file));
                    if (!declared.Contains(relative))
                    {
                        errorCategory = UpdateErrorCategory.Extraction;
                        errorMessage = "The update package contains files that are not listed in its payload.";
                        return false;
                    }
                }

                errorCategory = "";
                errorMessage = "";
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errorCategory = UpdateErrorCategory.Extraction;
                errorMessage = "The update package could not be verified.";
                return false;
            }
        }

        private static UpdateOperationResult? PlanEntries(ZipArchive archive, string root, List<PlannedEntry> planned)
        {
            if (archive.Entries.Count > MaxEntries)
            {
                return Failed(UpdateErrorCategory.Extraction, "The update package contains too many entries.");
            }

            HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
            long totalUncompressed = 0;
            long totalCompressed = 0;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an entry without a name.");
                }
                if (name.Contains('\\'))
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an unsafe entry path.");
                }

                bool isDirectory = name.EndsWith("/", StringComparison.Ordinal);
                string relative = isDirectory ? name.TrimEnd('/') : name;
                if (!IsSafeRelativePath(relative))
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an unsafe entry path.");
                }
                if (IsLinkOrReparseEntry(entry))
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains a link or reparse entry.");
                }
                if (isDirectory) { continue; }

                if (entry.Length < 0)
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an entry with an invalid size.");
                }
                if (entry.Length > MaxEntryBytes)
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an entry that is too large.");
                }

                totalUncompressed += entry.Length;
                if (totalUncompressed > MaxTotalBytes)
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package expands to more data than allowed.");
                }
                if (entry.CompressedLength == 0 && entry.Length > 0)
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an entry with an unsafe compression ratio.");
                }
                if (entry.CompressedLength > 0)
                {
                    totalCompressed += entry.CompressedLength;
                    if ((double)entry.Length / entry.CompressedLength > MaxEntryCompressionRatio)
                    {
                        return Failed(UpdateErrorCategory.Extraction, "The update package contains an entry with an unsafe compression ratio.");
                    }
                }

                string destination = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsContained(root, destination))
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an unsafe entry path.");
                }
                if (!destinations.Add(destination))
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains duplicate entry paths.");
                }

                planned.Add(new PlannedEntry(entry, destination, entry.Length));
            }

            if (totalCompressed > 0 && (double)totalUncompressed / totalCompressed > MaxTotalCompressionRatio)
            {
                return Failed(UpdateErrorCategory.Extraction, "The update package has an unsafe compression ratio.");
            }

            return null;
        }

        private static async Task<UpdateOperationResult?> ExtractEntriesAsync(List<PlannedEntry> planned, string root, CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[BufferSize];

            foreach (PlannedEntry item in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? directory = Path.GetDirectoryName(item.Destination);
                if (!string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }
                if (!IsContained(root, item.Destination))
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an unsafe entry path.");
                }

                using Stream source = item.Entry.Open();
                using FileStream output = new(item.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);
                long written = 0;

                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0) { break; }

                    written += read;
                    if (written > item.Length)
                    {
                        return Failed(UpdateErrorCategory.Extraction, "The update package contains an entry with inconsistent sizes.");
                    }

                    try
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        return Failed(UpdateErrorCategory.Disk, "The update package could not be written to disk.");
                    }
                }

                if (written != item.Length)
                {
                    return Failed(UpdateErrorCategory.Extraction, "The update package contains an entry with inconsistent sizes.");
                }
            }

            return null;
        }

        private static bool IsSafeRelativePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) { return false; }
            if (relative[0] == '/') { return false; }
            if (relative.Length >= 2 && relative[1] == ':') { return false; }

            foreach (string segment in relative.Split('/'))
            {
                if (segment.Length == 0) { return false; }
                if (segment == "." || segment == "..") { return false; }
                if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)) { return false; }

                foreach (char character in segment)
                {
                    if (character < 0x20 || character == '"' || character == '<' || character == '>' ||
                        character == '|' || character == '?' || character == '*' || character == ':')
                    {
                        return false;
                    }
                }

                int dot = segment.IndexOf('.');
                string stem = dot < 0 ? segment : segment[..dot];
                if (ReservedDeviceNames.Contains(stem)) { return false; }
            }

            return true;
        }

        // Unix mode symlinks and Windows reparse-point attributes are refused
        // even though extraction itself would only create regular files.
        private static bool IsLinkOrReparseEntry(ZipArchiveEntry entry)
        {
            int attributes = entry.ExternalAttributes;
            int unixMode = (attributes >> 16) & 0xFFFF;
            if ((unixMode & UnixFileTypeMask) == UnixSymbolicLink) { return true; }
            return (attributes & FileAttributeReparsePoint) != 0;
        }

        private static bool IsContained(string root, string fullPath)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.Length > normalizedRoot.Length &&
                   fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRelative(string path)
            => path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

        private static UpdateOperationResult Failed(string category, string message)
            => new() { Success = false, ErrorCategory = category, ErrorMessage = message };

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) { Directory.Delete(path, recursive: true); }
            }
            catch { }
        }

        private sealed class PlannedEntry
        {
            public PlannedEntry(ZipArchiveEntry entry, string destination, long length)
            {
                Entry = entry;
                Destination = destination;
                Length = length;
            }

            public ZipArchiveEntry Entry { get; }
            public string Destination { get; }
            public long Length { get; }
        }
    }
}
