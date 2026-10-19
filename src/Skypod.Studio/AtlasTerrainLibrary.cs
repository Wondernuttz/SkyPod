using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Skypod.Studio;

/// <summary>Local terrain imagery; routing always remains in the world index.</summary>
internal sealed class AtlasTerrainLibrary
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skypod", "AtlasTerrain");
    private static string SettingsPath => Path.Combine(DefaultDirectory, "library-path.txt");
    private readonly Dictionary<string, AtlasTerrainRegion> _regions;
    public string DirectoryPath { get; }

    private AtlasTerrainLibrary(string path, AtlasTerrainRegion[] regions)
    {
        DirectoryPath = path;
        _regions = regions.GroupBy(region => region.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public AtlasTerrainRegion? Find(string editorId) => _regions.GetValueOrDefault(editorId);

    public static AtlasTerrainLibrary Open(string directory)
    {
        directory = Path.GetFullPath(directory);
        var file = Path.Combine(directory, "catalog.json");
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new InvalidDataException("Terrain catalog is too large.");
        var regions = JsonSerializer.Deserialize<AtlasTerrainRegion[]>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var region in regions)
        {
            if (region is null || string.IsNullOrWhiteSpace(region.Id) || region.Bounds is null || region.Tiles is null || region.Overlays is null || region.Bounds.Length != 4 ||
                region.Bounds.Any(value => !double.IsFinite(value)) ||
                region.Bounds[2] <= region.Bounds[0] || region.Bounds[3] <= region.Bounds[1])
                throw new InvalidDataException("Terrain catalog contains invalid map bounds.");
            foreach (var tile in region.Tiles.Concat(region.Overlays))
            {
                if (tile is null || tile.Level is not (4 or 8 or 16 or 32) || !double.IsFinite(tile.X) || !double.IsFinite(tile.Y))
                    throw new InvalidDataException("Terrain catalog contains an invalid tile.");
                tile.ColorPath = ResolveImage(directory, tile.Color);
                tile.NormalPath = string.IsNullOrWhiteSpace(tile.Normal) ? null : ResolveImage(directory, tile.Normal);
                tile.WaterPath = string.IsNullOrWhiteSpace(tile.Water) ? null : ResolveImage(directory, tile.Water);
                if (tile.Bounds is { } bounds && (bounds.Length != 4 || bounds.Any(v => !double.IsFinite(v)) || bounds[2] <= bounds[0] || bounds[3] <= bounds[1]))
                    throw new InvalidDataException("Invalid overlay bounds.");
            }
        }
        return new AtlasTerrainLibrary(directory, regions);
    }

    public static AtlasTerrainLibrary? TryOpenDefault(out string? error)
    {
        error = null;
        try
        {
            var portable = Path.Combine(AppContext.BaseDirectory, "AtlasTerrain");
            var path = File.Exists(Path.Combine(portable, "catalog.json")) ? portable
                : File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : DefaultDirectory;
            return File.Exists(Path.Combine(path, "catalog.json")) ? Open(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            error = ex.Message;
            return null;
        }
    }

    public void Remember()
    {
        Directory.CreateDirectory(DefaultDirectory);
        File.WriteAllText(SettingsPath, DirectoryPath);
    }

    private static string ResolveImage(string root, string relative)
    {
        var resolved = Path.GetFullPath(Path.Combine(root, relative));
        if (!resolved.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(resolved).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Terrain images must be PNG files inside the terrain library.");
        return resolved;
    }

    public static BitmapSource ReadTile(AtlasTerrainTile tile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        static BitmapSource Read(string path)
        {
            using var stream = File.OpenRead(path);
            var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if ((long)image.PixelWidth * image.PixelHeight > 16_777_216)
                throw new InvalidDataException("Terrain tile exceeds the image size limit.");
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            return converted;
        }

        var color = Read(tile.ColorPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (tile.NormalPath is null && tile.WaterPath is null) return color;
        var normal = tile.NormalPath is null ? null : Read(tile.NormalPath);
        var water = tile.WaterPath is null ? null : Read(tile.WaterPath);
        var pixels = new byte[color.PixelWidth * color.PixelHeight * 4];
        var normals = normal is null ? [] : new byte[normal.PixelWidth * normal.PixelHeight * 4];
        var waters = water is null ? [] : new byte[water.PixelWidth * water.PixelHeight * 4];
        color.CopyPixels(pixels, color.PixelWidth * 4, 0);
        normal?.CopyPixels(normals, normal.PixelWidth * 4, 0);
        water?.CopyPixels(waters, water.PixelWidth * 4, 0);
        for (var y = 0; y < color.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ny = normal is null ? 0 : y * normal.PixelHeight / color.PixelHeight;
            for (var x = 0; x < color.PixelWidth; x++)
            {
                var n = normal is null ? 0 : (ny * normal.PixelWidth + x * normal.PixelWidth / color.PixelWidth) * 4;
                var nx = normal is null ? 0 : normals[n + 2] / 127.5 - 1;
                var up = normal is null ? 1 : normals[n + 1] / 127.5 - 1;
                var nz = normal is null ? 0 : normals[n] / 127.5 - 1;
                var length = Math.Max(.01, Math.Sqrt(nx * nx + up * up + nz * nz));
                var diffuse = (-.42 * nx + .80 * up + .43 * nz) / length;
                var factor = normal is null ? 1 : 1 + .7 * (Math.Max(.18, diffuse) * 1.5 - .85);
                var multiply = Math.Min(1, factor);
                var screen = Math.Max(0, factor - 1) * 100 / 255;
                var p = (y * color.PixelWidth + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                    pixels[p + channel] = (byte)Math.Clamp(255 - (255 - pixels[p + channel] * multiply) * (1 - screen), 0, 255);
                if (water is not null)
                {
                    var wi = (y * water.PixelHeight / color.PixelHeight * water.PixelWidth + x * water.PixelWidth / color.PixelWidth) * 4;
                    var opacity = waters[wi] / 255d;
                    pixels[p] = (byte)(pixels[p] * (1 - opacity) + 66 * opacity);
                    pixels[p + 1] = (byte)(pixels[p + 1] * (1 - opacity) + 54 * opacity);
                    pixels[p + 2] = (byte)(pixels[p + 2] * (1 - opacity) + 34 * opacity);
                }
            }
        }
        var result = BitmapSource.Create(color.PixelWidth, color.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, color.PixelWidth * 4);
        result.Freeze();
        return result;
    }
}

internal sealed class AtlasTerrainRegion
{
    public string Id { get; set; } = string.Empty;
    public double[] Bounds { get; set; } = [];
    public string Status { get; set; } = "terrain";
    public AtlasTerrainTile[] Tiles { get; set; } = [];
    public AtlasTerrainTile[] Overlays { get; set; } = [];
    public bool HasTerrain => Status == "terrain" && (Tiles.Length > 0 || Overlays.Length > 0);
}

internal sealed class AtlasTerrainTile
{
    public double X { get; set; }
    public double Y { get; set; }
    public int Level { get; set; }
    public string Color { get; set; } = string.Empty;
    public string? Normal { get; set; }
    public string? Water { get; set; }
    public double[]? Bounds { get; set; }
    public string ColorPath { get; set; } = string.Empty;
    public string? NormalPath { get; set; }
    public string? WaterPath { get; set; }
}
