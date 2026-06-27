using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DependencyFreezer;
using Xunit;

namespace DependencyFreezer.Tests
{

public sealed class DependencyFreezerEngineTests
{
    [Fact]
    public async Task FreezeAsync_EmbedsPackagesAndWritesFrozenLock()
    {
        using var project = TestProject.Create(
            manifestDependencies: new Dictionary<string, string>
            {
                ["com.example.root"] = "1.0.0",
            },
            packagesLockDependencies: new Dictionary<string, object>
            {
                ["com.example.root"] = new
                {
                    version = "1.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string>
                    {
                        ["com.example.child"] = "2.0.0",
                    },
                },
                ["com.example.child"] = new
                {
                    version = "2.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string>(),
                },
            });

        using var engine = new DependencyFreezerEngine(TestRegistryHttpMessageHandler.CreateHttpClient(new Dictionary<string, HttpResponseMessage>
        {
            ["https://registry.example.test/com.example.root/1.0.0"] = JsonResponse(new
            {
                name = "com.example.root",
                version = "1.0.0",
                dependencies = new Dictionary<string, string>
                {
                    ["com.example.child"] = "2.0.0",
                },
                dist = CreateDist("https://registry.example.test/tarballs/com.example.root-1.0.0.tgz", TestPackageArchive.Create("com.example.root", "1.0.0")),
            }),
            ["https://registry.example.test/com.example.child/2.0.0"] = JsonResponse(new
            {
                name = "com.example.child",
                version = "2.0.0",
                dependencies = new Dictionary<string, string>(),
                dist = CreateDist("https://registry.example.test/tarballs/com.example.child-2.0.0.tgz", TestPackageArchive.Create("com.example.child", "2.0.0")),
            }),
            ["https://registry.example.test/tarballs/com.example.root-1.0.0.tgz"] = BinaryResponse(TestPackageArchive.Create("com.example.root", "1.0.0")),
            ["https://registry.example.test/tarballs/com.example.child-2.0.0.tgz"] = BinaryResponse(TestPackageArchive.Create("com.example.child", "2.0.0")),
        }));

        var result = await engine.FreezeAsync(new FreezeRequest(project.RootPath));

        Assert.Equal(new[] { "com.example.child", "com.example.root" }, result.FrozenPackages);
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(project.RootPath, "Packages", "manifest.json")));
        Assert.Equal("file:Packages/FrozenPackages/com.example.root", manifest.RootElement.GetProperty("dependencies").GetProperty("com.example.root").GetString());
        Assert.Equal("file:Packages/FrozenPackages/com.example.child", manifest.RootElement.GetProperty("dependencies").GetProperty("com.example.child").GetString());

