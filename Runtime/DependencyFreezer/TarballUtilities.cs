using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DependencyFreezer
{
    internal static class TarballUtilities
    {
        public static void ExtractPackageTarball(byte[] gzipTarball, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            using var compressedStream = new MemoryStream(gzipTarball, writable: false);
            using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);

            var headerBuffer = new byte[512];
            while (true)
            {
                var bytesRead = ReadExactly(gzipStream, headerBuffer, 0, headerBuffer.Length);
                if (bytesRead == 0 || headerBuffer.All(static b => b == 0))
                {
                    break;
                }

                if (bytesRead < headerBuffer.Length)
                {
                    throw new InvalidOperationException("Unexpected end of tar archive while reading header.");
                }

                var name = ReadString(headerBuffer, 0, 100);
                var sizeText = ReadString(headerBuffer, 124, 12);
                var typeFlag = headerBuffer[156];
                var prefix = ReadString(headerBuffer, 345, 155);
                var size = string.IsNullOrWhiteSpace(sizeText) ? 0 : Convert.ToInt64(sizeText.Trim(), 8);
                var entryName = string.IsNullOrWhiteSpace(prefix) ? name : $"{prefix}/{name}";
                var normalizedEntryName = NormalizeTarEntryName(entryName);

                if (normalizedEntryName is not null)
                {
                    var fullPath = Path.Combine(destinationDirectory, normalizedEntryName);
                    EnsureWithinDirectory(destinationDirectory, fullPath);

                    if (typeFlag == (byte)'5')
                    {
                        Directory.CreateDirectory(fullPath);
                    }
                    else if (typeFlag is 0 or (byte)'0')
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? destinationDirectory);
                        using var fileStream = File.Create(fullPath);
                        CopyExactly(gzipStream, fileStream, size);
                    }
                    else
                    {
                        SkipExactly(gzipStream, size);
                    }
                }
                else
                {
                    SkipExactly(gzipStream, size);
                }

                var remainder = size % 512;
                if (remainder != 0)
                {
                    SkipExactly(gzipStream, 512 - remainder);
                }
            }
        }

        public static string ComputeDirectoryHash(string directoryPath)
        {
            using var sha256 = SHA256.Create();
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(directoryPath, file).Replace('\\', '/');
                var pathBytes = Encoding.UTF8.GetBytes(relativePath + "\n");
                sha256.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
                var fileBytes = File.ReadAllBytes(file);
                sha256.TransformBlock(fileBytes, 0, fileBytes.Length, null, 0);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
        }

        public static (string Name, string Version) ReadExtractedPackageIdentity(string extractedDirectory)
        {
            var packageJsonPath = Path.Combine(extractedDirectory, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                throw new InvalidOperationException($"Embedded package '{extractedDirectory}' is missing package.json.");
            }

            var packageNode = SimpleJson.ParseObject(File.ReadAllText(packageJsonPath));
            var name = SimpleJson.ReadString(packageNode.TryGetValue("name", out var nameValue) ? nameValue : null) ?? throw new InvalidOperationException($"Embedded package '{extractedDirectory}' package.json is missing a name.");
            var version = SimpleJson.ReadString(packageNode.TryGetValue("version", out var versionValue) ? versionValue : null) ?? throw new InvalidOperationException($"Embedded package '{extractedDirectory}' package.json is missing a version.");
            return (name, version);
        }

        public static string SanitizePackagePath(string packageName)
        {
            return packageName.Replace('/', '_').Replace('\\', '_');
        }

        private static string? NormalizeTarEntryName(string entryName)
        {
            var normalized = entryName.Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (normalized.StartsWith("package/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring("package/".Length);
            }
            else if (string.Equals(normalized, "package", StringComparison.Ordinal))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static void EnsureWithinDirectory(string directory, string candidatePath)
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(candidatePath);
            if (!fullPath.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Tarball entry '{candidatePath}' would escape destination directory '{directory}'.");
            }
        }

        private static string ReadString(byte[] buffer, int offset, int count)
        {
            var value = Encoding.ASCII.GetString(buffer, offset, count);
            var terminator = value.IndexOf('\0');
            return (terminator >= 0 ? value.Substring(0, terminator) : value).Trim();
        }

        private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            var totalRead = 0;
            while (totalRead < count)
            {
                var bytesRead = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (bytesRead == 0)
                {
                    break;
                }

                totalRead += bytesRead;
            }

            return totalRead;
        }

        private static void CopyExactly(Stream input, Stream output, long bytes)
        {
            var buffer = new byte[16 * 1024];
            var remaining = bytes;
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidOperationException("Unexpected end of tar archive while reading file contents.");
                }

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static void SkipExactly(Stream input, long bytes)
        {
            var buffer = new byte[16 * 1024];
            var remaining = bytes;
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidOperationException("Unexpected end of tar archive while skipping contents.");
                }

                remaining -= read;
            }
        }
    }
}
