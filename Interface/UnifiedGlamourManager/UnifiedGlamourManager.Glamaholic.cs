using System.Globalization;
using System.Text;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal partial class UnifiedGlamourManager
{
    private readonly List<string>      glamaholicImportSkipped = [];
    private readonly List<PlatePreset> glamaholicImportPreview = [];
    private          string            glamaholicImportStatus  = string.Empty;
    private          bool              glamaholicImportExpanded;

    private sealed class GlamaholicConfiguration
    {
        public List<GlamaholicNode>? Plates { get; set; }
    }

    private sealed class GlamaholicNode
    {
        public string?               Name     { get; set; }
        public List<GlamaholicNode>? Children { get; set; }
        public GlamaholicPlate?      Plate    { get; set; }
    }

    private sealed class GlamaholicPlate
    {
        public string?                              Name               { get; set; }
        public Dictionary<string, GlamaholicItem?>? Items              { get; set; }
        public List<string>?                        Tags               { get; set; }
        public bool                                 FillWithNewEmperor { get; set; }
    }

    private sealed class GlamaholicItem
    {
        public uint ItemID { get; set; }
        public byte Stain1 { get; set; }
        public byte Stain2 { get; set; }
    }

    private void ClearGlamaholicImportPreview()
    {
        glamaholicImportPreview.Clear();
        glamaholicImportSkipped.Clear();
        glamaholicImportStatus = string.Empty;
    }

    private void PreviewGlamaholicImport
    (
        string path
    )
    {
        ClearGlamaholicImportPreview();

        var filePath = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Preset-ImportFailed");
            return;
        }

        try
        {
            var configuration = JsonConvert.DeserializeObject<GlamaholicConfiguration>(File.ReadAllText(filePath, Encoding.UTF8));
            if (configuration?.Plates == null)
            {
                glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Preset-ImportFailed");
                return;
            }

            var untitledTitle  = Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset");
            var existing       = config.Presets.Select(GetPresetImportKey).ToHashSet();
            var totalCount     = 0;
            var duplicateCount = 0;

            foreach (var node in configuration.Plates)
                VisitNode(node, string.Empty);

            glamaholicImportStatus = Lang.Get
            (
                "UnifiedGlamourManager-Preset-ImportSummary",
                new Dictionary<string, object>
                {
                    ["totalCount"]     = totalCount,
                    ["importCount"]    = glamaholicImportPreview.Count,
                    ["duplicateCount"] = duplicateCount,
                    ["skippedCount"]   = glamaholicImportSkipped.Count
                }
            );

            void VisitNode
            (
                GlamaholicNode? node,
                string          folder
            )
            {
                if (node == null)
                    return;

                if (node.Children != null)
                {
                    var folderName = node.Name?.Trim() ?? string.Empty;
                    var nextFolder = string.IsNullOrEmpty(folder) ?
                                         folderName :
                                         string.IsNullOrEmpty(folderName) ?
                                             folder :
                                             $"{folder}/{folderName}";

                    foreach (var child in node.Children)
                        VisitNode(child, nextFolder);

                    return;
                }

                if (node.Plate?.Items == null)
                    return;

                totalCount++;

                var preset = ConvertGlamaholicPlate(node.Plate, untitledTitle, folder);
                if (preset == null)
                    return;

                if (existing.Add(GetPresetImportKey(preset)))
                    glamaholicImportPreview.Add(preset);
                else
                    duplicateCount++;
            }
        }
        catch (Exception ex)
        {
            ClearGlamaholicImportPreview();
            DLog.Error("UnifiedGlamourManager import failed", ex);
            glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Preset-ImportFailed");
        }
    }

    private PlatePreset? ConvertGlamaholicPlate
    (
        GlamaholicPlate plate,
        string          untitledTitle,
        string          folder
    )
    {
        var title  = string.IsNullOrWhiteSpace(plate.Name) ?
                         untitledTitle :
                         plate.Name.Trim();
        var tags   = plate.Tags == null ?
                         string.Empty :
                         string.Join(", ", plate.Tags.Where(static x => !string.IsNullOrWhiteSpace(x)));
        var preset = new PlatePreset
        {
            Title = title,
            Note  = string.Join("; ", new[] { folder, tags }.Where(static x => !string.IsNullOrEmpty(x))),
            Race  = string.Empty,
            Sex   = string.Empty
        };

        var skippedBefore = glamaholicImportSkipped.Count;

        foreach (var (slotName, item) in plate.Items)
        {
            var slot = Array.IndexOf(GlamaholicSlotNames, slotName);
            if (slot < 0 || item == null)
            {
                glamaholicImportSkipped.Add($"{title} - {slotName}");
                continue;
            }

            if (item.ItemID == 0)
                continue;

            var slotIndex = (uint)slot;
            var itemID    = ItemUtil.GetBaseId(item.ItemID).ItemId;
            var validItem = LuminaGetter.TryGetRow<Item>(itemID, out var row);
            if (validItem                                                                                &&
                LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) &&
                IsEquipSlotCategoryCompatibleWithPlateSlot(category, slotIndex)                          &&
                (item.Stain1 == 0 || LuminaGetter.TryGetRow<Stain>(item.Stain1, out _))                  &&
                (item.Stain2 == 0 || LuminaGetter.TryGetRow<Stain>(item.Stain2, out _)))
            {
                preset.Items.Add(new(slotIndex, itemID, item.Stain1, item.Stain2));
                continue;
            }

            var itemLabel = validItem && TryGetItemName(row, out var name) ?
                                name :
                                itemID.ToString(CultureInfo.InvariantCulture);
            glamaholicImportSkipped.Add($"{title} - {GetPresetSlotName(slotIndex)}: {itemLabel}");
        }

        if (preset.Items.Count == 0 && glamaholicImportSkipped.Count > skippedBefore)
        {
            glamaholicImportSkipped.Add
            (
                Lang.Get
                (
                    "UnifiedGlamourManager-Preset-ImportSkippedPreset",
                    new Dictionary<string, object> { ["presetName"] = title }
                )
            );
            return null;
        }

        foreach (var entry in NormalizePresetEquipment(preset))
            glamaholicImportSkipped.Add($"{title} - {GetPresetSlotName(entry.SlotIndex)}: {LuminaWrapper.GetItemName(entry.ItemID)}");

        if (plate.FillWithNewEmperor)
            FillEmptyPresetSlots(preset);

        return preset;
    }

    private void CommitGlamaholicImport()
    {
        if (glamaholicImportPreview.Count == 0)
            return;

        var existing  = config.Presets.Select(GetPresetImportKey).ToHashSet();
        var imported  = glamaholicImportPreview.Where(x => existing.Add(GetPresetImportKey(x))).ToList();
        var timestamp = DateTime.Now.Ticks;

        for (var index = 0; index < imported.Count; index++)
            imported[index].CreatedAt = new DateTime(timestamp + index);

        if (imported.Count > 0)
        {
            config.Presets.AddRange(imported);
            selectedPreset = imported[0];
            SaveConfig();
        }

        glamaholicImportPreview.Clear();
        glamaholicImportStatus = Lang.Get
        (
            "UnifiedGlamourManager-Preset-ImportDone",
            new Dictionary<string, object> { ["count"] = imported.Count }
        );
    }

    private static string GetPresetImportKey
    (
        PlatePreset preset
    )
    {
        var title = preset.Title.Trim();
        var note  = preset.Note.Trim();
        var items = preset.Items
                          .OrderBy(static x => x.SlotIndex)
                          .ThenBy(static x => x.ItemID)
                          .ThenBy(static x => x.Stain0ID)
                          .ThenBy(static x => x.Stain1ID)
                          .Select(static x => FormattableString.Invariant($"{x.SlotIndex},{x.ItemID},{x.Stain0ID},{x.Stain1ID}"));

        return FormattableString.Invariant($"{title.Length}:{title}{note.Length}:{note}|") + string.Join(";", items);
    }

    #region 常量

    private static readonly string[] GlamaholicSlotNames =
    [
        "MainHand",
        "OffHand",
        "Head",
        "Body",
        "Hands",
        "Legs",
        "Feet",
        "Ears",
        "Neck",
        "Wrists",
        "RightRing",
        "LeftRing"
    ];

    #endregion
}