        var validation = await engine.ValidateAsync(project.RootPath);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));
        Assert.Contains(validation.Packages, package => package.Name == "com.example.root" && package.State == PackageState.FrozenEmbedded);
        Assert.True(File.Exists(Path.Combine(project.RootPath, "frozen-lock.json")));
        Assert.True(File.Exists(Path.Combine(project.RootPath, "Packages", "FrozenPackages", "com.example.root", "package.json")));
        Assert.True(File.Exists(Path.Combine(project.RootPath, "Packages", "FrozenPackages", "com.example.child", "package.json")));
    }

    [Fact]
    public async Task PreviewUnfreezeAsync_BlocksSharedFrozenDependencies()
    {
        using var project = TestProject.Create(
            manifestDependencies: new Dictionary<string, string>
            {
                ["com.example.root"] = "1.0.0",
                ["com.example.other"] = "3.0.0",
            },
            packagesLockDependencies: new Dictionary<string, object>
            {
                ["com.example.root"] = new
                {
                    version = "1.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string> { ["com.example.shared"] = "2.0.0" },
                },
                ["com.example.other"] = new
                {
                    version = "3.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string> { ["com.example.shared"] = "2.0.0" },
                },
                ["com.example.shared"] = new
                {
                    version = "2.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string>(),
                },
            });

        using var engine = new DependencyFreezerEngine(TestRegistryHttpMessageHandler.CreateHttpClient(TestRegistryHttpMessageHandler.CreateStandardRegistryResponses(new Dictionary<string, string>
        {
            ["com.example.root"] = "1.0.0",
            ["com.example.other"] = "3.0.0",
            ["com.example.shared"] = "2.0.0",
        }, new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["com.example.root"] = new Dictionary<string, string> { ["com.example.shared"] = "2.0.0" },
            ["com.example.other"] = new Dictionary<string, string> { ["com.example.shared"] = "2.0.0" },
            ["com.example.shared"] = new Dictionary<string, string>(),
        })));

        await engine.FreezeAsync(new FreezeRequest(project.RootPath));
        var preview = await engine.PreviewUnfreezeAsync(new UnfreezeRequest(project.RootPath, new[] { "com.example.root" }, false));

        Assert.False(preview.CanProceed);
        Assert.Contains("com.example.root", preview.ImpactedPackages);
        Assert.Contains("com.example.shared", preview.BlockingPackages);
    }

    [Fact]
    public async Task UnfreezeAsync_UnfreezesEverythingAndRestoresOriginalManifestEntries()
    {
        using var project = TestProject.Create(
            manifestDependencies: new Dictionary<string, string>
            {
                ["com.example.root"] = "1.0.0",
            },
            packagesLockDependencies: new Dictionary<string, object>
            {
                ["com.example.root"] = new
                {
                    version = "1.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string> { ["com.example.child"] = "2.0.0" },
                },
                ["com.example.child"] = new
                {
                    version = "2.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string>(),
                },
            });

        using var engine = new DependencyFreezerEngine(TestRegistryHttpMessageHandler.CreateHttpClient(TestRegistryHttpMessageHandler.CreateStandardRegistryResponses(new Dictionary<string, string>
        {
            ["com.example.root"] = "1.0.0",
            ["com.example.child"] = "2.0.0",
        }, new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["com.example.root"] = new Dictionary<string, string> { ["com.example.child"] = "2.0.0" },
            ["com.example.child"] = new Dictionary<string, string>(),
        })));

        await engine.FreezeAsync(new FreezeRequest(project.RootPath));
        var result = await engine.UnfreezeAsync(new UnfreezeRequest(project.RootPath, null, true));

        Assert.Equal(new[] { "com.example.child", "com.example.root" }, result.UnfrozenPackages.OrderBy(name => name, StringComparer.Ordinal));
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(project.RootPath, "Packages", "manifest.json")));
        Assert.Equal("1.0.0", manifest.RootElement.GetProperty("dependencies").GetProperty("com.example.root").GetString());
        Assert.False(manifest.RootElement.GetProperty("dependencies").TryGetProperty("com.example.child", out _));
        Assert.False(File.Exists(Path.Combine(project.RootPath, "frozen-lock.json")));
        Assert.False(Directory.Exists(Path.Combine(project.RootPath, "Packages", "FrozenPackages", "com.example.root")));
    }

    [Fact]
    public async Task ValidateAsync_FailsWhenEmbeddedPackageDrifts()
    {
        using var project = TestProject.Create(
            manifestDependencies: new Dictionary<string, string>
            {
                ["com.example.root"] = "1.0.0",
            },
            packagesLockDependencies: new Dictionary<string, object>
            {
                ["com.example.root"] = new
                {
                    version = "1.0.0",
                    source = "registry",
                    url = "https://registry.example.test",
                    dependencies = new Dictionary<string, string>(),
                },
            });

        using var engine = new DependencyFreezerEngine(TestRegistryHttpMessageHandler.CreateHttpClient(TestRegistryHttpMessageHandler.CreateStandardRegistryResponses(new Dictionary<string, string>
        {
            ["com.example.root"] = "1.0.0",
        }, new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["com.example.root"] = new Dictionary<string, string>(),
        })));

        await engine.FreezeAsync(new FreezeRequest(project.RootPath));
        await File.AppendAllTextAsync(Path.Combine(project.RootPath, "Packages", "FrozenPackages", "com.example.root", "README.txt"), "drift");

        var validation = await engine.ValidateAsync(project.RootPath);

        Assert.False(validation.Success);
        Assert.Contains(validation.Issues, issue => issue.PackageName == "com.example.root" && issue.Message.Contains("drifted", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage BinaryResponse(byte[] payload)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        };

    private static object CreateDist(string tarballUrl, byte[] archive)
        => new
        {
            tarball = tarballUrl,
            integrity = $"sha512-{Convert.ToBase64String(SHA512.HashData(archive))}",
            shasum = Convert.ToHexString(SHA1.HashData(archive)).ToLowerInvariant(),
        };

    private sealed class TestProject : IDisposable
    {
        private TestProject(string rootPath)
        {
            RootPath = rootPath;
        }

        public string RootPath { get; }

        public static TestProject Create(IReadOnlyDictionary<string, string> manifestDependencies, IReadOnlyDictionary<string, object> packagesLockDependencies)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "dependency-freezer-tests", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path.Combine(rootPath, "Packages"));
            File.WriteAllText(Path.Combine(rootPath, "Packages", "manifest.json"), JsonSerializer.Serialize(new
            {
                dependencies = manifestDependencies,
                scopedRegistries = new[]
                {
                    new
                    {
                        name = "Example",
                        url = "https://registry.example.test",
                        scopes = new[] { "com.example" },
                    },
                },
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(rootPath, "Packages", "packages-lock.json"), JsonSerializer.Serialize(new
            {
                dependencies = packagesLockDependencies,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return new TestProject(rootPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class TestRegistryHttpMessageHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, HttpResponseMessage> _responses;

        private TestRegistryHttpMessageHandler(IReadOnlyDictionary<string, HttpResponseMessage> responses)
        {
            _responses = responses;
        }

        public static HttpClient CreateHttpClient(IReadOnlyDictionary<string, HttpResponseMessage> responses)
            => new(new TestRegistryHttpMessageHandler(responses));

        public static Dictionary<string, HttpResponseMessage> CreateStandardRegistryResponses(
            IReadOnlyDictionary<string, string> packages,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> dependencies)
        {
            var responses = new Dictionary<string, HttpResponseMessage>(StringComparer.Ordinal);
            foreach (var (packageName, version) in packages)
            {
                var archive = TestPackageArchive.Create(packageName, version);
                var tarballUrl = $"https://registry.example.test/tarballs/{packageName}-{version}.tgz";
                responses[$"https://registry.example.test/{packageName}/{version}"] = JsonResponse(new
                {
                    name = packageName,
                    version,
                    dependencies = dependencies[packageName],
                    dist = CreateDist(tarballUrl, archive),
                });
                responses[tarballUrl] = BinaryResponse(archive);
            }

            return responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUri = request.RequestUri?.ToString() ?? string.Empty;
            if (!_responses.TryGetValue(requestUri, out var response))
            {
                throw new InvalidOperationException($"Unexpected HTTP request '{requestUri}'.");
            }

            return Task.FromResult(CloneResponse(response));
        }

        private static HttpResponseMessage CloneResponse(HttpResponseMessage response)
        {
            var clone = new HttpResponseMessage(response.StatusCode);
            if (response.Content is not null)
            {
                clone.Content = response.Content is ByteArrayContent
                    ? new ByteArrayContent(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
                    : new StringContent(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Encoding.UTF8, response.Content.Headers.ContentType?.MediaType);
            }

            return clone;
        }
    }

    private static class TestPackageArchive
    {
        public static byte[] Create(string packageName, string version)
        {
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            {
                WriteEntry(gzip, "package/package.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    name = packageName,
                    version,
                })));
                WriteEntry(gzip, "package/README.txt", Encoding.UTF8.GetBytes($"{packageName}@{version}"));
                gzip.Write(new byte[1024], 0, 1024);
            }

            return output.ToArray();
        }

        private static void WriteEntry(Stream stream, string path, byte[] content)
        {
            var header = new byte[512];
            WriteString(header, 0, 100, path);
            WriteOctal(header, 100, 8, 420);
            WriteOctal(header, 108, 8, 0);
            WriteOctal(header, 116, 8, 0);
            WriteOctal(header, 124, 12, content.Length);
            WriteOctal(header, 136, 12, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            for (var index = 148; index < 156; index++)
            {
                header[index] = (byte)' ';
            }

            header[156] = (byte)'0';
            WriteString(header, 257, 6, "ustar");
            WriteString(header, 263, 2, "00");
            var checksum = header.Sum(value => value);
            WriteOctal(header, 148, 8, checksum);
            stream.Write(header, 0, header.Length);
            stream.Write(content, 0, content.Length);
            var padding = 512 - (content.Length % 512);
            if (padding != 512)
            {
                stream.Write(new byte[padding], 0, padding);
            }
        }

        private static void WriteString(byte[] buffer, int offset, int length, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length));
        }

        private static void WriteOctal(byte[] buffer, int offset, int length, long value)
        {
            var octal = Convert.ToString(value, 8) ?? "0";
            octal = octal.PadLeft(length - 1, '0');
            var bytes = Encoding.ASCII.GetBytes(octal);
            Array.Copy(bytes, 0, buffer, offset, Math.Min(bytes.Length, length - 1));
            buffer[offset + length - 1] = 0;
        }
    }
}
}
