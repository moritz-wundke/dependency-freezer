using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
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
            using var response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var metadata = SimpleJson.ParseObject(payload);

            var dist = metadata.TryGetValue("dist", out var distValue) ? distValue as Dictionary<string, object?> : null;
            if (dist is null)
            {
                throw new InvalidOperationException($"Registry '{normalizedRegistryUrl}' did not return dist metadata for '{packageName}@{version}'.");
            }

            var tarballUrl = SimpleJson.ReadString(dist.TryGetValue("tarball", out var tarballValue) ? tarballValue : null);
            if (string.IsNullOrWhiteSpace(tarballUrl))
            {
                throw new InvalidOperationException($"Registry '{normalizedRegistryUrl}' did not provide a tarball URL for '{packageName}@{version}'.");
            }

            var dependencies = SimpleJson.ReadStringMap(metadata.TryGetValue("dependencies", out var dependenciesValue) ? dependenciesValue : null);

            return new RegistryPackageMetadata(
                SimpleJson.ReadString(metadata.TryGetValue("name", out var nameValue) ? nameValue : null) ?? packageName,
                SimpleJson.ReadString(metadata.TryGetValue("version", out var versionValue) ? versionValue : null) ?? version,
                normalizedRegistryUrl,
                tarballUrl,
                SimpleJson.ReadString(dist.TryGetValue("integrity", out var integrityValue) ? integrityValue : null),
                SimpleJson.ReadString(dist.TryGetValue("shasum", out var shasumValue) ? shasumValue : null),
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
