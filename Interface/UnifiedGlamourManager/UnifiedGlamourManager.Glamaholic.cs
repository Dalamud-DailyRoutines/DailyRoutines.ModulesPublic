using System.Globalization;
using System.Text;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal partial class UnifiedGlamourManager
{
    private readonly List<string>      glamaholicImportSkipped  = [];
    private readonly List<PlatePreset> glamaholicImportPreview  = [];
    private          string            glamaholicImportStatus   = string.Empty;
    private          bool              glamaholicImportExpanded;

    private sealed record GlamaholicConfiguration(List<JToken> Plates);

    private sealed record GlamaholicNode
    (
        string                     Name,
        List<JToken>               Children,
        GlamaholicNode             Plate,
        Dictionary<string, JToken> Items,
        List<string>               Tags,
        bool                       FillWithNewEmperor
    );

    private sealed record GlamaholicItem(uint ItemID, byte Stain1, byte Stain2);

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

        try
        {
            using var stream = new StreamReader(path.Trim().Trim('"'), Encoding.UTF8);
            using var reader = new JsonTextReader(stream) { DateParseHandling = DateParseHandling.None };
            var serializer = JsonSerializer.Create
            (
                new()
                {
                    CheckAdditionalContent   = true,
                    MetadataPropertyHandling = MetadataPropertyHandling.Ignore
                }
            );

            reader.Read();
            while (reader.TokenType == JsonToken.Comment && reader.Read())
            {
            }

            var nodes = reader.TokenType switch
            {
                JsonToken.StartObject => serializer.Deserialize<GlamaholicConfiguration>(reader)?.Plates,
                JsonToken.StartArray  => serializer.Deserialize<List<JToken>>(reader),
                _                     => null
            };
            if (nodes == null)
                throw new JsonSerializationException("Invalid Glamaholic configuration");

            var untitledTitle = Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset");
            var existing      = config.Presets.Select(GetPresetImportKey).ToHashSet();
            var readCount     = 0;
            var duplicates    = 0;
            foreach (var node in nodes)
                Visit(node, string.Empty);

            glamaholicImportStatus = Lang.Get
            (
                "UnifiedGlamourManager-Editor-ImportSummary",
                new Dictionary<string, object>
                {
                    ["readCount"]      = readCount,
                    ["importCount"]    = glamaholicImportPreview.Count,
                    ["duplicateCount"] = duplicates,
                    ["skippedCount"]   = glamaholicImportSkipped.Count
                }
            );

            void Visit(JToken token, string folder)
            {
                GlamaholicNode node;
                try
                {
                    node = token?.ToObject<GlamaholicNode>(serializer);
                }
                catch (JsonException)
                {
                    node = null;
                }

                if (node == null)
                {
                    AddGlamaholicSkippedData(folder, string.Empty, token);
                    return;
                }

                if (node.Children != null)
                {
                    var folderPath = string.IsNullOrEmpty(folder) ?
                                         node.Name ?? string.Empty :
                                         $"{folder}/{node.Name}";
                    foreach (var child in node.Children)
                        Visit(child, folderPath);

                    return;
                }

                var plate = node.Plate ?? node;
                var title = string.IsNullOrWhiteSpace(plate.Name) ?
                                untitledTitle :
                                plate.Name.Trim();
                if (plate.Items == null)
                {
                    AddGlamaholicSkippedData(title, string.Empty, token);
                    return;
                }

                readCount++;
                var preset = ConvertGlamaholicPlate(plate, title, folder, serializer);
                if (preset == null)
                    return;

                if (existing.Add(GetPresetImportKey(preset)))
                    glamaholicImportPreview.Add(preset);
                else
                    duplicates++;
            }
        }
        catch (Exception ex)
        {
            ClearGlamaholicImportPreview();
            DLog.Error("UnifiedGlamourManager import failed", ex);
            glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Editor-ImportFailed");
        }
    }

    private PlatePreset? ConvertGlamaholicPlate
    (
        GlamaholicNode plate,
        string         title,
        string         folder,
        JsonSerializer serializer
    )
    {
        var tags   = plate.Tags == null ?
                         string.Empty :
                         string.Join(", ", plate.Tags);
        var preset = new PlatePreset
        {
            Title = title,
            Note  = string.Join("; ", new[] { folder, tags }.Where(x => !string.IsNullOrEmpty(x))),
            Race  = string.Empty,
            Sex   = string.Empty
        };

        var skippedBefore = glamaholicImportSkipped.Count;
        foreach (var (slotName, token) in plate.Items)
        {
            if (slotName == "$type")
                continue;

            var slot = Array.IndexOf(GlamaholicSlotNames, slotName);
            if (slot < 0 && int.TryParse(slotName, NumberStyles.None, CultureInfo.InvariantCulture, out var numericSlot))
                slot = numericSlot;

            if (slot < 0 || slot >= GlamaholicSlotNames.Length)
            {
                AddGlamaholicSkippedData(title, slotName, token);
                continue;
            }

            var slotIndex = (uint)slot;
            if (token is not JObject data                                                                                                 ||
                data.GetValue(nameof(GlamaholicItem.ItemID), StringComparison.OrdinalIgnoreCase)?.Type != JTokenType.Integer               ||
                data.GetValue(nameof(GlamaholicItem.Stain1), StringComparison.OrdinalIgnoreCase)?.Type is not (null or JTokenType.Integer) ||
                data.GetValue(nameof(GlamaholicItem.Stain2), StringComparison.OrdinalIgnoreCase)?.Type is not (null or JTokenType.Integer))
            {
                AddGlamaholicSkippedData(title, GetPresetSlotName(slotIndex), token);
                continue;
            }

            GlamaholicItem item;
            try
            {
                item = data.ToObject<GlamaholicItem>(serializer);
            }
            catch (JsonException)
            {
                AddGlamaholicSkippedData(title, GetPresetSlotName(slotIndex), token);
                continue;
            }

            if (item.ItemID == 0)
                continue;

            var itemID    = ItemUtil.GetBaseId(item.ItemID).ItemId;
            var validItem = LuminaGetter.TryGetRow<Item>(itemID, out var row);
            if (validItem                                                                                &&
                LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) &&
                IsEquipSlotCategoryCompatibleWithPlateSlot(category, slotIndex)                          &&
                (item.Stain1 == 0 || LuminaGetter.TryGetRow<Stain>(item.Stain1, out _))                   &&
                (item.Stain2 == 0 || LuminaGetter.TryGetRow<Stain>(item.Stain2, out _)))
            {
                preset.Items.Add(new(slotIndex, itemID, item.Stain1, item.Stain2));
                continue;
            }

            var itemLabel = validItem && TryGetItemName(row, out var name) ?
                                name :
                                itemID.ToString(CultureInfo.InvariantCulture);
            AddGlamaholicSkippedItem(title, slotIndex, itemLabel);
        }

        if (preset.Items.Count == 0 && glamaholicImportSkipped.Count > skippedBefore)
        {
            glamaholicImportSkipped.Add
            (
                Lang.Get(
                    "UnifiedGlamourManager-Editor-ImportSkippedPreset",
                    new Dictionary<string, object> { ["presetName"] = title }
                )
            );
            return null;
        }

        foreach (var entry in NormalizePresetEquipment(preset))
            AddGlamaholicSkippedItem(title, entry.SlotIndex, LuminaWrapper.GetItemName(entry.ItemID));

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
            "UnifiedGlamourManager-Editor-ImportDone",
            new Dictionary<string, object> { ["count"] = imported.Count }
        );
    }

    private void AddGlamaholicSkippedData
    (
        string preset,
        string slot,
        JToken token
    ) =>
        glamaholicImportSkipped.Add
        (
            Lang.Get
            (
                "UnifiedGlamourManager-Editor-ImportSkippedData",
                new Dictionary<string, object>
                {
                    ["presetName"] = preset,
                    ["slot"]       = slot,
                    ["value"]      = token?.ToString(Formatting.None) ?? "null"
                }
            )
        );

    private void AddGlamaholicSkippedItem
    (
        string preset,
        uint   slot,
        string item
    ) =>
        glamaholicImportSkipped.Add
        (
            Lang.Get
            (
                "UnifiedGlamourManager-Editor-ImportSkippedItem",
                new Dictionary<string, object>
                {
                    ["presetName"] = preset,
                    ["slot"]       = GetPresetSlotName(slot),
                    ["itemID"]     = item
                }
            )
        );

    private static string GetPresetImportKey
    (
        PlatePreset preset
    )
    {
        var title = preset.Title.Trim();
        var note  = preset.Note.Trim();
        var items = preset.Items
                          .OrderBy(x => x.SlotIndex)
                          .ThenBy(x => x.ItemID)
                          .ThenBy(x => x.Stain0ID)
                          .ThenBy(x => x.Stain1ID)
                          .Select(x => FormattableString.Invariant($"{x.SlotIndex},{x.ItemID},{x.Stain0ID},{x.Stain1ID}"));

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
