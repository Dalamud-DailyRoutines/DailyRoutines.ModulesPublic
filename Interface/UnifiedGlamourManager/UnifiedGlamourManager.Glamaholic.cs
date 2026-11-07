using System.Globalization;
using System.Text;
using System.Threading;
using Dalamud.Utility;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

public partial class UnifiedGlamourManager
{
    private GlamaholicImporter glamaholicImporter;

    private sealed class GlamaholicImporter
    (
        UnifiedGlamourManager owner
    )
    {
        private sealed class ParseResult
        {
            public List<PlatePreset>                              Presets        = [];
            public List<PlatePreset>                              FillEmpty      = [];
            public HashSet<PlatePreset>                           InvalidPresets = [];
            public List<(string Preset, string Slot, string Item)> Skipped        = [];
        }

        public          List<PlatePreset>? Preview;
        public          string             Status = string.Empty;
        public          bool               Expanded;
        public readonly List<string>       Skipped = [];

        private CancellationTokenSource? cancellation;

        public bool IsBusy => cancellation != null;

        public void ClearPreview()
        {
            Preview = null;
            Skipped.Clear();
        }

        public void Reset()
        {
            var current = cancellation;
            cancellation = null;
            current?.Cancel();
            current?.Dispose();

            ClearPreview();
            Status = string.Empty;
        }

        public void StartPreview()
        {
            ClearPreview();

            var path = owner.config.GlamaholicImportPath.Trim().Trim('"');
            cancellation = new();
            Status       = Lang.Get("UnifiedGlamourManager-Editor-ImportReading");

            _ = LoadPresets
            (
                path,
                Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset"),
                cancellation
            );
        }

        public void CommitImport()
        {
            if (Preview is not { Count: > 0 })
                return;

            var existing  = owner.config.Presets.Select(GetPresetImportKey).ToHashSet();
            var imported  = Preview.Where(x => existing.Add(GetPresetImportKey(x))).ToList();
            var timestamp = DateTime.Now.Ticks;

            for (var index = 0; index < imported.Count; index++)
                imported[index].CreatedAt = new DateTime(timestamp + index);

            owner.config.Presets.AddRange(imported);
            if (imported.Count > 0)
                owner.selectedPreset = imported[0];

            owner.SaveConfig();
            Status = Lang.Get
            (
                "UnifiedGlamourManager-Editor-ImportDone",
                new Dictionary<string, object> { ["count"] = imported.Count }
            );
            Preview = null;
        }

        private async Task LoadPresets
        (
            string                  path,
            string                  untitledTitle,
            CancellationTokenSource source
        )
        {
            var         token  = source.Token;
            ParseResult parsed = null;
            Exception   error  = null;

            try
            {
                parsed = await Task.Run
                               (
                                   () => ParsePresets(File.ReadAllText(path, Encoding.UTF8), untitledTitle),
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
                    if (cancellation != source)
                        return;

                    cancellation = null;
                    source.Dispose();

                    if (owner.IsDisposed || token.IsCancellationRequested)
                        return;

                    if (error != null)
                    {
                        DLog.Error("UnifiedGlamourManager import failed", error);
                        Status = Lang.Get("UnifiedGlamourManager-Editor-ImportFailed");
                        return;
                    }

                    FinishImport(parsed);
                }
            ).ConfigureAwait(false);
        }

        private void FinishImport
        (
            ParseResult completed
        )
        {
            try
            {
                var parsed = completed.Presets;
                var valid  = new List<PlatePreset>();
                Skipped.Clear();

                foreach (var skipped in completed.Skipped)
                {
                    var slotName = uint.TryParse(skipped.Slot, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) ?
                                       GetPresetSlotName(slot) :
                                       skipped.Slot;

                    Skipped.Add
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
                            if (LuminaGetter.TryGetRow<Item>(entry.ItemID, out var row)                                  &&
                                LuminaGetter.TryGetRow<EquipSlotCategory>(row.EquipSlotCategory.RowId, out var category) &&
                                entry.SlotIndex < PlateSlotDefinitions.Length                                            &&
                                PlateSlotDefinitions[entry.SlotIndex].CanUse(category)                                   &&
                                (entry.Stain0ID == 0 || LuminaGetter.TryGetRow<Stain>(entry.Stain0ID, out _))            &&
                                (entry.Stain1ID == 0 || LuminaGetter.TryGetRow<Stain>(entry.Stain1ID, out _)))
                                return false;

                            var itemLabel = LuminaGetter.TryGetRow<Item>(entry.ItemID, out var invalidRow) &&
                                            TryGetItemName(invalidRow, out var invalidName) ?
                                                invalidName :
                                                entry.ItemID.ToString(CultureInfo.InvariantCulture);

                            Skipped.Add
                            (
                                Lang.Get
                                (
                                    "UnifiedGlamourManager-Editor-ImportSkippedItem",
                                    new Dictionary<string, object>
                                    {
                                        ["presetName"] = preset.Title,
                                        ["slot"]       = GetPresetSlotName(entry.SlotIndex),
                                        ["itemID"]     = itemLabel
                                    }
                                )
                            );
                            return true;
                        }
                    );

                    if (preset.Items.Count == 0 && (originalCount > 0 || completed.InvalidPresets.Contains(preset)))
                    {
                        Skipped.Add
                        (
                            Lang.Get
                            (
                                "UnifiedGlamourManager-Editor-ImportSkippedPreset",
                                new Dictionary<string, object> { ["presetName"] = preset.Title }
                            )
                        );
                        continue;
                    }

                    foreach (var entry in owner.NormalizePresetEquipment(preset))
                    {
                        Skipped.Add
                        (
                            Lang.Get
                            (
                                "UnifiedGlamourManager-Editor-ImportSkippedItem",
                                new Dictionary<string, object>
                                {
                                    ["presetName"] = preset.Title,
                                    ["slot"]       = GetPresetSlotName(entry.SlotIndex),
                                    ["itemID"]     = LuminaWrapper.GetItemName(entry.ItemID)
                                }
                            )
                        );
                    }

                    if (completed.FillEmpty.Contains(preset))
                        owner.FillEmptyPresetSlots(preset);

                    valid.Add(preset);
                }

                var existing = owner.config.Presets.Select(GetPresetImportKey).ToHashSet();
                Preview = valid.Where(x => existing.Add(GetPresetImportKey(x))).ToList();
                Status  = Lang.Get
                (
                    "UnifiedGlamourManager-Editor-ImportSummary",
                    new Dictionary<string, object>
                    {
                        ["readCount"]      = parsed.Count,
                        ["importCount"]    = Preview.Count,
                        ["duplicateCount"] = valid.Count - Preview.Count,
                        ["skippedCount"]   = Skipped.Count
                    }
                );
            }
            catch (Exception ex)
            {
                DLog.Error("UnifiedGlamourManager import failed", ex);
                Status = Lang.Get("UnifiedGlamourManager-Editor-ImportFailed");
            }
        }

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

        private static ParseResult ParsePresets
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

            var result = new ParseResult();

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

                    var slot = Array.IndexOf(SlotNames, property.Name);
                    if (slot < 0 && int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var numericSlot))
                        slot = numericSlot;

                    if (slot < 0 || slot >= SlotNames.Length || property.Value is not JObject item)
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

                    if (!TryParseDye(item["Stain1"] ?? item["StainId"], out var stain0) ||
                        !TryParseDye(item["Stain2"], out var stain1))
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

        private static bool TryParseDye
        (
            JToken   token,
            out byte dye
        )
        {
            dye = 0;
            return token == null || byte.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out dye);
        }

        #region 常量

        private static readonly string[] SlotNames =
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
}
