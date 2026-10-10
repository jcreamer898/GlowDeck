using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace GlowDeck.Services;

public static class OpenRgbBackendInstaller
{
    public const string Version = "1.0";
    public const string OfficialReleasePage = "https://openrgb.org/releases.html";

    private const string ArchiveUrl =
        "https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.0/OpenRGB_1.0_Windows_64_81bbe18.zip";
    private const long ArchiveSize = 22_012_179;
    private const string ArchiveSha256 = "182A52A3C97C4C4AE52C80286B4260C9666C51C3447DBC416EE8945F66192E90";
    private const long MaximumExpandedSize = 256L * 1024 * 1024;
    private const string ExecutableArchivePath = "OpenRGB Windows 64-bit/OpenRGB.exe";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlowDeck",
        "Backends",
        "OpenRGB-1.0");

    public static string PortableExecutablePath =>
        Path.Combine(InstallDirectory, "OpenRGB Windows 64-bit", "OpenRGB.exe");

    public static async Task<string> InstallPortableAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallGate.WaitAsync(cancellationToken);
        string? stagingDirectory = null;
        try
        {
            if (File.Exists(PortableExecutablePath))
            {
                return PortableExecutablePath;
            }

            if (Directory.Exists(InstallDirectory))
            {
                throw new InvalidDataException(
                    $"The OpenRGB backend folder exists but is incomplete: {InstallDirectory}");
            }

            var backendDirectory = Path.GetDirectoryName(InstallDirectory)
                ?? throw new InvalidOperationException("The OpenRGB backend folder has no parent directory.");
            Directory.CreateDirectory(backendDirectory);

            stagingDirectory = Path.Combine(
                backendDirectory,
                $".openrgb-install-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);

            var archivePath = Path.Combine(stagingDirectory, "OpenRGB-1.0-Windows-x64.zip");
            await DownloadArchiveAsync(archivePath, progress, cancellationToken);

            var extractionDirectory = Path.Combine(stagingDirectory, "extracted");
            Directory.CreateDirectory(extractionDirectory);
            ValidateArchive(archivePath, extractionDirectory);
            ZipFile.ExtractToDirectory(archivePath, extractionDirectory);

            var extractedExecutable = Path.Combine(
                extractionDirectory,
                "OpenRGB Windows 64-bit",
                "OpenRGB.exe");
            if (!File.Exists(extractedExecutable))
            {
                throw new InvalidDataException("The OpenRGB release archive did not contain its expected executable.");
            }

            if (new FileInfo(extractedExecutable).Length == 0)
            {
                throw new InvalidDataException("The OpenRGB executable in the release archive is empty.");
            }

            Directory.Move(extractionDirectory, InstallDirectory);
            progress?.Report(1);
            return PortableExecutablePath;
        }
        finally
        {
            try
            {
                if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            finally
            {
                InstallGate.Release();
            }
        }
    }

    private static async Task DownloadArchiveAsync(
        string archivePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var downloadToken = timeout.Token;

        using var response = await HttpClient.GetAsync(
            ArchiveUrl,
            HttpCompletionOption.ResponseHeadersRead,
            downloadToken);
        response.EnsureSuccessStatusCode();

        var downloadUri = response.RequestMessage?.RequestUri;
        if (downloadUri is null ||
            downloadUri.Scheme != Uri.UriSchemeHttps ||
            !downloadUri.Host.Equals("codeberg.org", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("The OpenRGB download did not come from the official Codeberg release host.");
        }

        if (response.Content.Headers.ContentLength is long contentLength && contentLength != ArchiveSize)
        {
            throw new InvalidDataException(
                $"The OpenRGB archive size was unexpected: {contentLength} bytes.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(downloadToken);
        await using var output = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long totalRead = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), downloadToken);
            if (read == 0)
            {
                break;
            }

            totalRead = checked(totalRead + read);
            if (totalRead > ArchiveSize)
            {
                throw new InvalidDataException("The OpenRGB archive exceeded its expected download size.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), downloadToken);
            progress?.Report(Math.Min(0.9, (double)totalRead / ArchiveSize * 0.9));
        }

        await output.FlushAsync(downloadToken);
        if (totalRead != ArchiveSize)
        {
            throw new InvalidDataException(
                $"The OpenRGB archive was incomplete: expected {ArchiveSize} bytes, received {totalRead}.");
        }

        var actualSha256 = Convert.ToHexString(hash.GetHashAndReset());
        if (!actualSha256.Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CryptographicException("The OpenRGB archive did not match the verified release checksum.");
        }
    }

    private static void ValidateArchive(string archivePath, string extractionDirectory)
    {
        var root = Path.GetFullPath(extractionDirectory) + Path.DirectorySeparatorChar;
        var containsExecutable = false;
        long expandedSize = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 2_000)
        {
            throw new InvalidDataException("The OpenRGB archive contains an unreasonable number of files.");
        }

        foreach (var entry in archive.Entries)
        {
            var normalizedName = entry.FullName.Replace('\\', '/');
            if (Path.IsPathRooted(entry.FullName) ||
                normalizedName.StartsWith("/", StringComparison.Ordinal) ||
                normalizedName.Split('/').Any(part => part is "." or ".."))
            {
                throw new InvalidDataException("The OpenRGB archive contains an unsafe file path.");
            }

            var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (fileType == 0xA000)
            {
                throw new InvalidDataException("The OpenRGB archive contains a symbolic link.");
            }

            var destination = Path.GetFullPath(Path.Combine(
                extractionDirectory,
                normalizedName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The OpenRGB archive contains a file outside its extraction folder.");
            }

            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > MaximumExpandedSize)
            {
                throw new InvalidDataException("The OpenRGB archive expands beyond the allowed size.");
            }

            containsExecutable |= normalizedName.Equals(
                ExecutableArchivePath,
                StringComparison.OrdinalIgnoreCase);
        }

        if (!containsExecutable)
        {
            throw new InvalidDataException("The OpenRGB archive did not contain its expected executable.");
        }
    }
}
