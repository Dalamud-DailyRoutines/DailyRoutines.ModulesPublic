using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading;
using DailyRoutines.Extensions;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public partial class UnifiedGlamourManager
{
    private          List<PlatePreset>?      glamaholicImportPreview;
    private          string                  glamaholicImportStatus  = string.Empty;
    private          bool                    glamaholicImportExpanded;
    private          CancellationTokenSource importCancellation;
    private readonly List<string>            glamaholicImportSkipped = [];

    private sealed class GlamaholicParseResult
    {
        public List<PlatePreset>                              Presets        = [];
        public List<PlatePreset>                              FillEmpty      = [];
        public HashSet<PlatePreset>                           InvalidPresets = [];
        public List<(string Preset, string Slot, string Item)> Skipped        = [];
    }

    private static string GetPresetImportKey
    (
        PlatePreset preset
    )
    {
        var title = preset.Title.Trim();
        var note  = preset.Note.Trim();
        var items = preset.Items.OrderBy(x => x.SlotIndex)
                          .ThenBy(x => x.ItemID)
                          .ThenBy(x => x.Stain0ID)
                          .ThenBy(x => x.Stain1ID)
                          .Select(x => FormattableString.Invariant($"{x.SlotIndex},{x.ItemID},{x.Stain0ID},{x.Stain1ID}"));
        return FormattableString.Invariant($"{title.Length}:{title}{note.Length}:{note}|") + string.Join(";", items);
    }

    private static GlamaholicParseResult ParseGlamaholicPresets
    (
        string json,
        string untitledTitle
    )
    {
        var root  = JToken.Parse(json);
        var nodes = root is JObject configuration && configuration["Plates"] is JArray plates ?
                        plates :
                        root as JArray;
        if (nodes == null)
            throw new FormatException("Invalid Glamaholic root");

        var result = new GlamaholicParseResult();
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
            {
                result.Skipped.Add((folder, "", node.ToString()));
                return;
            }

            if (value["Children"] is JArray children)
            {
                var path = string.IsNullOrEmpty(folder) ?
                               value["Name"]?.ToString() ?? string.Empty :
                               $"{folder}/{value["Name"]}";
                foreach (var child in children)
                    Visit(child, path);

                return;
            }

            var plate = value["Plate"] as JObject ?? value;
            var title = plate["Name"]?.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
                title = untitledTitle;
            if (plate["Items"] is not JObject equipment)
            {
                result.Skipped.Add((title, "", plate["Items"]?.ToString() ?? ""));
                return;
            }

            var tags = plate["Tags"] is JArray tagArray ?
                           string.Join(", ", tagArray.Select(x => x.ToString())) :
                           string.Empty;
            var preset = new PlatePreset
            {
                Title = title,
                Note  = string.Join("; ", new[] { folder, tags }.Where(x => !string.IsNullOrEmpty(x))),
                Race  = string.Empty,
                Sex   = string.Empty
            };
            var skippedCount = result.Skipped.Count;
            foreach (var property in equipment.Properties())
            {
                if (property.Name == "$type")
                    continue;

                var slot = Array.IndexOf(slots, property.Name);
                if (slot < 0 && int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var numericSlot))
                    slot = numericSlot;
                if (slot < 0 || slot >= slots.Length || property.Value is not JObject item)
                {
                    result.Skipped.Add((preset.Title, property.Name, property.Value.ToString()));
                    continue;
                }

                if (!uint.TryParse
                    (
                        item["ItemId"]?.ToString(),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var itemID
                    ))
                {
                    result.Skipped.Add((preset.Title, slot.ToString(CultureInfo.InvariantCulture), item["ItemId"]?.ToString() ?? ""));
                    continue;
                }

                if (itemID == 0)
                    continue;

                if (!TryParseGlamaholicDye(item["Stain1"] ?? item["StainId"], out var stain0) ||
                    !TryParseGlamaholicDye(item["Stain2"], out var stain1))
                {
                    result.Skipped.Add((preset.Title, slot.ToString(CultureInfo.InvariantCulture), item.ToString()));
                    continue;
                }
                preset.Items.Add(new((uint)slot, ItemUtil.GetBaseId(itemID).ItemId, stain0, stain1));
            }

            if (preset.Items.Count == 0 && result.Skipped.Count > skippedCount)
                result.InvalidPresets.Add(preset);
            if (bool.TryParse(plate["FillWithNewEmperor"]?.ToString(), out var fill) && fill)
                result.FillEmpty.Add(preset);
            result.Presets.Add(preset);
        }
    }

    private static bool TryParseGlamaholicDye
    (
        JToken   token,
        out byte dye
    )
    {
        dye = 0;
        return token == null || byte.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out dye);
    }

    private void ResetGlamaholicImport()
    {
        var cancellation   = importCancellation;
        importCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        glamaholicImportPreview = null;
        glamaholicImportSkipped.Clear();
        glamaholicImportStatus = string.Empty;
    }

    private async Task LoadGlamaholicPresets
    (
        string                  path,
        string                  untitledTitle,
        CancellationTokenSource cancellation
    )
    {
        var                   token  = cancellation.Token;
        GlamaholicParseResult parsed = null;
        Exception             error  = null;
        try
        {
            parsed = await Task.Run
                     (
                         () => ParseGlamaholicPresets(File.ReadAllText(path, Encoding.UTF8), untitledTitle),
                         token
                     ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        await IFramework.Instance().RunOnFrameworkThread
        (() =>
            {
                if (importCancellation != cancellation)
                    return;

                importCancellation = null;
                cancellation.Dispose();
                if (IsDisposed || token.IsCancellationRequested)
                    return;

                if (error != null)
                {
                    DLog.Error("UnifiedGlamourManager import failed", error);
                    glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Editor-ImportFailed");
                    return;
                }

                FinishGlamaholicImport(parsed);
            }
        ).ConfigureAwait(false);
    }

    private void FinishGlamaholicImport
    (
        GlamaholicParseResult completed
    )
    {
        try
        {
            var parsed = completed.Presets;
            var valid  = new List<PlatePreset>();
            glamaholicImportSkipped.Clear();

            foreach (var skipped in completed.Skipped)
            {
                var slotName = uint.TryParse(skipped.Slot, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) ?
                                   GetPresetSlotName(slot) :
                                   skipped.Slot;
                glamaholicImportSkipped.Add
                (
                    Lang.Get
                    (
                        "UnifiedGlamourManager-Editor-ImportSkippedData",
                        new Dictionary<string, object>
                        {
                            ["presetName"] = skipped.Preset,
                            ["slot"]       = slotName,
                            ["value"]      = skipped.Item
                        }
                    )
                );
            }

            foreach (var preset in parsed)
            {
                var originalCount = preset.Items.Count;
                preset.Items.RemoveAll
                (entry =>
                    {
                        if (LuminaGetter.TryGetRow<Item>(entry.ItemID, out var row) &&
                            LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) &&
                            IsEquipSlotCategoryCompatibleWithPlateSlot(category, entry.SlotIndex) &&
                            (entry.Stain0ID == 0 || LuminaGetter.TryGetRow<Stain>(entry.Stain0ID, out _)) &&
                            (entry.Stain1ID == 0 || LuminaGetter.TryGetRow<Stain>(entry.Stain1ID, out _)))
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
                                    ["slot"]       = GetPresetSlotName(entry.SlotIndex),
                                    ["itemID"]     = entry.ItemID
                                }
                            )
                        );
                        return true;
                    }
                );
                if (preset.Items.Count == 0 && (originalCount > 0 || completed.InvalidPresets.Contains(preset)))
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

                foreach (var entry in NormalizePresetEquipment(preset))
                {
                    glamaholicImportSkipped.Add
                    (
                        Lang.Get
                        (
                            "UnifiedGlamourManager-Editor-ImportSkippedItem",
                            new Dictionary<string, object>
                            {
                                ["presetName"] = preset.Title,
                                ["slot"]       = GetPresetSlotName(entry.SlotIndex),
                                ["itemID"]     = entry.ItemID
                            }
                        )
                    );
                }

                if (completed.FillEmpty.Contains(preset))
                    FillEmptyPresetSlots(preset);
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
        if (!glamaholicImportExpanded)
            return;

        using (ImRaii.Disabled(importCancellation != null))
        {
            if (ImGui.InputText
                (
                    Lang.Get("UnifiedGlamourManager-Editor-ImportPath"),
                    ref config.GlamaholicImportPath,
                    1024
                ))
            {
                glamaholicImportPreview = null;
                glamaholicImportSkipped.Clear();
                SaveConfig();
            }

            if (ImGui.Button(Lang.Get("Preview")))
            {
                glamaholicImportPreview = null;
                glamaholicImportSkipped.Clear();
                var path               = config.GlamaholicImportPath.Trim().Trim('"');
                importCancellation     = new();
                glamaholicImportStatus = Lang.Get("UnifiedGlamourManager-Editor-ImportReading");
                _ = LoadGlamaholicPresets
                (
                    path,
                    Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset"),
                    importCancellation
                );
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
                    SelectPreset(imported[0], false);
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
