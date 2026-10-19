namespace Skypod.Core;

/// <summary>
/// A build-time view of the active load order's place-to-music graph.  The
/// identifiers are stable across load-order index changes because they use
/// plugin names and local FormIDs rather than runtime FormIDs.
/// </summary>
public sealed record SoundtrackWorldIndex(
    IReadOnlyList<SoundtrackPlace> Places,
    IReadOnlyList<SoundtrackMusicType> MusicTypes)
{
    public int SchemaVersion { get; init; } = 5;
    public IReadOnlyList<BossEncounter> Bosses { get; init; } = [];
    public IReadOnlyList<SoundtrackWorldMap> Maps { get; init; } = [];
    public static SoundtrackWorldIndex Empty { get; } = new([], []);
}

public enum SoundtrackPlaceKind
{
    Area,
    Cell,
    Location,
    Worldspace,
    Region
}

public enum SoundtrackPlaceScope
{
    Default,
    WholeLocation,
    InteriorOnly,
    ExteriorOnly,
    MapMarker
}

public enum SoundtrackMapStyle
{
    Skyrim,
    Solstheim,
    Apocrypha,
    Sovngarde,
    SoulCairn,
    ForgottenVale,
    ArcaneRealm
}

/// <summary>
/// Indicates how safely a palette can be used as an automatic playback target.
/// Ambient palettes can accept ordinary soundtrack additions. Contextual and
/// event palettes should only be selected deliberately by an advanced rule.
/// </summary>
public enum MusicTypeSafety
{
    Ambient,
    Contextual,
    Event,
    Silence,
    Unknown
}

public sealed record SoundtrackPlace(
    string Id,
    string DisplayName,
    string EditorId,
    string FormKey,
    string Plugin,
    SoundtrackPlaceKind Kind,
    string Group,
    string? WorldspaceFormKey,
    string? WorldspaceName,
    string? ParentLocationFormKey,
    bool IsAdvanced,
    IReadOnlyList<string> ConditionFormKeys,
    IReadOnlyList<SoundtrackMusicAssignment> Assignments,
    string SearchText)
{
    /// <summary>
    /// Map-only presentation data. Runtime routing continues to use the stable
    /// place ID and concrete ConditionFormKeys, never screen coordinates.
    /// </summary>
    public SoundtrackPlaceMapData? Map { get; init; }

    /// <summary>
    /// Distinguishes the exact interior/exterior choices displayed for a
    /// single logical location in the Studio map picker.
    /// </summary>
    public SoundtrackPlaceScope Scope { get; init; } = SoundtrackPlaceScope.Default;

    /// <summary>
    /// Stable ID of the logical location represented by a scoped synthetic
    /// area. Null for ordinary records.
    /// </summary>
    public string? RootPlaceId { get; init; }
}

public sealed record SoundtrackMapPoint(double X, double Y);

public sealed record SoundtrackMapPolygon(IReadOnlyList<SoundtrackMapPoint> Points);

public sealed record SoundtrackPlaceMapData(
    string WorldspaceFormKey,
    string PlacementSource,
    double X,
    double Y,
    IReadOnlyList<SoundtrackMapPolygon> Areas,
    double Confidence);

public sealed record SoundtrackWorldMap(
    string Id,
    string DisplayName,
    string EditorId,
    string FormKey,
    string Plugin,
    SoundtrackMapStyle Style,
    double MinX,
    double MinY,
    double MaxX,
    double MaxY,
    int ExteriorCellCount,
    bool IsModAdded)
{
    public string? LocationFormKey { get; init; }
    public string? ParentWorldspaceFormKey { get; init; }
    public string? TerrainParentEditorId { get; init; }
}

public sealed record SoundtrackMusicAssignment(
    string Provenance,
    bool IsDirect,
    string SourcePlaceId,
    string SourcePlaceFormKey,
    string SourcePlaceName,
    string MusicTypeId,
    string MusicTypeFormKey,
    string MusicTypeEditorId,
    string MusicTypePlugin,
    TrackCategory Category,
    MusicTypeSafety Safety,
    string SafetyReason);

public sealed record SoundtrackMusicType(
    string Id,
    string FormKey,
    string EditorId,
    string Plugin,
    TrackCategory Category,
    MusicTypeSafety Safety,
    string SafetyReason,
    ushort Priority,
    IReadOnlyList<string> Flags,
    float FadeDuration,
    string SearchText);
