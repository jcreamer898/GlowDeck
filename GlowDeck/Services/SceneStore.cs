using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GlowDeck.Models;

namespace GlowDeck.Services;

public sealed class SceneStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public SceneStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GlowDeck",
            "scenes.json");
    }

    public static IReadOnlyList<LightingScene> BuiltInScenes =>
    [
        new()
        {
            Id = "built-in-arctic",
            Name = "Arctic glow",
            Effect = SceneEffect.Breathing,
            Colors = [new(77, 190, 255), new(198, 240, 255)],
            Speed = 32,
            Brightness = 70,
            IsBuiltIn = true
        },
        new()
        {
            Id = "built-in-sunset",
            Name = "Sunset",
            Effect = SceneEffect.ColorCycle,
            Colors = [new(255, 93, 76), new(255, 170, 70), new(180, 76, 190)],
            Speed = 28,
            Brightness = 76,
            IsBuiltIn = true
        },
        new()
        {
            Id = "built-in-aurora",
            Name = "Aurora",
            Effect = SceneEffect.Rainbow,
            Colors = [new(75, 230, 190), new(127, 111, 255)],
            Speed = 24,
            Brightness = 65,
            IsBuiltIn = true
        },
        new()
        {
            Id = "built-in-warm-white",
            Name = "Warm white",
            Effect = SceneEffect.Solid,
            Colors = [new(255, 178, 112)],
            Speed = 45,
            Brightness = 48,
            IsBuiltIn = true
        }
    ];

    public async Task<IReadOnlyList<LightingScene>> LoadCustomScenesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var scenes = await JsonSerializer.DeserializeAsync<List<LightingScene>>(
                stream,
                SerializerOptions,
                cancellationToken);
            return scenes ?? throw new InvalidDataException("The saved scenes file does not contain a scene list.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The saved scenes file is not valid JSON: {_filePath}", exception);
        }
    }

    public async Task SaveCustomScenesAsync(
        IEnumerable<LightingScene> scenes,
        CancellationToken cancellationToken = default)
    {
        var customScenes = scenes.Where(scene => !scene.IsBuiltIn).ToList();
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("The scene file path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, customScenes, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, _filePath, true);
    }
}
