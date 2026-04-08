using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Helpers;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic;

public class ContentFinderCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("ContentFinderCommandTitle"),
        Description = Lang.Get("ContentFinderCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        CommandManager.Instance().AddCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("ContentFinderCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.Instance().RemoveCommand(COMMAND);

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{Lang.Get("Command")}:");

        ImGui.SameLine();
        ImGui.TextUnformatted($"{COMMAND} {Lang.Get("ContentFinderCommand-CommandHelp")}");

        using (ImRaii.PushIndent())
            ImGui.TextUnformatted(Lang.Get("ContentFinderCommand-ArgsHelp"));

        ImGui.Spacing();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("ContentFinderCommand-DutyType"));
        using (ImRaii.PushIndent())
            RenderTwoRowsTable("DutyType", DutyTypes, x => x.Value.desc);

        ImGui.Spacing();

        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), Lang.Get("ContentFinderCommand-Options"));
        using (ImRaii.PushIndent())
            RenderTwoRowsTable("Options", OptionSetters, x => x.Value.desc);
    }

    private static void RenderTwoRowsTable<T1, T2>(string id, IDictionary<T1, T2> source, Func<KeyValuePair<T1, T2>, string> right) where T1 : notnull
    {
        var       tableSize = new Vector2(ImGui.GetContentRegionAvail().X * 0.75f, 0);
        using var table     = ImRaii.Table(id, 2, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("键", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("值", ImGuiTableColumnFlags.WidthStretch, 30);

        foreach (var rowData in source)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{rowData.Key}");
            ImGuiOm.ClickToCopyAndNotify(rowData.Key.ToString());

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{right(rowData)}");
        }
    }

    private static void OnCommand(string command, string args)
    {
        var arguments = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Length is not (2 or 3)) return;

        if (!DutyTypes.TryGetValue(arguments[0], out var dutyType))
            return;

        if (!TryParseContent(arguments[1], dutyType.dutyType, out var contentIds))
            return;

        var options = new ContentsFinderOption { Config817to820 = true };
        if (dutyType.dutyType == DutyType.Normal && arguments.Length == 3 && !TryParseContentSettings(arguments[2], ref options))
            return;

        ExecuteDutyRequest(dutyType.dutyType, contentIds, options);
    }

    private static void ExecuteDutyRequest(DutyType dutyType, uint[] contentIDs, ContentsFinderOption options)
    {
        switch (dutyType)
        {
            case DutyType.Normal:
                ContentsFinderHelper.RequestDutyNormal(contentIDs, options);
                break;
            case DutyType.Roulette:
                ContentsFinderHelper.RequestDutyRoulette((ushort)contentIDs[0], options);
                break;
            case DutyType.Support:
                ContentsFinderHelper.RequestDutySupport(contentIDs[0]);
                break;
        }
    }

    private static bool TryParseContent(string input, DutyType expectedType, out uint[] contentID)
    {
        contentID = [];
        if (string.IsNullOrWhiteSpace(input)) return false;

        var parts = input.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 5 || expectedType is DutyType.Roulette or DutyType.Support && parts.Length > 1)
            return false;

        var results = new List<uint>();

        foreach (var part in parts)
        {
            if (TryParseDirectID(part, expectedType, out var id))
            {
                results.Add(id);
                continue;
            }

            if (TryParseName(part, expectedType, out var foundId))
            {
                results.Add(foundId);
                continue;
            }

            return false;
        }

        if (results.Count == 0) return false;

        contentID = [..results];
        return true;
    }

    private static bool TryParseDirectID(string input, DutyType expectedType, out uint id)
    {
        if (!uint.TryParse(input, out id)) return false;

        return expectedType switch
        {
            DutyType.Normal   => LuminaGetter.GetRow<ContentFinderCondition>(id) != null,
            DutyType.Roulette => LuminaGetter.GetRow<ContentRoulette>(id)        != null,
            DutyType.Support  => LuminaGetter.GetRow<DawnContent>(id)            != null,
            _                 => false
        };
    }

    private static bool TryParseName(string input, DutyType expectedType, out uint id)
    {
        id = 0;

        switch (expectedType)
        {
            case DutyType.Normal:
                var contentByName = LuminaGetter.Get<ContentFinderCondition>()
                                                .FirstOrDefault
                                                    (x => x.Name.ToString().Replace(" ", string.Empty).Contains(input, StringComparison.OrdinalIgnoreCase));

                if (contentByName.RowId != 0)
                {
                    id = contentByName.RowId;
                    return true;
                }

                break;
            case DutyType.Roulette:
                var rouletteByName = LuminaGetter.Get<ContentRoulette>()
                                                 .FirstOrDefault
                                                     (x => x.Name.ToString().Replace(" ", string.Empty).Contains(input, StringComparison.OrdinalIgnoreCase));

                if (rouletteByName.RowId != 0)
                {
                    id = rouletteByName.RowId;
                    return true;
                }

                break;
            case DutyType.Support:
                var supportByName = LuminaGetter.Get<DawnContent>()
                                                .FirstOrDefault
                                                (x => x.Content.Value.Name.ToString().Replace(" ", string.Empty).Contains
                                                     (input, StringComparison.OrdinalIgnoreCase)
                                                );

                if (supportByName.RowId != 0)
                {
                    id = supportByName.RowId;
                    return true;
                }

                break;
        }

        return false;
    }

    private static bool TryParseContentSettings(string input, ref ContentsFinderOption options)
    {
        if (string.IsNullOrWhiteSpace(input)) return true;

        var wrapper = new OptionsWrapper { Options = options };
        var parts   = input.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (!OptionSetters.TryGetValue(part, out var setter))
                return false;
            setter.action(wrapper);
        }

        options = wrapper.Options;
        return true;
    }

    private sealed class OptionsWrapper
    {
        public ContentsFinderOption Options;
    }

    private enum DutyType
    {
        Normal,
        Roulette,
        Support
    }

    #region 常量

    private const string COMMAND = "/pdrduty";

    private static readonly FrozenDictionary<string, (DutyType dutyType, string desc)> DutyTypes =
        new Dictionary<string, (DutyType dutyType, string desc)>(StringComparer.OrdinalIgnoreCase)
        {
            ["normal"]   = (DutyType.Normal, Lang.Get("ContentFinderCommand-DutyType-Normal")),
            ["n"]        = (DutyType.Normal, Lang.Get("ContentFinderCommand-DutyType-Normal")),
            ["roulette"] = (DutyType.Roulette, Lang.Get("ContentFinderCommand-DutyType-Roulette")),
            ["r"]        = (DutyType.Roulette, Lang.Get("ContentFinderCommand-DutyType-Roulette")),
            ["support"]  = (DutyType.Support, LuminaWrapper.GetAddonText(14804)),
            ["s"]        = (DutyType.Support, LuminaWrapper.GetAddonText(14804))
        }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, (Action<OptionsWrapper> action, string desc)> OptionSetters =
        new Dictionary<string, (Action<OptionsWrapper> action, string desc)>(StringComparer.OrdinalIgnoreCase)
        {
            ["supply"] = (wrapper => wrapper.Options.Supply = true, LuminaWrapper.GetAddonText(2519)),
            ["unrest"] = (wrapper => wrapper.Options.UnrestrictedParty = true, LuminaWrapper.GetAddonText(10008)),
            ["minil"] = (wrapper => wrapper.Options.MinimalIL = true, LuminaWrapper.GetAddonText(10010)),
            ["sync"] = (wrapper => wrapper.Options.LevelSync = true, LuminaWrapper.GetAddonText(12696)),
            ["silence"] = (wrapper => wrapper.Options.SilenceEcho = true, LuminaWrapper.GetAddonText(2266)),
            ["explorer"] = (wrapper => wrapper.Options.ExplorerMode = true, LuminaWrapper.GetAddonText(13038)),
            ["limitleveling"] = (wrapper => wrapper.Options.IsLimitedLevelingRoulette = true, LuminaWrapper.GetAddonText(13030)),
            ["lootgreed"] = (wrapper => wrapper.Options.LootRules = ContentsFinder.LootRule.GreedOnly, LuminaWrapper.GetAddonText(102627)),
            ["lootmaster"] = (wrapper => wrapper.Options.LootRules = ContentsFinder.LootRule.Lootmaster, LuminaWrapper.GetAddonText(11087)),
            ["lootnormal"] = (wrapper => wrapper.Options.LootRules = ContentsFinder.LootRule.Normal, LuminaWrapper.GetAddonText(10100))
        }.ToFrozenDictionary();

    #endregion
}
