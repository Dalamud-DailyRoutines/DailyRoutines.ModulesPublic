using System.Globalization;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class OptimizedSalvage
{
    private static unsafe void OnItemTooltip
    (
        ItemKind                          itemKind,
        uint                              itemID,
        ref List<TooltipItemModification> modifications
    )
    {
        if (itemKind != ItemKind.Normal) return;
        if (!LuminaGetter.TryGetRow(itemID, out Item item) ||
            item.Desynth == 0)
            return;

        using var rented = new RentedSeStringBuilder();

        var itemLevel = item.LevelItem.RowId;
        var level     = PlayerState.Instance()->GetDesynthesisLevel(item.ClassJobRepair.RowId);

        ushort  colorType = 0;
        string? extraHint = null;

        if (level <= itemLevel)
            colorType = 0;
        else if (level > itemLevel + 50)
        {
            colorType = COLOR_LEVEL_TOO_HIGH;
            extraHint = $"{Lang.Get("OptimizedSalvage-Tag-ChanceRateUp")}" +
                        $"{UITexts.Space}"                                 +
                        $"{Lang.Get("OptimizedSalvage-Tag-UnableEXPGain")}";
        }
        else
        {
            colorType = COLOR_LEVEL_HIGH;
            extraHint = $"{Lang.Get("OptimizedSalvage-Tag-ChanceRateUp")}";
        }

        rented.PushColorType(colorType)
              .Append(level.ToString("F2", CultureInfo.InvariantCulture))
              .PopColorType();

        if (extraHint != null)
            rented.Append((string)$" ［{extraHint}］");

        if (item.EquipSlotCategory.RowId > 0)
        {
            var macroString = LuminaGetter.GetRowOrDefault<Addon>(1361).Text.ToMacroString();
            macroString = macroString.Replace(UITexts.Space.ToString(), "<br>");

            if (item.Desynth > 0)
            {
                macroString += "<br>"                                          +
                               $"{Lang.Get("OptimizedSalvage-Level-Current")}" +
                               $"{UITexts.Colon}"                              +
                               "<string(lstr5)>";
            }

            var result = ISeStringEvaluator.Instance().EvaluateMacroString
            (
                macroString,
                [
                    item.MaterializeType > 0 ?
                        1 :
                        0,
                    item.IsGlamorous ?
                        1 :
                        0,
                    (int)item.Desynth,
                    item.LevelItem.RowId,
                    rented.ToReadOnlySeString()
                ]
            );

            modifications.Add
            (
                new()
                {
                    Target = TooltipItemType.GearAbilityInfo,
                    Type   = TooltipModificationType.Contribute,
                    Text   = result
                }
            );
        }
        else
        {
            var result = ISeStringEvaluator.Instance().EvaluateMacroString
            (
                Lang.Get("OptimizedSalvage-MacroString-NonGearDescription"),
                [
                    item.Description,
                    itemLevel,
                    item.ClassJobRepair.RowId,
                    rented.ToReadOnlySeString()
                ]
            );

            modifications.Add
            (
                new()
                {
                    Target = TooltipItemType.Description,
                    Type   = TooltipModificationType.Contribute,
                    Text   = result
                }
            );
        }
    }
}
