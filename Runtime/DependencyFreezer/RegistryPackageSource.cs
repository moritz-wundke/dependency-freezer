using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DependencyFreezer
{
    internal sealed class RegistryPackageSource
    {
        private readonly HttpClient _httpClient;

        public RegistryPackageSource(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<RegistryPackageMetadata> GetPackageMetadataAsync(string registryUrl, string packageName, string version, CancellationToken cancellationToken)
        {
            var normalizedRegistryUrl = registryUrl.TrimEnd('/');
            var requestUri = $"{normalizedRegistryUrl}/{Uri.EscapeDataString(packageName)}/{Uri.EscapeDataString(version)}";
            var metadata = await _httpClient.GetFromJsonAsync<JsonObject>(requestUri, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Registry '{normalizedRegistryUrl}' returned an empty metadata document for '{packageName}@{version}'.");

            var dist = metadata["dist"] as JsonObject ?? throw new InvalidOperationException($"Registry '{normalizedRegistryUrl}' did not return dist metadata for '{packageName}@{version}'.");
            var tarballUrl = dist["tarball"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(tarballUrl))
            {
                throw new InvalidOperationException($"Registry '{normalizedRegistryUrl}' did not provide a tarball URL for '{packageName}@{version}'.");
            }

            var dependencies = (metadata["dependencies"] as JsonObject)?
                .Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value?.GetValue<string>() ?? string.Empty))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);

            return new RegistryPackageMetadata(
                metadata["name"]?.GetValue<string>() ?? packageName,
                metadata["version"]?.GetValue<string>() ?? version,
                normalizedRegistryUrl,
                tarballUrl,
                dist["integrity"]?.GetValue<string>(),
                dist["shasum"]?.GetValue<string>(),
                dependencies);
        }

        public async Task<byte[]> DownloadTarballAsync(string tarballUrl, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(tarballUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        public static string ResolveIntegrity(RegistryPackageMetadata metadata, byte[] bytes)
        {
            if (!string.IsNullOrWhiteSpace(metadata.Integrity))
            {
                VerifyIntegrity(metadata.Integrity, bytes, metadata.Name, metadata.Version);
                return metadata.Integrity;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Shasum))
            {
                var sha1 = SHA1.HashData(bytes);
                var actual = Convert.ToHexString(sha1).ToLowerInvariant();
                if (!string.Equals(actual, metadata.Shasum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"SHA1 checksum mismatch for '{metadata.Name}@{metadata.Version}'. Expected '{metadata.Shasum}' but got '{actual}'.");
                }
            }

            return $"sha512-{Convert.ToBase64String(SHA512.HashData(bytes))}";
        }

        private static void VerifyIntegrity(string integrity, byte[] bytes, string packageName, string version)
        {
            var parts = integrity.Split('-', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                throw new InvalidOperationException($"Unsupported integrity format '{integrity}' for '{packageName}@{version}'.");
            }

            var actual = parts[0].ToLowerInvariant() switch
            {
                "sha1" => SHA1.HashData(bytes),
                "sha256" => SHA256.HashData(bytes),
                "sha512" => SHA512.HashData(bytes),
                _ => throw new InvalidOperationException($"Unsupported integrity algorithm '{parts[0]}' for '{packageName}@{version}'."),
            };

            var expected = Convert.FromBase64String(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidOperationException($"Integrity mismatch for '{packageName}@{version}'.");
            }
        }
    }
}
