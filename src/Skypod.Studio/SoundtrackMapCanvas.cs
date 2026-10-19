using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Skypod.Core;

namespace Skypod.Studio;

/// <summary>
/// Terrain imagery and load-order location geometry share one world-coordinate projection.
/// </summary>
public sealed class SoundtrackMapCanvas : FrameworkElement
{
    private readonly List<(Rect Bounds, SoundtrackPlace Place)> _hitTargets = [];
    private readonly List<(Rect Bounds, SoundtrackPlace Place)> _labelHitTargets = [];
    private readonly HashSet<string> _labelledPlaceIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownTownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dawnstar", "Dragon Bridge", "Falkreath", "Helgen", "Ivarstead", "Karthwasten",
        "Morthal", "Riverwood", "Rorikstead", "Shor's Stone", "Winterhold",
        "Raven Rock", "Skaal Village"
    };
    private static readonly HashSet<string> KnownMajorCityNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Solitude", "Markarth", "Whiterun", "Windhelm", "Riften"
    };
    private static readonly HashSet<string> KnownHoldCapitalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Solitude", "Markarth", "Whiterun", "Windhelm", "Riften",
        "Dawnstar", "Falkreath", "Morthal", "Winterhold"
    };
    private SoundtrackWorldMap? _map;
    private IReadOnlyList<SoundtrackPlace> _places = [];
    private IReadOnlySet<string> _selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _selectedRootIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Color AssignmentGold = Color.FromRgb(241, 200, 109);
    private static readonly Color ViewingColor = Color.FromRgb(224, 222, 234);
    private string? _focusedRootId;
    private SoundtrackPlace? _hovered;
    private double _zoom = 1;
    private Vector _pan;
    private Point? _dragStart;
    private Vector _panStart;
    private double _viewMinX;
    private double _viewMinY;
    private double _viewMaxX = 1;
    private double _viewMaxY = 1;

    private AtlasTerrainLibrary? _terrainLibrary;
    private AtlasTerrainRegion? _terrain;
    private CancellationTokenSource _terrainCancellation = new();
    private readonly SemaphoreSlim _decodeSlots = new(4);
    private readonly Dictionary<string, (BitmapSource Image, long Used)> _tileCache = [];
    private readonly HashSet<string> _loadingTiles = [];
    private readonly HashSet<string> _failedTiles = [];
    private long _renderStamp;
    private long _cachedPixels;
    private bool _showHoldBoundaries = true;
    private CityMapOverview? _cityOverview;
    internal IReadOnlyList<CityMapOverview> CityOverviews { get; set; } = [];
    private bool _usesParentTerrain;
    public event EventHandler? ViewChanged;
    public sealed class ZoomRequest(Point anchor, double factor) : EventArgs
    {
        public Point Anchor { get; } = anchor;
        public double Factor { get; } = factor;
        public bool Handled { get; set; }
    }
    public event EventHandler<ZoomRequest>? ZoomRequested;
    private double MinimumZoom => _cityOverview is null ? 1 : .35;
    private double MaximumZoom => _cityOverview is null ? 128 : 10;
    internal double PixelsPerWorldUnit => BaseScale * _zoom;
    private double BaseScale => Math.Abs(ProjectUntransformed(1, 0).X - ProjectUntransformed(0, 0).X);
    internal Point WorldToScreen(double x, double y) => TransformToScreen(ProjectUntransformed(x, y));
    internal Point ScreenToWorld(Point point)
    {
        var origin = WorldToScreen(0, 0);
        return new Point((point.X - origin.X) / PixelsPerWorldUnit, (origin.Y - point.Y) / PixelsPerWorldUnit);
    }
    internal double CityFitScale(CityMapOverview city)
    {
        var terrain = _terrainLibrary?.Find(city.Map.EditorId) ??
            (city.Map.TerrainParentEditorId is { } parent ? _terrainLibrary?.Find(parent) : null);
        var padding = terrain?.HasTerrain == true ? 8 : Math.Min(42, Math.Max(18, Math.Min(ActualWidth, ActualHeight) * .045));
        return Math.Min(Math.Max(1, ActualWidth - padding * 2) / (city.MaxX - city.MinX),
            Math.Max(1, ActualHeight - padding * 2) / (city.MaxY - city.MinY));
    }
    internal void MatchScaleAt(Point world, double pixelsPerUnit, Point anchor)
    {
        _zoom = Math.Clamp(pixelsPerUnit / BaseScale, MinimumZoom, MaximumZoom);
        var projected = ProjectUntransformed(world.X, world.Y);
        _pan = new Vector(anchor.X - projected.X * _zoom, anchor.Y - projected.Y * _zoom);
        ClampPan();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public string TerrainStatus => _terrain?.HasTerrain != true ? "Location diagram · no overhead image" :
        _failedTiles.Count > 0 ? "Some terrain images could not load" :
        _loadingTiles.Count > 0 ? "Loading terrain detail…" :
        _terrain?.Overlays.Length > 0 ? "Overhead map · game geometry" :
        _cityOverview is not null ? "Building entrances · terrain background" : "Terrain view";
    public bool ShowHoldBoundaries
    {
        get => _showHoldBoundaries;
        set { _showHoldBoundaries = value; InvalidateVisual(); }
    }
    internal void SetTerrainLibrary(AtlasTerrainLibrary? library)
    {
        _terrainLibrary = library;
        _tileCache.Clear();
        _cachedPixels = 0;
        ChangeTerrain();
        ResetView();
        InvalidateVisual();
    }
    public void ReleaseTerrain()
    {
        _terrainCancellation.Cancel();
        _tileCache.Clear();
        _cachedPixels = 0;
    }
    private void ChangeTerrain()
    {
        _terrainCancellation.Cancel();
        _terrainCancellation.Dispose();
        _terrainCancellation = new CancellationTokenSource();
        _terrain = _map is null ? null : _terrainLibrary?.Find(_map.EditorId);
        _usesParentTerrain = _terrain?.HasTerrain != true && _map?.TerrainParentEditorId is not null;
        if (_usesParentTerrain) _terrain = _terrainLibrary?.Find(_map!.TerrainParentEditorId!);
        _loadingTiles.Clear();
        _failedTiles.Clear();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public void FitMap() { ResetView(); InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty); }
    public sealed record ViewState(double MinX, double MinY, double MaxX, double MaxY, double Zoom, Vector Pan);
    public ViewState CaptureView() => new(_viewMinX, _viewMinY, _viewMaxX, _viewMaxY, _zoom, _pan);
    public void RestoreView(ViewState view)
    {
        (_viewMinX, _viewMinY, _viewMaxX, _viewMaxY, _zoom, _pan) =
            (view.MinX, view.MinY, view.MaxX, view.MaxY, view.Zoom, view.Pan);
        ClampPan(); InvalidateVisual(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ShowCity(CityMapOverview overview)
    {
        Map = overview.Map;
        _cityOverview = overview;
        Places = overview.MapPlaces;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    public void ZoomBy(double factor)
    {
        ZoomAt(new Point(ActualWidth / 2, ActualHeight / 2), factor);
    }
    private void ZoomAt(Point anchor, double factor)
    {
        if (_map is null || !double.IsFinite(factor) || factor <= 0 || factor == 1) return;
        var request = new ZoomRequest(anchor, factor);
        ZoomRequested?.Invoke(this, request);
        if (request.Handled) return;
        var oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * factor, MinimumZoom, MaximumZoom);
        var logical = new Point((anchor.X - _pan.X) / oldZoom, (anchor.Y - _pan.Y) / oldZoom);
        _pan = new Vector(anchor.X - logical.X * _zoom, anchor.Y - logical.Y * _zoom);
        ClampPan();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler<SoundtrackPlace>? PlaceActivated;
    public event EventHandler<SoundtrackPlace?>? PlaceHovered;

    public SoundtrackWorldMap? Map
    {
        get => _map;
        set
        {
            _map = value;
            _cityOverview = null;
            ChangeTerrain();
            _hovered = null;
            ResetView();
            InvalidateVisual();
        }
    }

    public IReadOnlyList<SoundtrackPlace> Places
    {
        get => _places;
        set
        {
            _places = value ?? [];
            ResetView();
            InvalidateVisual();
        }
    }

    public IReadOnlySet<string> SelectedIds
    {
        get => _selectedIds;
        set
        {
            _selectedIds = value ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            InvalidateVisual();
        }
    }

    public string? FocusedRootId
    {
        get => _focusedRootId;
        set
        {
            _focusedRootId = value;
            InvalidateVisual();
        }
    }

    internal IReadOnlySet<string> SelectedRootIds
    {
        get => _selectedRootIds;
        set { _selectedRootIds = value; InvalidateVisual(); }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, MinimumZoom, MaximumZoom);
            ClampPan();
            InvalidateVisual();
        }
    }

    public void CenterOn(SoundtrackPlace place)
    {
        if (_map is null || place.Map is null) return;
        var point = ProjectUntransformed(place.Map.X, place.Map.Y);
        _zoom = Math.Max(_zoom, 3);
        _pan = new Vector(
            ActualWidth / 2 - point.X * _zoom,
            ActualHeight / 2 - point.Y * _zoom);
        ClampPan();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _hitTargets.Clear();
        _labelHitTargets.Clear();
        _labelledPlaceIds.Clear();
        DrawBackground(drawingContext);
        if (_map is null)
        {
            DrawCenteredText(drawingContext, "No map available for this location.", 16, Brushes.White);
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 14, 14));
        drawingContext.PushTransform(new TranslateTransform(_pan.X, _pan.Y));
        drawingContext.PushTransform(new ScaleTransform(_zoom, _zoom));
        DrawTerrain(drawingContext);
        DrawAreas(drawingContext);
        DrawPins(drawingContext);
        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();

        DrawCompass(drawingContext);
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DrawBackground(DrawingContext dc)
    {
        var palette = PaletteFor(_map?.Style ?? SoundtrackMapStyle.Skyrim);
        dc.DrawRoundedRectangle(
            _terrain?.Tiles.Any(tile => tile.WaterPath is not null) == true
                ? new SolidColorBrush(Color.FromRgb(34, 54, 66))
                : new LinearGradientBrush(palette.BackgroundTop, palette.BackgroundBottom, 90),
            new Pen(new SolidColorBrush(palette.Border), 1.4),
            new Rect(0.7, 0.7, Math.Max(0, ActualWidth - 1.4), Math.Max(0, ActualHeight - 1.4)),
            14,
            14);
    }

    private void DrawTerrain(DrawingContext dc)
    {
        if (_terrain?.HasTerrain != true) return;
        _renderStamp++;
        var levels = _terrain.Tiles.Select(tile => tile.Level).Distinct().OrderDescending().DefaultIfEmpty(32).ToArray();
        var detail = levels[0];
        foreach (var level in levels)
        {
            detail = level;
            var size = Math.Abs(Project(level * 4096, 0).X - Project(0, 0).X) * _zoom;
            if (size <= 480) break;
        }
        // Coarse imagery stays underneath detail so zooming never exposes unloaded holes.
        var viewport = new Rect(-_pan.X / _zoom, -_pan.Y / _zoom, ActualWidth / _zoom, ActualHeight / _zoom);
        var bounds = new Rect(Project(_terrain.Bounds[0], _terrain.Bounds[3]), Project(_terrain.Bounds[2], _terrain.Bounds[1]));
        var terrainClip = _cityOverview is null ? bounds : viewport;
        dc.PushClip(new RectangleGeometry(terrainClip));
        foreach (var tile in _terrain.Tiles.Where(tile => tile.Level == levels[0] || tile.Level == detail)
                     .OrderByDescending(tile => tile.Level).Concat(_terrain.Overlays).Concat(ParentCityOverlays()))
        {
            var rect = tile.Bounds is { } area
                ? new Rect(Project(area[0], area[3]), Project(area[2], area[1]))
                : new Rect(Project(tile.X * 4096, (tile.Y + tile.Level) * 4096),
                    Project((tile.X + tile.Level) * 4096, tile.Y * 4096));
            if (!rect.IntersectsWith(viewport) || !rect.IntersectsWith(terrainClip)) continue;
            if (_tileCache.TryGetValue(tile.ColorPath, out var cached))
            {
                _tileCache[tile.ColorPath] = (cached.Image, _renderStamp);
                var fade = _cityOverview is null && tile.Bounds is not null ? Math.Clamp((rect.Width * _zoom - 80) / 100, 0, 1) : 1;
                dc.PushOpacity(fade);
                dc.DrawImage(cached.Image, rect);
                dc.Pop();
            }
            else if (_loadingTiles.Count < 4 && !_loadingTiles.Contains(tile.ColorPath) && !_failedTiles.Contains(tile.ColorPath))
            {
                _ = LoadTileAsync(tile, _terrainCancellation.Token);
            }
        }
        dc.Pop();
        // Bound decoded texture memory, retaining the tiles used in the current frame.
        foreach (var entry in _tileCache.OrderBy(pair => pair.Value.Used).ToArray())
        {
            if (_cachedPixels <= 80_000_000 || entry.Value.Used == _renderStamp) break;
            _cachedPixels -= (long)entry.Value.Image.PixelWidth * entry.Value.Image.PixelHeight;
            _tileCache.Remove(entry.Key);
        }
    }

    private IEnumerable<AtlasTerrainTile> ParentCityOverlays()
    {
        if (_cityOverview is not null || _map is null || _terrainLibrary is null) yield break;
        foreach (var city in CityOverviews)
        {
            if (city.City.Map is not { } pin || !pin.WorldspaceFormKey.Equals(_map.FormKey, StringComparison.OrdinalIgnoreCase) ||
                city.Map.FormKey.Equals(_map.FormKey, StringComparison.OrdinalIgnoreCase)) continue;
            var sharesCoordinates = city.Map.TerrainParentEditorId?.Equals(_map.EditorId, StringComparison.OrdinalIgnoreCase) == true ||
                city.Map.ParentWorldspaceFormKey?.Equals(_map.FormKey, StringComparison.OrdinalIgnoreCase) == true &&
                pin.X >= city.MinX - 4096 && pin.X <= city.MaxX + 4096 && pin.Y >= city.MinY - 4096 && pin.Y <= city.MaxY + 4096;
            if (!sharesCoordinates || _terrainLibrary.Find(city.Map.EditorId) is not { HasTerrain: true } terrain) continue;
            foreach (var overlay in terrain.Overlays)
                if (overlay.Bounds is { } bounds && (bounds[2] - bounds[0]) * PixelsPerWorldUnit > 80)
                    yield return overlay;
        }
    }

    private async Task LoadTileAsync(AtlasTerrainTile tile, CancellationToken cancellationToken)
    {
        _loadingTiles.Add(tile.ColorPath);
        var hasSlot = false;
        try
        {
            await _decodeSlots.WaitAsync(cancellationToken);
            hasSlot = true;
            var image = await Task.Run(() => AtlasTerrainLibrary.ReadTile(tile, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            _tileCache[tile.ColorPath] = (image, _renderStamp);
            _cachedPixels += (long)image.PixelWidth * image.PixelHeight;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or
                                   System.IO.FileFormatException or ArgumentException)
        {
            if (!cancellationToken.IsCancellationRequested) _failedTiles.Add(tile.ColorPath);
        }
        finally
        {
            if (hasSlot) _decodeSlots.Release();
            if (!cancellationToken.IsCancellationRequested)
            {
                _loadingTiles.Remove(tile.ColorPath);
                InvalidateVisual();
                ViewChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void DrawTopography(DrawingContext dc)
    {
        if (_map is null) return;
        var palette = PaletteFor(_map.Style);
        var width = ActualWidth;
        var height = ActualHeight;
        var seed = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(_map.FormKey));

        // Layered contour lines create a readable original parchment/realm
        // surface without pretending to be literal terrain height data.
        for (var layer = 0; layer < 13; layer++)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var yBase = height * (0.08 + layer * 0.07);
                context.BeginFigure(new Point(-20, yBase), false, false);
                for (var x = -20d; x <= width + 20; x += 18)
                {
                    var wave = Math.Sin((x + seed % 97) * 0.010 + layer * 0.83) * (10 + layer % 3 * 4) +
                               Math.Sin((x + seed % 53) * 0.025 + layer * 1.37) * 5;
                    context.LineTo(new Point(x, yBase + wave), true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(palette.Contour), 0.7 / _zoom), geometry);
        }

        DrawRealmArt(dc, palette, seed, width, height);

        if (_map.Style is SoundtrackMapStyle.Apocrypha or SoundtrackMapStyle.SoulCairn or SoundtrackMapStyle.Sovngarde)
        {
            var starBrush = new SolidColorBrush(palette.Accent) { Opacity = 0.34 };
            for (var i = 0; i < 70; i++)
            {
                var x = ((seed * 31L + i * 127L) % 1000) / 1000d * width;
                var y = ((seed * 17L + i * 211L) % 1000) / 1000d * height;
                var radius = 0.7 + i % 4 * 0.35;
                dc.DrawEllipse(starBrush, null, new Point(x, y), radius / _zoom, radius / _zoom);
            }
        }
    }

    private void DrawAreas(DrawingContext dc)
    {
        if (_map is null) return;
        var palette = PaletteFor(_map.Style);
        DrawHoldTerritories(dc, palette);
        if (_showHoldBoundaries)
        {
            DrawSolstheimTerritories(dc, palette);
        }
        var areaPlaces = _places
            .Where(place => place.RootPlaceId is null)
            .Where(place => place.Map?.Areas.Count > 0)
            .Where(place => !IsGroup(place, "hold"))
            .Where(place => IsPlaceFocused(place) || IsLogicalPlaceSelected(place) ||
                            ReferenceEquals(_hovered, place))
            .OrderBy(place => IsLogicalPlaceSelected(place))
            .TakeLast(120)
            .ToArray();

        foreach (var place in areaPlaces)
        {
            var selected = IsLogicalPlaceSelected(place);
            var focused = IsPlaceFocused(place);
            var fill = new SolidColorBrush(selected ? AssignmentGold : focused ? ViewingColor : palette.RegionFill)
            {
                Opacity = selected ? 0.24 : focused ? 0.06 : 0.04
            };
            var stroke = new Pen(
                new SolidColorBrush(selected ? AssignmentGold : focused ? ViewingColor : palette.RegionStroke)
                {
                    Opacity = selected || focused ? 1 : 0.65
                },
                (selected ? 2.2 : 0.9) / _zoom);
            if (focused && !selected) stroke.DashStyle = DashStyles.Dash;
            var geometry = BuildOverviewBoundary(place.Map!.Areas);
            if (geometry is not null) dc.DrawGeometry(fill, stroke, geometry);
        }
    }

    private void DrawHoldTerritories(DrawingContext dc, MapPalette palette)
    {
        var holds = _places
            .Where(place => place.RootPlaceId is null && IsGroup(place, "hold") && place.Map is not null)
            .GroupBy(place => IsPlaceFocused(place) || IsLogicalPlaceSelected(place) ? place.Id :
                $"{(IsGroup(place, "hold") ? "hold:" : "place:")}{place.DisplayName}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var partitions = BuildTerritoryPartitions(holds);
        foreach (var hold in holds)
        {
            if (!partitions.TryGetValue(hold.Id, out var territory)) continue;
            var selected = IsLogicalPlaceSelected(hold);
            var focused = IsPlaceFocused(hold);
            if (!_showHoldBoundaries && !selected && !focused) continue;
            var holdColor = HoldColor(hold);
            var fill = new SolidColorBrush(selected ? AssignmentGold : holdColor)
            {
                Opacity = selected ? 0.28 : 0.115
            };
            var borderBrush = new SolidColorBrush(selected ? AssignmentGold : focused ? ViewingColor : holdColor)
            {
                Opacity = selected || focused ? 1 : 0.82
            };
            var border = new Pen(borderBrush, (selected ? 2.4 : 1.1) / _zoom);
            if (focused && !selected) border.DashStyle = DashStyles.Dash;
            dc.DrawGeometry(fill, border, territory);
        }
    }

    private void DrawSolstheimTerritories(DrawingContext dc, MapPalette palette)
    {
        if (_map?.Style != SoundtrackMapStyle.Solstheim) return;
        var regions = SolstheimTerritoryPlaces(_places);
        var islandAnchors = _places
            .Where(place => place.RootPlaceId is null && place.Map is not null)
            .Where(place => place.Kind != SoundtrackPlaceKind.Worldspace && !IsGroup(place, "regionBiome"))
            .Where(place => place.Scope is SoundtrackPlaceScope.WholeLocation or SoundtrackPlaceScope.MapMarker)
            .ToArray();
        var partitions = BuildTerritoryPartitions(regions, islandAnchors);
        foreach (var region in regions)
        {
            if (!partitions.TryGetValue(region.Id, out var territory)) continue;
            var selected = IsLogicalPlaceSelected(region);
            var focused = IsPlaceFocused(region);
            var color = SolstheimRegionColor(region);
            var fill = new SolidColorBrush(selected ? AssignmentGold : color)
            {
                Opacity = selected ? 0.28 : 0.13
            };
            var border = new SolidColorBrush(selected ? AssignmentGold : focused ? ViewingColor : color)
            {
                Opacity = selected || focused ? 1 : 0.86
            };
            var stroke = new Pen(border, (selected ? 2.4 : 1.1) / _zoom);
            if (focused && !selected) stroke.DashStyle = DashStyles.Dash;
            dc.DrawGeometry(fill, stroke, territory);
        }
    }

    private Dictionary<string, StreamGeometry> BuildTerritoryPartitions(
        IReadOnlyList<SoundtrackPlace> places,
        IReadOnlyList<SoundtrackPlace>? silhouettePlaces = null)
    {
        const double cellSize = 4096;
        var sites = places
            .Where(place => place.Map is not null)
            .Select(place => new TerritorySite(
                place,
                place.Map!.X / cellSize,
                place.Map.Y / cellSize,
                ExtractAreaCells(place.Map.Areas, cellSize)))
            .ToArray();
        if (sites.Length == 0) return [];

        var silhouettePoints = new List<Point>();
        if (silhouettePlaces is { Count: >= 3 })
        {
            // Solstheim weather regions have very broad rectangular records. Build the
            // visible island envelope from real mapped locations instead, with one cell
            // of coastline breathing room, then divide that common silhouette by region.
            foreach (var place in silhouettePlaces.Where(place => place.Map is not null))
            {
                var x = place.Map!.X / cellSize;
                var y = place.Map.Y / cellSize;
                const double margin = 1.15;
                silhouettePoints.Add(new Point(x - margin, y - margin));
                silhouettePoints.Add(new Point(x + margin, y - margin));
                silhouettePoints.Add(new Point(x + margin, y + margin));
                silhouettePoints.Add(new Point(x - margin, y + margin));
            }
        }
        else foreach (var site in sites)
        {
            // Keep geographically plausible cells near each hold's own anchor.
            // Mod-added inheritance can otherwise contribute distant islands.
            var nearby = site.SeedCells
                .Where(cell => DistanceSquared(cell.X + 0.5, cell.Y + 0.5, site.X, site.Y) <= 24 * 24)
                .ToArray();
            if (nearby.Length == 0) nearby = [(X: (int)Math.Floor(site.X), Y: (int)Math.Floor(site.Y))];
            foreach (var cell in nearby)
            {
                silhouettePoints.Add(new Point(cell.X, cell.Y));
                silhouettePoints.Add(new Point(cell.X + 1, cell.Y));
                silhouettePoints.Add(new Point(cell.X + 1, cell.Y + 1));
                silhouettePoints.Add(new Point(cell.X, cell.Y + 1));
            }
        }
        var hull = ConvexHull(silhouettePoints);
        if (hull.Count < 3) return [];

        var minX = (int)Math.Floor(hull.Min(point => point.X));
        var maxX = (int)Math.Ceiling(hull.Max(point => point.X));
        var minY = (int)Math.Floor(hull.Min(point => point.Y));
        var maxY = (int)Math.Ceiling(hull.Max(point => point.Y));
        var ownership = sites.ToDictionary(
            site => site.Place.Id,
            _ => new HashSet<(int X, int Y)>(),
            StringComparer.OrdinalIgnoreCase);
        for (var x = minX; x < maxX; x++)
        {
            for (var y = minY; y < maxY; y++)
            {
                if (!ContainsPoint(hull, x + 0.5, y + 0.5)) continue;
                var owner = sites.MinBy(site => DistanceSquared(x + 0.5, y + 0.5, site.X, site.Y));
                if (owner is not null) ownership[owner.Place.Id].Add((x, y));
            }
        }

        var result = new Dictionary<string, StreamGeometry>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            var loops = TraceCellBoundaries(ownership[site.Place.Id]);
            var boundary = loops.OrderByDescending(LoopArea).FirstOrDefault();
            if (boundary is null || boundary.Count < 3) continue;
            result[site.Place.Id] = ClosedGeometry(boundary
                .Select(point => Project(point.X * cellSize, point.Y * cellSize))
                .ToArray());
        }
        return result;
    }

    private static HashSet<(int X, int Y)> ExtractAreaCells(
        IReadOnlyList<SoundtrackMapPolygon> polygons,
        double cellSize)
    {
        var cells = new HashSet<(int X, int Y)>();
        foreach (var polygon in polygons.Where(polygon => polygon.Points.Count >= 3))
        {
            var minX = (int)Math.Floor(polygon.Points.Min(point => point.X) / cellSize);
            var maxX = (int)Math.Ceiling(polygon.Points.Max(point => point.X) / cellSize) - 1;
            var minY = (int)Math.Floor(polygon.Points.Min(point => point.Y) / cellSize);
            var maxY = (int)Math.Ceiling(polygon.Points.Max(point => point.Y) / cellSize) - 1;
            for (var x = minX; x <= maxX; x++)
                for (var y = minY; y <= maxY; y++)
                    if (ContainsPoint(polygon.Points, (x + 0.5) * cellSize, (y + 0.5) * cellSize))
                        cells.Add((x, y));
        }
        return cells;
    }

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    private static bool ContainsPoint(IReadOnlyList<SoundtrackMapPoint> polygon, double x, double y)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + polygon.Count - 1) % polygon.Count];
            if ((a.Y > y) == (b.Y > y)) continue;
            var crossingX = (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X;
            if (x < crossingX) inside = !inside;
        }
        return inside;
    }

    private static bool ContainsPoint(IReadOnlyList<Point> polygon, double x, double y)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + polygon.Count - 1) % polygon.Count];
            if ((a.Y > y) == (b.Y > y)) continue;
            var crossingX = (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X;
            if (x < crossingX) inside = !inside;
        }
        return inside;
    }

    private static IReadOnlyList<IReadOnlyList<(int X, int Y)>> TraceCellBoundaries(HashSet<(int X, int Y)> cells)
    {
        var outgoing = new Dictionary<(int X, int Y), List<(int X, int Y)>>();
        void AddEdge((int X, int Y) from, (int X, int Y) to)
        {
            if (!outgoing.TryGetValue(from, out var edges)) outgoing[from] = edges = [];
            edges.Add(to);
        }

        foreach (var cell in cells)
        {
            if (!cells.Contains((cell.X, cell.Y - 1))) AddEdge((cell.X, cell.Y), (cell.X + 1, cell.Y));
            if (!cells.Contains((cell.X + 1, cell.Y))) AddEdge((cell.X + 1, cell.Y), (cell.X + 1, cell.Y + 1));
            if (!cells.Contains((cell.X, cell.Y + 1))) AddEdge((cell.X + 1, cell.Y + 1), (cell.X, cell.Y + 1));
            if (!cells.Contains((cell.X - 1, cell.Y))) AddEdge((cell.X, cell.Y + 1), (cell.X, cell.Y));
        }

        var loops = new List<IReadOnlyList<(int X, int Y)>>();
        while (outgoing.Count > 0)
        {
            var start = outgoing.First().Key;
            var loop = new List<(int X, int Y)> { start };
            var current = start;
            do
            {
                if (!outgoing.TryGetValue(current, out var edges) || edges.Count == 0) break;
                var next = edges[^1];
                edges.RemoveAt(edges.Count - 1);
                if (edges.Count == 0) outgoing.Remove(current);
                current = next;
                if (current != start) loop.Add(current);
            } while (current != start && loop.Count < 10000);
            if (current == start && loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    private static double LoopArea(IReadOnlyList<(int X, int Y)> points)
    {
        var area = 0d;
        for (var i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }
        return Math.Abs(area) * 0.5;
    }

    private static StreamGeometry ClosedGeometry(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], true, true);
            foreach (var point in points.Skip(1)) context.LineTo(point, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private StreamGeometry? BuildOverviewBoundary(IReadOnlyList<SoundtrackMapPolygon> polygons)
    {
        var points = polygons
            .SelectMany(polygon => polygon.Points)
            .Select(Project)
            .Distinct()
            .ToArray();
        if (points.Length < 3) return null;

        var hull = ConvexHull(points);
        if (hull.Count < 3) return null;
        var softened = hull.Count >= 4 ? SmoothClosedPolygon(hull, 2) : hull;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(softened[0], true, true);
            foreach (var point in softened.Skip(1)) context.LineTo(point, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static IReadOnlyList<Point> ConvexHull(IReadOnlyList<Point> points)
    {
        var sorted = points.OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        if (sorted.Length <= 2) return sorted;
        var lower = new List<Point>();
        foreach (var point in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], point) <= 0) lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }
        var upper = new List<Point>();
        for (var index = sorted.Length - 1; index >= 0; index--)
        {
            var point = sorted[index];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], point) <= 0) upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }
        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    private static IReadOnlyList<Point> SmoothClosedPolygon(IReadOnlyList<Point> points, int passes)
    {
        IReadOnlyList<Point> current = points;
        for (var pass = 0; pass < passes; pass++)
        {
            var next = new List<Point>(current.Count * 2);
            for (var index = 0; index < current.Count; index++)
            {
                var a = current[index];
                var b = current[(index + 1) % current.Count];
                next.Add(new Point(a.X * 0.75 + b.X * 0.25, a.Y * 0.75 + b.Y * 0.25));
                next.Add(new Point(a.X * 0.25 + b.X * 0.75, a.Y * 0.25 + b.Y * 0.75));
            }
            current = next;
        }
        return current;
    }

    private static double Cross(Point origin, Point a, Point b) =>
        (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);

    private void DrawRealmArt(DrawingContext dc, MapPalette palette, int seed, double width, double height)
    {
        if (_map is null) return;
        if (_map.Style is SoundtrackMapStyle.Skyrim or SoundtrackMapStyle.Solstheim or SoundtrackMapStyle.ForgottenVale)
        {
            for (var layer = 0; layer < 3; layer++)
            {
                var baseline = height * (0.34 + layer * 0.11);
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(-20, baseline), true, true);
                    var peakCount = 11 + layer * 3;
                    for (var i = 0; i <= peakCount; i++)
                    {
                        var x = -20 + (width + 40) * i / peakCount;
                        var jitter = ((seed + i * 37 + layer * 71) % 31) / 31d;
                        var peak = baseline - height * (0.05 + jitter * (0.09 - layer * 0.015));
                        context.LineTo(new Point(x, peak), true, false);
                        context.LineTo(new Point(x + (width + 40) / peakCount * 0.55, baseline), true, false);
                    }
                    context.LineTo(new Point(width + 20, height * 0.63), true, false);
                    context.LineTo(new Point(-20, height * 0.63), true, false);
                }
                dc.DrawGeometry(
                    new SolidColorBrush(layer == 0 ? palette.RegionFill : palette.BackgroundBottom)
                    {
                        Opacity = 0.13 + layer * 0.07
                    },
                    new Pen(new SolidColorBrush(palette.RegionStroke) { Opacity = 0.14 }, 0.8 / _zoom),
                    geometry);
            }

            if (_map.Style == SoundtrackMapStyle.Solstheim)
            {
                var ashGlow = new RadialGradientBrush(Color.FromArgb(105, 225, 104, 52), Colors.Transparent);
                dc.DrawEllipse(ashGlow, null, new Point(width * 0.52, height * 0.28), width * 0.19, height * 0.17);
            }
            return;
        }

        if (_map.Style == SoundtrackMapStyle.Apocrypha)
        {
            for (var i = 0; i < 8; i++)
            {
                var x = width * (0.08 + i * 0.12);
                var curve = new StreamGeometry();
                using var context = curve.Open();
                context.BeginFigure(new Point(x, height * 1.05), false, false);
                context.BezierTo(
                    new Point(x - width * 0.08, height * 0.72),
                    new Point(x + width * (i % 2 == 0 ? 0.10 : -0.10), height * 0.50),
                    new Point(x + width * (i % 3 - 1) * 0.035, height * (0.19 + i % 4 * 0.045)),
                    true,
                    false);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(palette.Accent) { Opacity = 0.12 }, (10 - i % 3 * 2) / _zoom), curve);
            }
            return;
        }

        if (_map.Style == SoundtrackMapStyle.Sovngarde)
        {
            for (var i = 0; i < 5; i++)
            {
                var aurora = new StreamGeometry();
                using var context = aurora.Open();
                var y = height * (0.12 + i * 0.055);
                context.BeginFigure(new Point(-30, y), false, false);
                context.BezierTo(new Point(width * 0.24, y - 55), new Point(width * 0.55, y + 50), new Point(width + 30, y - 20), true, false);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(i % 2 == 0 ? palette.Accent : palette.SelectedFill) { Opacity = 0.16 }, (13 - i * 1.5) / _zoom), aurora);
            }
            return;
        }

        if (_map.Style == SoundtrackMapStyle.SoulCairn)
        {
            for (var i = 0; i < 18; i++)
            {
                var x = width * ((i * 71 + seed % 43) % 100) / 100d;
                var baseY = height * (0.70 + (i % 4) * 0.06);
                var crystal = new StreamGeometry();
                using var context = crystal.Open();
                context.BeginFigure(new Point(x - 7, baseY), true, true);
                context.LineTo(new Point(x, baseY - 28 - i % 5 * 8), true, false);
                context.LineTo(new Point(x + 7, baseY), true, false);
                dc.DrawGeometry(new SolidColorBrush(palette.Accent) { Opacity = 0.11 }, null, crystal);
            }
        }
    }

    private void DrawPins(DrawingContext dc)
    {
        if (_map is null) return;
        var palette = PaletteFor(_map.Style);
        var candidates = _places
            .Where(place => place.Map is not null)
            .Where(place => place.Scope is SoundtrackPlaceScope.WholeLocation or SoundtrackPlaceScope.MapMarker ||
                            place.Kind is SoundtrackPlaceKind.Region or SoundtrackPlaceKind.Worldspace)
            .GroupBy(place => place.RootPlaceId ?? place.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(Importance).First())
            .OrderByDescending(Importance)
            .ToArray();
        var solstheimRegions = SolstheimTerritoryPlaces(candidates);
        var overviewPlaces = candidates
            .Where(place => IsGroup(place, "hold") || IsCity(place) || IsSolstheimTerritory(place))
            .GroupBy(
                place => IsCity(place) ? $"city:{place.DisplayName}" :
                    IsGroup(place, "hold") ? $"hold:{place.DisplayName}" : $"region:{place.Id}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(place => IsGroup(place, "city"))
                .ThenByDescending(Importance)
                .First())
            .OrderBy(place => IsCity(place) ? 0 : 1)
            .ToArray();
        var towns = (_map.Style == SoundtrackMapStyle.Solstheim || _zoom >= 1.15)
            ? candidates
                .Where(place => IsTown(place) && !IsCity(place))
                .GroupBy(place => place.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(Importance).First())
                .ToArray()
            : [];
        var sparseRealm = candidates.Count(place => place.Kind is not (SoundtrackPlaceKind.Worldspace or SoundtrackPlaceKind.Region)) <= 24 &&
            !_map.EditorId.Equals("Tamriel", StringComparison.OrdinalIgnoreCase) && _map.Style != SoundtrackMapStyle.Solstheim;
        var onlyRegions = candidates.All(place => place.Kind is SoundtrackPlaceKind.Worldspace or SoundtrackPlaceKind.Region);
        var details = candidates
            .Where(place => !IsGroup(place, "hold") && !IsCity(place) && !IsTown(place))
            .Where(place => place.Kind != SoundtrackPlaceKind.Worldspace)
            .Where(place => !solstheimRegions.Any(region => region.Id.Equals(place.Id, StringComparison.OrdinalIgnoreCase)))
            .Where(_ => _cityOverview is not null || sparseRealm || _zoom >= 1.4)
            .Where(place => !IsGroup(place, "regionBiome") || onlyRegions || IsPlaceFocused(place) || IsLogicalPlaceSelected(place))
            .Where(place => !IsDuplicateMajorMarker(place, overviewPlaces))
            .Take(_zoom < 2.4 ? 210 : 520);
        var roots = candidates.Where(place => IsLogicalPlaceSelected(place) || IsPlaceFocused(place))
            .Concat(overviewPlaces
            .Where(IsCity)
            .Concat(overviewPlaces.Where(place => IsGroup(place, "hold")))
            .Concat(towns)
            .Concat(details))
            .Where(place => !IsGroup(place, "hold") || _showHoldBoundaries || IsLogicalPlaceSelected(place) || IsPlaceFocused(place))
            .GroupBy(place => place.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var labelRects = roots
            .Where(IsCity)
            .Select(place =>
            {
                var point = Project(place.Map!.X, place.Map.Y);
                var padding = 4 / _zoom;
                return new Rect(point.X - padding, point.Y - padding, padding * 2, padding * 2);
            })
            .ToList();

        var labelsDrawn = 0;
        var labelBudget = Math.Clamp((int)(ActualWidth * ActualHeight / (_cityOverview is null ? 14000 : 7500)), 16, 80);
        foreach (var place in roots)
        {
            var point = Project(place.Map!.X, place.Map.Y);
            var onScreen = TransformToScreen(point);
            if (onScreen.X < 8 || onScreen.X > ActualWidth - 8 || onScreen.Y < 8 || onScreen.Y > ActualHeight - 8) continue;
            var selected = IsLogicalPlaceSelected(place);
            var focused = IsPlaceFocused(place);
            var hovered = ReferenceEquals(_hovered, place);
            var major = IsMajorPlace(place) || IsSolstheimTerritory(place);
            var radius = (IsGroup(place, "hold") ? 6 : IsCity(place) ? 5 : IsTown(place) ? 4 :
                IsGroup(place, "regionBiome") ? 4 : 3) / _zoom;
            if (selected)
            {
                dc.DrawEllipse(
                    new SolidColorBrush(AssignmentGold) { Opacity = 0.16 },
                    new Pen(new SolidColorBrush(AssignmentGold), 1.6 / _zoom),
                    point,
                    radius * 2.25,
                    radius * 2.25);
            }
            else if (focused)
            {
                var edge = radius * 2.25;
                var length = 4 / _zoom;
                var pen = new Pen(new SolidColorBrush(ViewingColor), 1.1 / _zoom);
                foreach (var x in new[] { -1, 1 })
                    foreach (var y in new[] { -1, 1 })
                    {
                        var corner = new Point(point.X + x * edge, point.Y + y * edge);
                        dc.DrawLine(pen, corner, new Point(corner.X - x * length, corner.Y));
                        dc.DrawLine(pen, corner, new Point(corner.X, corner.Y - y * length));
                    }
            }
            DrawPlaceMarker(dc, place, point, radius, selected, focused, hovered, palette);
            dc.DrawEllipse(new SolidColorBrush(palette.PinCore), null, point, radius * 0.27, radius * 0.27);

            var screenPoint = TransformToScreen(point);
            _hitTargets.Add((new Rect(screenPoint.X - 12, screenPoint.Y - 12, 24, 24), place));

            var showLabel = _cityOverview is not null || selected || focused || hovered || major ||
                            sparseRealm || _zoom >= 2.0;
            if (IsGroup(place, "hold") && _zoom >= 1.15 && !selected && !focused && !hovered)
            {
                showLabel = false;
            }
            if (!showLabel || (labelsDrawn >= labelBudget && !selected && !focused && !hovered)) continue;
            var text = CreateText(
                MapLabel(place),
                (IsGroup(place, "hold") ? 15 : major ? 11.5 : 10.5) / _zoom,
                selected ? AssignmentGold : focused ? ViewingColor : palette.Label,
                IsGroup(place, "hold") ? FontWeights.Bold : FontWeights.Normal);
            var labelPoint = LabelPoint(place, point, radius, text, labelRects);
            var labelRect = new Rect(labelPoint, new Size(text.Width + 4 / _zoom, text.Height));
            labelRect.Inflate(3 / _zoom, 3 / _zoom);
            if (!selected && !focused && !hovered &&
                (labelRects.Any(existing => existing.IntersectsWith(labelRect)) || !IsLabelInViewport(labelRect))) continue;
            labelRects.Add(labelRect);
            labelsDrawn++;
            _labelledPlaceIds.Add(place.Id);
            if (_cityOverview is not null && (labelPoint - point).Length * _zoom > 24)
            {
                var end = new Point(Math.Clamp(point.X, labelRect.Left, labelRect.Right), Math.Clamp(point.Y, labelRect.Top, labelRect.Bottom));
                dc.DrawLine(new Pen(new SolidColorBrush(MarkerColor(place, palette)) { Opacity = .65 }, .8 / _zoom), point, end);
            }
            // A quiet dark plate keeps every visible label legible against terrain.
            {
                var padding = 2.5 / _zoom;
                var plate = new Rect(
                    labelPoint.X - padding,
                    labelPoint.Y - padding,
                    text.Width + padding * 2,
                    text.Height + padding * 2);
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromArgb(178, 8, 7, 12)),
                    new Pen(new SolidColorBrush(MarkerColor(place, palette)) { Opacity = 0.55 }, 0.65 / _zoom),
                    plate,
                    3 / _zoom,
                    3 / _zoom);
                _labelHitTargets.Add((new Rect(TransformToScreen(plate.TopLeft), TransformToScreen(plate.BottomRight)), place));
            }
            dc.DrawText(text, labelPoint);
        }
    }

    private Point LabelPoint(
        SoundtrackPlace place,
        Point point,
        double radius,
        FormattedText text,
        IReadOnlyList<Rect> existingLabels)
    {
        var gap = 4 / _zoom;
        var candidates = new List<Point>();
        if (IsGroup(place, "hold"))
        {
            var anchor = HoldLabelAnchor(place, point, text);
            candidates.Add(new Point(anchor.X - text.Width / 2, anchor.Y - text.Height / 2));
            candidates.Add(new Point(anchor.X - text.Width / 2, anchor.Y - text.Height - radius - gap));
            candidates.Add(new Point(anchor.X + radius + gap, anchor.Y - text.Height / 2));
            candidates.Add(new Point(anchor.X - text.Width / 2, anchor.Y + radius + gap));
            candidates.Add(new Point(anchor.X - text.Width - radius - gap, anchor.Y - text.Height / 2));
        }
        else if (IsCity(place))
        {
            candidates.Add(new Point(point.X + radius + gap, point.Y + radius * 0.15));
            candidates.Add(new Point(point.X - text.Width / 2, point.Y + radius + gap));
            candidates.Add(new Point(point.X - text.Width - radius - gap, point.Y - text.Height / 2));
            candidates.Add(new Point(point.X - text.Width / 2, point.Y - text.Height - radius - gap));
        }
        else
        {
            candidates.Add(new Point(point.X + radius + gap, point.Y - text.Height / 2));
            candidates.Add(new Point(point.X - text.Width / 2, point.Y + radius + gap));
            candidates.Add(new Point(point.X - text.Width - radius - gap, point.Y - text.Height / 2));
            candidates.Add(new Point(point.X - text.Width / 2, point.Y - text.Height - radius - gap));
        }

        if (_cityOverview is not null)
            foreach (var distance in new[] { 22d, 40d, 60d, 82d, 106d })
                foreach (var direction in new[] { -1d, 1d })
                {
                    candidates.Add(new Point(point.X + radius + gap, point.Y + direction * distance / _zoom - text.Height / 2));
                    candidates.Add(new Point(point.X - text.Width - radius - gap, point.Y + direction * distance / _zoom - text.Height / 2));
                    candidates.Add(new Point(point.X - text.Width / 2, point.Y + direction * distance / _zoom - text.Height / 2));
                }

        foreach (var candidate in candidates)
        {
            var rect = new Rect(candidate, new Size(text.Width + gap, text.Height));
            rect.Inflate(3 / _zoom, 3 / _zoom);
            if (IsLabelInViewport(rect) && !existingLabels.Any(existing => existing.IntersectsWith(rect))) return candidate;
        }
        return ClampLabelPoint(candidates[0], text);
    }

    private Point HoldLabelAnchor(SoundtrackPlace hold, Point holdPoint, FormattedText text)
    {
        var nearestCity = _places
            .Where(place => place.Map is not null && IsCity(place))
            .Select(place => Project(place.Map!.X, place.Map.Y))
            .OrderBy(point => (point - holdPoint).LengthSquared)
            .FirstOrDefault();
        var away = holdPoint - nearestCity;
        if (away.Length < 1)
        {
            away = hold.DisplayName.ToLowerInvariant() switch
            {
                var name when name.Contains("haafingar") => new Vector(-1, -0.5),
                var name when name.Contains("reach") => new Vector(0.2, -1),
                var name when name.Contains("whiterun") => new Vector(-0.7, -0.7),
                var name when name.Contains("eastmarch") => new Vector(0, 1),
                var name when name.Contains("rift") => new Vector(-0.4, -1),
                _ => new Vector(0, -1)
            };
        }
        away.Normalize();
        var separation = Math.Max(24 / _zoom, Math.Min(44 / _zoom, text.Width * 0.22));
        return holdPoint + away * separation;
    }

    private bool IsLabelInViewport(Rect rect)
    {
        var padding = 4 / _zoom;
        var viewport = new Rect(
            -_pan.X / _zoom + padding,
            -_pan.Y / _zoom + padding,
            ActualWidth / _zoom - padding * 2,
            ActualHeight / _zoom - padding * 2);
        var onScreen = new Rect(TransformToScreen(rect.TopLeft), TransformToScreen(rect.BottomRight));
        var controls = new Rect(Math.Max(0, ActualWidth - 74), 0, 74, 211);
        return viewport.Contains(rect) && onScreen.Bottom < ActualHeight - 30 && !controls.IntersectsWith(onScreen);
    }

    private Point ClampLabelPoint(Point point, FormattedText text)
    {
        var padding = 4 / _zoom;
        var left = -_pan.X / _zoom + padding;
        var top = -_pan.Y / _zoom + padding;
        var right = (ActualWidth - _pan.X) / _zoom - text.Width - padding;
        var bottom = (ActualHeight - _pan.Y) / _zoom - text.Height - padding;
        return new Point(Math.Clamp(point.X, left, right), Math.Clamp(point.Y, top, bottom));
    }

    private static bool IsDuplicateMajorMarker(SoundtrackPlace place, IReadOnlyList<SoundtrackPlace> majorPlaces)
    {
        if (place.Scope != SoundtrackPlaceScope.MapMarker) return false;
        return majorPlaces.Any(major => major.DisplayName.Equals(place.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private void DrawPlaceMarker(
        DrawingContext dc,
        SoundtrackPlace place,
        Point point,
        double radius,
        bool selected,
        bool focused,
        bool hovered,
        MapPalette palette)
    {
        var fill = new SolidColorBrush(selected ? AssignmentGold : focused
            ? ViewingColor
            : MarkerColor(place, palette));
        var stroke = new Pen(
            new SolidColorBrush(selected ? AssignmentGold : focused || hovered ? ViewingColor : palette.PinStroke),
            1.35 / _zoom);

        if (IsGroup(place, "hold"))
        {
            dc.DrawGeometry(fill, stroke, PolygonGeometry(point, radius, 4, Math.PI / 4));
            return;
        }
        if (IsCity(place))
        {
            dc.DrawGeometry(fill, stroke, PolygonGeometry(point, radius * 1.2, 4, 0));
            return;
        }
        if (IsTown(place))
        {
            dc.DrawGeometry(fill, stroke, PolygonGeometry(point, radius, 6, Math.PI / 6));
            return;
        }
        if (IsGroup(place, "regionBiome"))
        {
            dc.DrawGeometry(fill, stroke, PolygonGeometry(point, radius * 1.15, 3, -Math.PI / 2));
            return;
        }

        dc.DrawEllipse(fill, stroke, point, radius, radius);
    }

    private static StreamGeometry PolygonGeometry(Point center, double radius, int sides, double rotation)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        for (var side = 0; side < sides; side++)
        {
            var angle = rotation + Math.PI * 2 * side / sides;
            var point = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
            if (side == 0) context.BeginFigure(point, true, true);
            else context.LineTo(point, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Color MarkerColor(SoundtrackPlace place, MapPalette palette)
    {
        if (IsGroup(place, "hold")) return HoldColor(place);
        if (IsSolstheimTerritory(place)) return SolstheimRegionColor(place);
        if (IsCity(place)) return Color.FromRgb(229, 195, 139);
        if (IsTown(place)) return Color.FromRgb(196, 178, 211);
        if (IsGroup(place, "regionBiome")) return Color.FromRgb(102, 171, 220);
        return palette.PinFill;
    }

    private static Color HoldColor(SoundtrackPlace place) => place.DisplayName
        .Replace("the ", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim()
        .ToLowerInvariant() switch
        {
            "eastmarch" => Color.FromRgb(220, 137, 73),
            "falkreath" => Color.FromRgb(92, 171, 103),
            "haafingar" => Color.FromRgb(174, 118, 207),
            "hjaalmarch" => Color.FromRgb(78, 171, 165),
            "pale" => Color.FromRgb(130, 190, 220),
            "reach" => Color.FromRgb(202, 101, 122),
            "rift" => Color.FromRgb(184, 170, 73),
            "whiterun" => Color.FromRgb(142, 174, 198),
            "winterhold" => Color.FromRgb(99, 137, 219),
            _ => Color.FromRgb(211, 184, 112)
        };

    private static string MapLabel(SoundtrackPlace place)
    {
        if (IsGroup(place, "hold")) return $"{place.DisplayName.ToUpperInvariant()} HOLD";
        var solstheimName = SolstheimRegionName(place);
        if (solstheimName is not null) return solstheimName.ToUpperInvariant();
        return AtlasLocationDirectory.Name(place);
    }

    private void DrawCompass(DrawingContext dc)
    {
        var palette = PaletteFor(_map?.Style ?? SoundtrackMapStyle.Skyrim);
        var center = new Point(Math.Max(34, ActualWidth - 42), 42);
        var brush = new SolidColorBrush(palette.Accent) { Opacity = 0.82 };
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(130, 12, 9, 18)), new Pen(brush, 1), center, 22, 22);
        var north = new StreamGeometry();
        using (var context = north.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - 17), true, true);
            context.LineTo(new Point(center.X - 5, center.Y + 4), true, false);
            context.LineTo(new Point(center.X, center.Y), true, false);
            context.LineTo(new Point(center.X + 5, center.Y + 4), true, false);
        }
        dc.DrawGeometry(brush, null, north);
        var label = CreateText("N", 9, palette.Label);
        dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y + 7));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_map is null) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.2, e.Delta / 120d));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var point = e.GetPosition(this);
        var place = PlaceAt(point);
        if (place is not null)
        {
            PlaceActivated?.Invoke(this, place);
            e.Handled = true;
            return;
        }
        _dragStart = point;
        _panStart = _pan;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_dragStart is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            _pan = _panStart + (point - _dragStart.Value);
            ClampPan();
            InvalidateVisual();
            return;
        }

        var hovered = PlaceAt(point);
        if (ReferenceEquals(hovered, _hovered)) return;
        _hovered = hovered;
        Cursor = hovered is null ? Cursors.Arrow : Cursors.Hand;
        PlaceHovered?.Invoke(this, hovered);
        InvalidateVisual();
    }

    private SoundtrackPlace? PlaceAt(Point point)
    {
        // Nearby city and hold pins share hit areas. Resolve the nearest pin
        // consistently even when assignment or focus changes the drawing order.
        var pin = _hitTargets.Where(target => target.Bounds.Contains(point))
            .OrderBy(target => (new Point(target.Bounds.X + target.Bounds.Width / 2,
                target.Bounds.Y + target.Bounds.Height / 2) - point).LengthSquared)
            .ThenBy(target => IsGroup(target.Place, "hold") ? 1 : 0)
            .FirstOrDefault().Place;
        return pin ?? _labelHitTargets.LastOrDefault(target => target.Bounds.Contains(point)).Place;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStart = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_dragStart is null && _hovered is not null)
        {
            _hovered = null;
            PlaceHovered?.Invoke(this, null);
            InvalidateVisual();
        }
    }

    private bool IsSelected(SoundtrackPlace place) => _selectedIds.Contains(place.Id);

    private bool IsLogicalPlaceSelected(SoundtrackPlace place)
    {
        if (IsSelected(place)) return true;
        var rootId = place.RootPlaceId ?? place.Id;
        if (_selectedRootIds.Contains(rootId)) return true;
        return _places.Any(candidate =>
            (candidate.RootPlaceId ?? candidate.Id).Equals(rootId, StringComparison.OrdinalIgnoreCase) &&
            IsSelected(candidate));
    }

    private bool IsPlaceFocused(SoundtrackPlace place) => !string.IsNullOrWhiteSpace(_focusedRootId) &&
        (place.RootPlaceId ?? place.Id).Equals(_focusedRootId, StringComparison.OrdinalIgnoreCase);

    private static bool IsGroup(SoundtrackPlace place, string group) =>
        place.Group.Equals(group, StringComparison.OrdinalIgnoreCase);

    private static bool IsTown(SoundtrackPlace place) => !IsGroup(place, "hold") && !IsSanctuary(place) && !IsCity(place) &&
        (IsGroup(place, "town") ||
         (place.Kind == SoundtrackPlaceKind.Location && KnownTownNames.Contains(place.DisplayName)));

    private static bool IsSanctuary(SoundtrackPlace place) =>
        place.DisplayName.Contains("Sanctuary", StringComparison.OrdinalIgnoreCase) ||
        place.EditorId.Contains("Sanctuary", StringComparison.OrdinalIgnoreCase);

    private static bool IsCity(SoundtrackPlace place) => !IsGroup(place, "hold") &&
        (IsGroup(place, "city") ||
         (place.Kind == SoundtrackPlaceKind.Location && KnownHoldCapitalNames.Contains(place.DisplayName)));

    private static bool IsMajorCity(SoundtrackPlace place) =>
        IsCity(place) && KnownMajorCityNames.Contains(place.DisplayName);

    private static bool IsMajorPlace(SoundtrackPlace place) =>
        IsGroup(place, "hold") || IsCity(place) || IsTown(place);

    private static bool IsSolstheimTerritory(SoundtrackPlace place) =>
        SolstheimRegionName(place) is not null;

    private static SoundtrackPlace[] SolstheimTerritoryPlaces(IEnumerable<SoundtrackPlace> places) => places
        .Where(IsSolstheimTerritory)
        .GroupBy(place => place.EditorId, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();

    private static string? SolstheimRegionName(SoundtrackPlace place) => place.EditorId.ToLowerInvariant() switch
    {
        "audioextdlc2ashlands" => "Ashlands",
        "weatherdlc2solstheimmtns" => "Mountains",
        "weatherdlc2solstheimsnow" => "Snowfields",
        _ => null
    };

    private static Color SolstheimRegionColor(SoundtrackPlace place) => SolstheimRegionName(place) switch
    {
        "Ashlands" => Color.FromRgb(191, 95, 59),
        "Mountains" => Color.FromRgb(142, 126, 116),
        "Snowfields" => Color.FromRgb(105, 161, 189),
        _ => Color.FromRgb(211, 184, 112)
    };

    private int Importance(SoundtrackPlace place)
    {
        if (IsCity(place)) return 100;
        return place.Group switch
        {
        "hold" => 110,
        "town" => 95,
        "worldspace" => 80,
        "dungeon" => 55,
        "regionBiome" => 45,
        _ when place.Scope == SoundtrackPlaceScope.MapMarker => 40,
        _ when IsTown(place) => 95,
        _ => 25
        };
    }

    private static double PolygonArea(SoundtrackMapPolygon polygon)
    {
        if (polygon.Points.Count < 3) return 0;
        var area = 0d;
        for (var i = 0; i < polygon.Points.Count; i++)
        {
            var current = polygon.Points[i];
            var next = polygon.Points[(i + 1) % polygon.Points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }
        return Math.Abs(area) * 0.5;
    }

    private Point Project(SoundtrackMapPoint point) => Project(point.X, point.Y);

    private Point Project(double x, double y)
    {
        var projected = ProjectUntransformed(x, y);
        return projected;
    }

    private Point ProjectUntransformed(double x, double y)
    {
        if (_map is null) return default;
        var dataWidth = Math.Max(1, _viewMaxX - _viewMinX);
        var dataHeight = Math.Max(1, _viewMaxY - _viewMinY);
        var padding = _terrain?.HasTerrain == true ? 8 : Math.Min(42, Math.Max(18, Math.Min(ActualWidth, ActualHeight) * 0.045));
        var availableWidth = Math.Max(1, ActualWidth - padding * 2);
        var availableHeight = Math.Max(1, ActualHeight - padding * 2);
        var scale = Math.Min(availableWidth / dataWidth, availableHeight / dataHeight);
        var drawnWidth = dataWidth * scale;
        var drawnHeight = dataHeight * scale;
        var offsetX = (ActualWidth - drawnWidth) / 2;
        var offsetY = (ActualHeight - drawnHeight) / 2;
        return new Point(
            offsetX + (x - _viewMinX) * scale,
            offsetY + (_viewMaxY - y) * scale);
    }

    private void ResetView()
    {
        _zoom = 1;
        _pan = default;
        RecalculateViewBounds();
    }

    private void RecalculateViewBounds()
    {
        if (_cityOverview is not null)
        {
            (_viewMinX, _viewMinY, _viewMaxX, _viewMaxY) =
                (_cityOverview.MinX, _cityOverview.MinY, _cityOverview.MaxX, _cityOverview.MaxY);
            return;
        }
        if (_map is null)
        {
            _viewMinX = _viewMinY = 0;
            _viewMaxX = _viewMaxY = 1;
            return;
        }

        if (_terrain?.HasTerrain == true && !_usesParentTerrain)
        {
            (_viewMinX, _viewMinY, _viewMaxX, _viewMaxY) =
                (_terrain.Bounds[0], _terrain.Bounds[1], _terrain.Bounds[2], _terrain.Bounds[3]);
            return;
        }
        var roots = _places
            .Where(place => place.RootPlaceId is null && place.Map is not null)
            .ToArray();
        var selectableAnchors = roots
            .Where(place => place.Kind != SoundtrackPlaceKind.Worldspace)
            .ToArray();
        var anchors = selectableAnchors.Length <= 24
            ? selectableAnchors
            : selectableAnchors.Where(place => !IsGroup(place, "regionBiome")).ToArray();
        if (anchors.Length == 0)
        {
            anchors = roots.Where(place => place.Kind != SoundtrackPlaceKind.Worldspace).ToArray();
        }
        if (anchors.Length == 0) anchors = roots;

        var points = anchors
            .Select(place => new SoundtrackMapPoint(place.Map!.X, place.Map.Y))
            .ToList();
        var territoryPlaces = _map.Style == SoundtrackMapStyle.Skyrim
            ? roots.Where(place => IsGroup(place, "hold"))
            : _map.Style == SoundtrackMapStyle.Solstheim
                ? SolstheimTerritoryPlaces(roots)
                : [];
        points.AddRange(territoryPlaces
            .Where(place => place.Map is not null)
            .SelectMany(place => place.Map!.Areas)
            .SelectMany(area => area.Points));

        if (points.Count == 0)
        {
            _viewMinX = _map.MinX;
            _viewMinY = _map.MinY;
            _viewMaxX = _map.MaxX;
            _viewMaxY = _map.MaxY;
            return;
        }

        _viewMinX = points.Min(point => point.X);
        _viewMinY = points.Min(point => point.Y);
        _viewMaxX = points.Max(point => point.X);
        _viewMaxY = points.Max(point => point.Y);
        const double minimumSpan = 8192;
        var width = Math.Max(minimumSpan, _viewMaxX - _viewMinX);
        var height = Math.Max(minimumSpan, _viewMaxY - _viewMinY);
        var centerX = (_viewMinX + _viewMaxX) / 2;
        var centerY = (_viewMinY + _viewMaxY) / 2;
        _viewMinX = centerX - width * 0.56;
        _viewMaxX = centerX + width * 0.56;
        _viewMinY = centerY - height * 0.56;
        _viewMaxY = centerY + height * 0.56;
    }

    private Point TransformToScreen(Point point) => new(point.X * _zoom + _pan.X, point.Y * _zoom + _pan.Y);

    private void ClampPan()
    {
        if (_cityOverview is not null)
        {
            // Leave room around the city while approaching the parent map.
            // The camera keeps the world point beneath the mouse stationary.
            var center = ProjectUntransformed((_viewMinX + _viewMaxX) / 2, (_viewMinY + _viewMaxY) / 2);
            _pan = new Vector(Math.Clamp(_pan.X, -center.X * _zoom - ActualWidth * .25, ActualWidth * 1.25 - center.X * _zoom),
                Math.Clamp(_pan.Y, -center.Y * _zoom - ActualHeight * .25, ActualHeight * 1.25 - center.Y * _zoom));
            return;
        }
        var minX = ActualWidth * (1 - _zoom);
        var minY = ActualHeight * (1 - _zoom);
        _pan = new Vector(Math.Clamp(_pan.X, minX, 0), Math.Clamp(_pan.Y, minY, 0));
    }

    private void DrawCenteredText(DrawingContext dc, string value, double size, Brush brush)
    {
        var text = CreateText(value, size, ((SolidColorBrush)brush).Color);
        dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }

    private static FormattedText CreateText(string value, double size, Color color, FontWeight? weight = null) => new(
        value,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI Variable Text"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size,
        new SolidColorBrush(color),
        1.0);

    private static MapPalette PaletteFor(SoundtrackMapStyle style) => style switch
    {
        SoundtrackMapStyle.Solstheim => new("#211410", "#090708", "#6B3928", "#D78346", "#A7653B", "#E8B67B", "#6D3D2A", "#C57A45", "#3C211B", "#D5A672", "#F4C477", "#FFF0D0"),
        SoundtrackMapStyle.Apocrypha => new("#112116", "#040906", "#285832", "#82D76C", "#4A9A55", "#B0EF83", "#1D5129", "#69C85A", "#12391D", "#82D76C", "#C9FF9C", "#E9FFD5"),
        SoundtrackMapStyle.Sovngarde => new("#242449", "#070813", "#4A5A91", "#93F5D5", "#947BDB", "#D8FFF3", "#485B8D", "#8AD8CA", "#23335D", "#A5F2DF", "#FFD786", "#F2F8FF"),
        SoundtrackMapStyle.SoulCairn => new("#201335", "#06040B", "#5B337B", "#D78AFF", "#7753A1", "#E6BCFF", "#4A2868", "#BB6FE1", "#2B1746", "#D78AFF", "#79E7FF", "#F9E9FF"),
        SoundtrackMapStyle.ForgottenVale => new("#183244", "#071018", "#46758D", "#B9EEFF", "#78A9BF", "#E5F9FF", "#315F77", "#94D0E4", "#173B4E", "#C3F1FF", "#F2D893", "#F5FCFF"),
        SoundtrackMapStyle.ArcaneRealm => new("#28162F", "#08060D", "#69437B", "#E4A2FF", "#9A62B2", "#F1CAFF", "#573167", "#C67CE3", "#341E40", "#DB9CF4", "#F1C86D", "#FBEFFF"),
        _ => new("#243246", "#090B10", "#56687C", "#F1C86D", "#8495A4", "#EEE1BE", "#475D70", "#A7B7B3", "#273848", "#D5C38E", "#F1C86D", "#FFF4D4")
    };

    private sealed record MapPalette(
        string BackgroundTopHex,
        string BackgroundBottomHex,
        string BorderHex,
        string AccentHex,
        string ContourHex,
        string LabelHex,
        string RegionFillHex,
        string RegionStrokeHex,
        string PinFillHex,
        string PinStrokeHex,
        string SelectedFillHex,
        string PinCoreHex)
    {
        public Color BackgroundTop => Parse(BackgroundTopHex);
        public Color BackgroundBottom => Parse(BackgroundBottomHex);
        public Color Border => Parse(BorderHex);
        public Color Accent => Parse(AccentHex);
        public Color Contour => Parse(ContourHex);
        public Color Label => Parse(LabelHex);
        public Color RegionFill => Parse(RegionFillHex);
        public Color RegionStroke => Parse(RegionStrokeHex);
        public Color PinFill => Parse(PinFillHex);
        public Color PinStroke => Parse(PinStrokeHex);
        public Color SelectedFill => Parse(SelectedFillHex);
        public Color PinCore => Parse(PinCoreHex);
        private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    }

    private sealed record TerritorySite(
        SoundtrackPlace Place,
        double X,
        double Y,
        HashSet<(int X, int Y)> SeedCells);
}
