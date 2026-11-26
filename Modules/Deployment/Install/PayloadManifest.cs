using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRPC.Application;

namespace VRPC.Deployment.Install
{
    // Declared payload contract: exact files, sizes and SHA-256 hashes.
    public sealed class PayloadFile
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    public sealed class PayloadManifest
    {
        public int Schema { get; set; } = 1;
        public string Product { get; set; } = AppPaths.ProductName;
        public string Version { get; set; } = "";
        public string Channel { get; set; } = "stable";
        public string EntryPoint { get; set; } = AppPaths.AppExeName;
        public List<PayloadFile> Files { get; set; } = new();

        [JsonIgnore]
        public string SourceDirectory { get; private set; } = "";

        [JsonIgnore]
        public string SourceManifestPath => Path.Combine(SourceDirectory, AppPaths.PayloadManifestName);

        [JsonIgnore]
        public string EntryPointPath => Path.Combine(SourceDirectory, EntryPoint);

        // Loads and validates the manifest before any file is copied.
        public static PayloadManifest Load(string directory)
        {
            string manifestPath = Path.Combine(directory, AppPaths.PayloadManifestName);
            if (!File.Exists(manifestPath))
            {
                throw new PayloadException($"No {AppPaths.PayloadManifestName} was found next to the setup executable. This build is incomplete; extract the full release package.");
            }

            PayloadManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllText(manifestPath, Encoding.UTF8), InstallJson.Options);
            }
            catch (Exception e)
            {
                throw new PayloadException($"The payload manifest could not be read: {e.Message}");
            }

            if (manifest is null) { throw new PayloadException("The payload manifest is empty."); }
            manifest.SourceDirectory = Path.GetFullPath(directory);
            manifest.Validate();
            return manifest;
        }

        public void Validate()
        {
            if (Schema != 1) { throw new PayloadException($"Unsupported payload schema {Schema}."); }
            if (!string.Equals(Product, AppPaths.ProductName, StringComparison.OrdinalIgnoreCase))
            {
                throw new PayloadException($"The payload is for '{Product}', not {AppPaths.ProductName}.");
            }
            if (!ProductInfo.TryParseVersion(Version, out _)) { throw new PayloadException($"The payload version '{Version}' is invalid."); }
            if (Files.Count == 0) { throw new PayloadException("The payload manifest contains no files."); }
            if (Files.Count > 2000) { throw new PayloadException("The payload manifest contains too many files."); }

            string root = Path.GetFullPath(SourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (PayloadFile file in Files)
            {
                if (string.IsNullOrWhiteSpace(file.Path)) { throw new PayloadException("A payload entry has an empty path."); }
                if (Path.IsPathRooted(file.Path)) { throw new PayloadException($"Payload entry '{file.Path}' must be relative."); }
                if (file.Path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == ".."))
                {
                    throw new PayloadException($"Payload entry '{file.Path}' escapes the payload directory.");
                }

                string fullPath = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PayloadException($"Payload entry '{file.Path}' escapes the payload directory.");
                }
                if (!seen.Add(fullPath)) { throw new PayloadException($"Payload entry '{file.Path}' is listed twice."); }
                if (file.Size < 0) { throw new PayloadException($"Payload entry '{file.Path}' has a negative size."); }
                if (string.IsNullOrWhiteSpace(file.Sha256)) { throw new PayloadException($"Payload entry '{file.Path}' has no hash."); }
            }

            if (!Files.Any(file => string.Equals(file.Path, EntryPoint, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PayloadException($"The payload entry point '{EntryPoint}' is not listed in the manifest.");
            }
        }

        // Verifies the source package before staging.
        public IReadOnlyList<string> Verify()
        {
            List<string> errors = new();
            string root = Path.GetFullPath(SourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (PayloadFile file in Files)
            {
                string fullPath = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!File.Exists(fullPath))
                {
                    errors.Add($"Missing: {file.Path}");
                    continue;
                }

                FileInfo info = new(fullPath);
                if (info.Length != file.Size)
                {
                    errors.Add($"Size mismatch: {file.Path} (expected {file.Size}, found {info.Length})");
                    continue;
                }

                string actual = HashFile(fullPath);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Hash mismatch: {file.Path}");
                }
            }

            return errors;
        }

        // Copies into staging and re-checks each destination path.
        public void CopyTo(string destinationDirectory)
        {
            string destinationRoot = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destinationRoot);

            foreach (PayloadFile file in Files)
            {
                string source = Path.GetFullPath(Path.Combine(SourceDirectory, file.Path));
                string destination = Path.GetFullPath(Path.Combine(destinationRoot, file.Path));
                if (!destination.StartsWith(destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PayloadException($"Payload entry '{file.Path}' would be copied outside the staging directory.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }

        public IReadOnlyList<string> VerifyDirectory(string directory)
        {
            List<string> errors = new();
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (PayloadFile file in Files)
            {
                string fullPath = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!File.Exists(fullPath))
                {
                    errors.Add($"Missing after copy: {file.Path}");
                    continue;
                }

                FileInfo info = new(fullPath);
                if (info.Length != file.Size)
                {
                    errors.Add($"Size mismatch after copy: {file.Path}");
                    continue;
                }

                if (!string.Equals(HashFile(fullPath), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"Hash mismatch after copy: {file.Path}");
                }
            }

            return errors;
        }

        // Streaming hash so the package does not have to fit in memory.
        public static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }

    public sealed class PayloadException : Exception
    {
        public PayloadException(string message) : base(message) { }
    }
}
