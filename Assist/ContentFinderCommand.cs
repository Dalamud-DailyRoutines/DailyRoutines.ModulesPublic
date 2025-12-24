using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Lumina.Excel.Sheets;
using ContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace DailyRoutines.ModulesPublic;

public class ContentFinderCommand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("ContentFinderCommandTitle"),
        Description = GetLoc("ContentFinderCommandDescription", Command),
        Category    = ModuleCategories.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private const string Command = "/pdrduty";

    protected override void Init() =>
        CommandManager.AddCommand(Command, new(OnCommand) { HelpMessage = GetLoc("ContentFinderCommand-CommandHelp") });

    protected override void Uninit() =>
        CommandManager.RemoveCommand(Command);

    protected override void ConfigUI()
    {
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), $"{GetLoc("Command")}:");

        ImGui.SameLine();
        ImGui.Text($"{Command} {GetLoc("ContentFinderCommand-CommandHelp")}");

        using (ImRaii.PushIndent())
            ImGui.Text(GetLoc("ContentFinderCommand-ArgsHelp"));

        ImGui.Spacing();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("ContentFinderCommand-DutyType"));
        using (ImRaii.PushIndent())
            RenderTwoRowsTable("DutyType", DutyTypes, x => x.Value.desc);

        ImGui.Spacing();
        
        ImGui.TextColored(KnownColor.LightSkyBlue.ToVector4(), GetLoc("ContentFinderCommand-Options"));
        using (ImRaii.PushIndent())
            RenderTwoRowsTable("Options", OptionSetters, x => x.Value.desc);
    }

    private static void RenderTwoRowsTable<T1, T2>(string id, Dictionary<T1, T2> source, Func<KeyValuePair<T1, T2>, string> right) where T1 : notnull
    {
        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X * 0.75f, 0);
        using var table = ImRaii.Table(id, 2, ImGuiTableFlags.Borders, tableSize);
        if (!table) return;

        ImGui.TableSetupColumn("键", ImGuiTableColumnFlags.WidthStretch, 20);
        ImGui.TableSetupColumn("值", ImGuiTableColumnFlags.WidthStretch, 30);

        foreach (var rowData in source)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{rowData.Key}");
            if (ImGui.IsItemHovered()) 
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGuiOm.ClickToCopy(rowData.Key.ToString(), ImGuiMouseButton.Left);
            if (ImGui.IsItemClicked()) 
                NotificationSuccess(GetLoc("CopiedToClipboard"));

            ImGui.TableNextColumn();
            ImGui.Text($"{right(rowData)}");
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
                RequestDutyNormal(contentIDs, options);
                break;
            case DutyType.Roulette:
                RequestDutyRoulette((ushort)contentIDs[0], options);
                break;
            case DutyType.Support:
                RequestDutySupport(contentIDs[0]);
                break;
        }
    }

    private static bool TryParseContent(string input, DutyType expectedType, out uint[] contentID)
    {
        contentID = [];
        if (string.IsNullOrWhiteSpace(input)) return false;

        var parts = input.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 5 || (expectedType is (DutyType.Roulette or DutyType.Support) && parts.Length > 1))
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
                    .FirstOrDefault(x => x.Name.ExtractText().Replace(" ", string.Empty).Contains(input, StringComparison.OrdinalIgnoreCase));
                if (contentByName.RowId != 0)
                {
                    id = contentByName.RowId;
                    return true;
                }
                break;
            case DutyType.Roulette:
                var rouletteByName = LuminaGetter.Get<ContentRoulette>()
                    .FirstOrDefault(x => x.Name.ExtractText().Replace(" ", string.Empty).Contains(input, StringComparison.OrdinalIgnoreCase));
                if (rouletteByName.RowId != 0)
                {
                    id = rouletteByName.RowId;
                    return true;
                }
                break;
            case DutyType.Support:
                var supportByName = LuminaGetter.Get<DawnContent>()
                    .FirstOrDefault(x => x.Content.Value.Name.ExtractText().Replace(" ", string.Empty).Contains(input, StringComparison.OrdinalIgnoreCase));
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
        var parts = input.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (!OptionSetters.TryGetValue(part, out var setter))
                return false;
            setter.action(wrapper);
        }

        options = wrapper.Options;
        return true;
    }
    
    private static readonly Dictionary<string, (DutyType dutyType, string desc)> DutyTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["normal"]   = (DutyType.Normal, GetLoc("ContentFinderCommand-DutyType-Normal")),
            ["n"]        = (DutyType.Normal, GetLoc("ContentFinderCommand-DutyType-Normal")),
            ["roulette"] = (DutyType.Roulette, GetLoc("ContentFinderCommand-DutyType-Roulette")),
            ["r"]        = (DutyType.Roulette, GetLoc("ContentFinderCommand-DutyType-Roulette")),
            ["support"]  = (DutyType.Support, LuminaGetter.GetRow<Addon>(14804)!.Value.Text.ExtractText()),
            ["s"]        = (DutyType.Support, LuminaGetter.GetRow<Addon>(14804)!.Value.Text.ExtractText())
        };

    private static readonly Dictionary<string, (Action<OptionsWrapper> action, string desc)> OptionSetters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["supply"] = (wrapper => wrapper.Options.Supply = true,
                             LuminaGetter.GetRow<Addon>(2519)!.Value.Text.ExtractText()),
            ["unrest"] = (wrapper => wrapper.Options.UnrestrictedParty = true,
                             LuminaGetter.GetRow<Addon>(10008)!.Value.Text.ExtractText()),
            ["minil"] = (wrapper => wrapper.Options.MinimalIL = true,
                            LuminaGetter.GetRow<Addon>(10010)!.Value.Text.ExtractText()),
            ["sync"] = (wrapper => wrapper.Options.LevelSync = true,
                           LuminaGetter.GetRow<Addon>(12696)!.Value.Text.ExtractText()),
            ["silence"] = (wrapper => wrapper.Options.SilenceEcho = true,
                              LuminaGetter.GetRow<Addon>(2266)!.Value.Text.ExtractText()),
            ["explorer"] = (wrapper => wrapper.Options.ExplorerMode = true,
                               LuminaGetter.GetRow<Addon>(13038)!.Value.Text.ExtractText()),
            ["limitleveling"] = (wrapper => wrapper.Options.IsLimitedLevelingRoulette = true,
                                    LuminaGetter.GetRow<Addon>(13030)!.Value.Text.ExtractText()),
            ["lootgreed"] = (wrapper => wrapper.Options.LootRules = ContentsFinder.LootRule.GreedOnly,
                                LuminaGetter.GetRow<Addon>(102627)!.Value.Text.ExtractText()),
            ["lootmaster"] = (wrapper => wrapper.Options.LootRules = ContentsFinder.LootRule.Lootmaster,
                                 LuminaGetter.GetRow<Addon>(11087)!.Value.Text.ExtractText()),
            ["lootnormal"] = (wrapper => wrapper.Options.LootRules = ContentsFinder.LootRule.Normal,
                                 LuminaGetter.GetRow<Addon>(10100)!.Value.Text.ExtractText())
        };
    
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
}
