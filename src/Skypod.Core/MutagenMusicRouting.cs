using System.Globalization;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace Skypod.Core;

internal static class MutagenMusicRouting
{
    private static readonly FormKey PlayerReference =
        new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x000014);

    internal static ConditionFloat CreatePlaceCondition(PlaybackPlaceCondition place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (!TryParseFormKey(place.FormKey, out var formKey))
        {
            throw new InvalidDataException(
                $"Place {place.DisplayName} has invalid FormKey '{place.FormKey}'.");
        }

        ConditionData data = place.Kind switch
        {
            SoundtrackPlaceKind.Cell => MakeCellConditionData(formKey),
            SoundtrackPlaceKind.Location => MakeLocationConditionData(formKey),
            SoundtrackPlaceKind.Worldspace => MakeWorldspaceConditionData(formKey),
            SoundtrackPlaceKind.Region => MakeRegionConditionData(formKey),
            SoundtrackPlaceKind.Area => throw new InvalidDataException(
                $"Area {place.DisplayName} was not expanded into concrete cell conditions."),
            _ => throw new InvalidDataException(
                $"Place {place.DisplayName} has unsupported kind {place.Kind}.")
        };

        data.RunOnType = Condition.RunOnType.Reference;
        data.Reference = new FormLink<ISkyrimMajorRecordGetter>(PlayerReference);
        return new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1.0f,
            Data = data
        };
    }

    private static GetInCellConditionData MakeCellConditionData(FormKey formKey)
    {
        var data = new GetInCellConditionData();
        data.Cell = new FormLinkOrIndex<ICellGetter>(data, formKey);
        return data;
    }

    private static GetInCurrentLocConditionData MakeLocationConditionData(FormKey formKey)
    {
        var data = new GetInCurrentLocConditionData();
        data.Location = new FormLinkOrIndex<ILocationGetter>(data, formKey);
        return data;
    }

    private static GetInWorldspaceConditionData MakeWorldspaceConditionData(FormKey formKey)
    {
        var data = new GetInWorldspaceConditionData();
        data.WorldspaceOrList = new FormLinkOrIndex<IWorldspaceOrListGetter>(data, formKey);
        return data;
    }

    private static IsPlayerInRegionConditionData MakeRegionConditionData(FormKey formKey)
    {
        var data = new IsPlayerInRegionConditionData();
        data.Region = new FormLinkOrIndex<IRegionGetter>(data, formKey);
        return data;
    }

    internal static bool TryParseFormKey(string? value, out FormKey formKey)
    {
        formKey = FormKey.Null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var separator = value.LastIndexOf('|');
        if (separator <= 0 || separator == value.Length - 1) return false;
        var plugin = value[..separator].Trim();
        var idText = value[(separator + 1)..].Trim();
        if (!uint.TryParse(idText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ||
            id > 0x00FF_FFFF)
        {
            return false;
        }

        try
        {
            formKey = new FormKey(ModKey.FromNameAndExtension(plugin), id);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
