using System.Globalization;
using System.Numerics;
using System.Text;
using DailyRoutines.Extensions;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using ItemSheet = Lumina.Excel.Sheets.Item;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager
{
    private string glamaholicImportPath = string.Empty;

    private          List<PlatePreset>?       glamaholicImportPreview;
    private          string                   glamaholicImportStatus  = string.Empty;
    private          bool                     glamaholicImportExpanded;
    private          Task<List<PlatePreset>>? importReadTask;
    private readonly List<string>             glamaholicImportSkipped = [];

    private static string GetPresetImportKey
    (
        PlatePreset preset
    ) => new JArray
    (
        preset.Title.Trim(),
        preset.Note.Trim(),
        new JArray
        (
            preset.Items.OrderBy(x => x.SlotIndex)
                  .Select(x => new JArray(x.SlotIndex, x.ItemID, x.Stain0ID, x.Stain1ID))
        )
    ).ToString(Formatting.None);

    private static List<PlatePreset> ParseGlamaholicPresets
    (
        string json
    )
    {
        var root  = JToken.Parse(json);
        var nodes = root is JObject configuration && configuration["Plates"] is JArray plates ?
                        plates :
                        root as JArray;
        if (nodes == null)
            throw new FormatException("Invalid Glamaholic root");

        var result = new List<PlatePreset>();
        var slots  = new[]
        {
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
        };

        foreach (var node in nodes)
            Visit(node, string.Empty);

        return result;

        void Visit
        (
            JToken node,
            string folder
        )
        {
            if (node is not JObject value)
                throw new FormatException("Invalid Glamaholic node");

            if (value["Children"] is JArray children)
            {
                var path = string.IsNullOrEmpty(folder) ?
                               (string?)value["Name"] ?? string.Empty :
                               $"{folder}/{value["Name"]}";
                foreach (var child in children)
                    Visit(child, path);

                return;
            }

            var plate = value["Plate"] as JObject ?? value;
            if (plate["Items"] is not JObject equipment)
                throw new FormatException("Missing Glamaholic items");

            var tags = plate["Tags"] is JArray tagArray ?
                           string.Join(", ", tagArray.Select(x => x.ToString())) :
                           string.Empty;
            var preset = new PlatePreset
            {
                Title = (string?)plate["Name"] ?? "Glamaholic",
                Note  = string.Join
                (
                    "; ",
                    new[]
                    {
                        string.IsNullOrEmpty(folder) ?
                            "Glamaholic" :
                            $"Glamaholic: {folder}",
                        string.IsNullOrEmpty(tags) ?
                            string.Empty :
                            $"Tags: {tags}"
                    }.Where(x => x.Length > 0)
                ),
                Race = string.Empty,
                Sex  = string.Empty
            };
            foreach (var property in equipment.Properties())
            {
                if (property.Name == "$type")
                    continue;

                var slot = Array.IndexOf(slots, property.Name);
                if (slot < 0 && int.TryParse(property.Name, out var numericSlot))
                    slot = numericSlot;
                if (slot < 0 || slot >= slots.Length || property.Value is not JObject item)
                    throw new FormatException($"Invalid slot: {property.Name}");

                if (!uint.TryParse
                    (
                        item["ItemId"]?.ToString(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var itemID
                    ))
                {
                    throw new FormatException($"Invalid item ID in slot {property.Name}");
                }

                if (itemID == 0)
                    continue;

                preset.Items.Add
                (
                    new
                    (
                        (uint)slot,
                        ItemUtil.GetBaseId(itemID).ItemId,
                        ParseGlamaholicDye(item["Stain1"] ?? item["StainId"], property.Name),
                        ParseGlamaholicDye(item["Stain2"], property.Name)
                    )
                );
            }

            if ((bool?)plate["FillWithNewEmperor"] == true)
                FillEmptyPresetSlots(preset);

            result.Add(preset);
        }
    }

    private static byte ParseGlamaholicDye
    (
        JToken? token,
        string  slot
    )
    {
        if (token == null)
            return 0;

        if (!byte.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var dye))
            throw new FormatException($"Invalid dye ID in slot {slot}");

        return dye;
    }

    private void FinishGlamaholicRead()
    {
        if (importReadTask == null || !importReadTask.IsCompleted)
            return;

        var completed  = importReadTask;
        importReadTask = null;
        try
        {
            var parsed = completed.GetAwaiter().GetResult();
            var valid  = new List<PlatePreset>();
            glamaholicImportSkipped.Clear();
            foreach (var preset in parsed)
            {
                var originalCount = preset.Items.Count;
                preset.Items.RemoveAll
                (entry =>
                    {
                        if (LuminaGetter.TryGetRow<ItemSheet>(entry.ItemID, out var row) &&
                            LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) &&
                            IsEquipSlotCategoryCompatibleWithPlateSlot(category, entry.SlotIndex))
                        {
                            return false;
                        }

                        glamaholicImportSkipped.Add
                        (
                            Lang.Get
                            (
                                "UnifiedGlamourManager-Editor-ImportSkippedItem",
                                new Dictionary<string, object>
                                {
                                    ["presetName"] = preset.Title,
                                    ["slot"]       = entry.SlotIndex,
                                    ["itemID"]     = entry.ItemID
                                }
                            )
                        );
                        return true;
                    }
                );
                if (originalCount > 0 && preset.Items.Count == 0)
                {
                    glamaholicImportSkipped.Add
                    (
                        Lang.Get
                        (
                            "UnifiedGlamourManager-Editor-ImportSkippedPreset",
                            new Dictionary<string, object> { ["presetName"] = preset.Title }
                        )
                    );
                    continue;
                }

                NormalizePresetEquipment(preset);
                valid.Add(preset);
            }

            var existing            = config.Presets.Select(GetPresetImportKey).ToHashSet();
            glamaholicImportPreview = valid.Where(x => existing.Add(GetPresetImportKey(x))).ToList();
            glamaholicImportStatus  = Lang.Get
            (
                "UnifiedGlamourManager-Editor-ImportSummary",
                new Dictionary<string, object>
                {
                    ["readCount"]      = parsed.Count,
                    ["importCount"]    = glamaholicImportPreview.Count,
                    ["duplicateCount"] = valid.Count - glamaholicImportPreview.Count,
                    ["skippedCount"]   = glamaholicImportSkipped.Count
                }
            );
        }
        catch (Exception ex)
        {
            DLog.Error("UnifiedGlamourManager import failed", ex);
            glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Editor-ImportFailed");
        }
    }

    private void DrawGlamaholicImport()
    {
        FinishGlamaholicRead();
        if (!glamaholicImportExpanded)
            return;

        using (ImRaii.Disabled(importReadTask != null))
        {
            if (ImGui.InputText
                (
                    Lang.Get("UnifiedGlamourManager-Editor-ImportPath"),
                    ref glamaholicImportPath,
                    1024
                ))
            {
                glamaholicImportPreview = null;
                glamaholicImportSkipped.Clear();
            }

            if (ImGui.Button(Lang.Get("Preview")))
            {
                glamaholicImportPreview = null;
                glamaholicImportSkipped.Clear();
                var path               = glamaholicImportPath.Trim().Trim('"');
                importReadTask         = Task.Run(() => ParseGlamaholicPresets(File.ReadAllText(path, Encoding.UTF8)));
                glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Editor-ImportReading");
            }
        }

        ImGui.TextWrapped(glamaholicImportStatus);
        if (glamaholicImportSkipped.Count > 0 &&
            ImGui.CollapsingHeader(Lang.Get("UnifiedGlamourManager-Editor-ImportSkipped")))
        {
            using var skipped = ImRaii.Child("##GlamaholicSkipped", new Vector2(0, 100 * GlobalUIScale), true);
            if (skipped)
            {
                foreach (var message in glamaholicImportSkipped)
                    ImGui.TextUnformatted(message);
            }
        }

        if (glamaholicImportPreview is { Count: > 0 })
        {
            using (var child = ImRaii.Child
                   (
                       "##GlamaholicImportPreview",
                       new Vector2(0, 100 * GlobalUIScale),
                       true
                   ))
            {
                if (child)
                {
                    foreach (var preset in glamaholicImportPreview)
                        ImGui.TextUnformatted(preset.Title);
                }
            }

            if (ImGui.Button(Lang.Get("Import")))
            {
                var existing  = config.Presets.Select(GetPresetImportKey).ToHashSet();
                var imported  = glamaholicImportPreview.Where(x => existing.Add(GetPresetImportKey(x))).ToList();
                var timestamp = DateTime.Now.Ticks;
                for (var index = 0; index < imported.Count; index++)
                    imported[index].CreatedAt = new DateTime(timestamp + index);
                config.Presets.AddRange(imported);
                if (imported.Count > 0)
                    SelectPreset(imported[0], true);
                SaveConfig();
                glamaholicImportStatus = Lang.Get
                (
                    "UnifiedGlamourManager-Editor-ImportDone",
                    new Dictionary<string, object> { ["count"] = imported.Count }
                );
                glamaholicImportPreview = null;
            }
        }
    }
}
