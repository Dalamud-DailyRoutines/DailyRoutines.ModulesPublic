using System;
using System.Collections.Generic;
using System.Numerics;
using DailyRoutines.Abstracts;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class PlayerTargetInfoExpand : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title           = GetLoc("PlayerTargetInfoExpandTitle"),
        Description     = GetLoc("PlayerTargetInfoExpandDescription"),
        Category        = ModuleCategories.UIOptimization,
        ModulesConflict = ["LiveAnonymousMode"]
    };

    public override ModulePermission Permission { get; } = new() { NeedAuth = true };

    private static readonly List<Payload> Payloads =
    [
        new("/Name/", LuminaWrapper.GetAddonText(6382), c => c.Name.TextValue),
        new("/Job/", LuminaWrapper.GetAddonText(294), c => c.ClassJob.ValueNullable?.Name.ToString() ?? LuminaGetter.GetRowOrDefault<ClassJob>(0).Name.ToString()),
        new("/Level/", LuminaWrapper.GetAddonText(335), c => c.Level.ToString()),
        new("/FCTag/", LuminaWrapper.GetAddonText(297), c => c.CompanyTag.TextValue),
        new
        (
            "/OnlineStatus/",
            GetLoc("OnlineStatus"),
            c => string.IsNullOrEmpty(c.OnlineStatus.ValueNullable?.Name.ToString())
                     ? LuminaGetter.GetRowOrDefault<OnlineStatus>(47).Name.ToString()
                     : c.OnlineStatus.ValueNullable?.Name.ToString()
        ),
        new("/Mount/", LuminaWrapper.GetAddonText(4964), c => LuminaGetter.GetRowOrDefault<Mount>(c.ToStruct()->Mount.MountId).Singular.ToString()),
        new("/HomeWorld/", LuminaWrapper.GetAddonText(4728), c => LuminaGetter.GetRowOrDefault<World>(c.ToStruct()->HomeWorld).Name.ToString()),
        new
        (
            "/Emote/",
            LuminaWrapper.GetAddonText(780),
            c => LuminaGetter.GetRowOrDefault<Emote>(c.ToStruct()->EmoteController.EmoteId).Name.ToString()
        ),
        new("/TargetsTarget/", GetLoc("TargetOfTarget"), c => c.TargetObject?.Name.TextValue ?? ""),
        new("/ShieldValue/", GetLoc("Sheild"), c => c.ShieldPercentage.ToString()),
        new("/CurrentHP/", LuminaWrapper.GetAddonText(232), c => c.CurrentHp.ToString()),
        new("/MaxHP/", GetLoc("MaxHP"), c => c.MaxHp.ToString()),
        new("/CurrentMP/", LuminaWrapper.GetAddonText(233), c => c.CurrentMp.ToString()),
        new("/MaxMP/", GetLoc("MaxMP"), c => c.MaxMp.ToString()),
        new("/CurrentCP/", LuminaWrapper.GetAddonText(1004), c => c.CurrentCp.ToString()),
        new("/MaxCP/", GetLoc("MaxCP"), c => c.MaxCp.ToString()),
        new("/CurrentGP/", LuminaWrapper.GetAddonText(1003), c => c.CurrentGp.ToString()),
        new("/MaxGP/", GetLoc("MaxGP"), c => c.MaxGp.ToString())
    ];

    private static Config ModuleConfig = null!;

    protected override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_TargetInfo", UpdateTargetInfo);
        DService.Instance().AddonLifecycle.RegisterListener
        (
            AddonEvent.PostRequestedUpdate,
            "_TargetInfoMainTarget",
            UpdateTargetInfoMainTarget
        );

        DService.Instance().AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "_FocusTargetInfo", UpdateFocusTargetInfo);
    }

    protected override void ConfigUI()
    {
        var tableSize = new Vector2(ImGui.GetContentRegionAvail().X / 2, 0);
        
        using (ImRaii.Group())
        {
            DrawInputAndPreviewText(Lang.Get("Target"), ref ModuleConfig.TargetPattern);
            DrawInputAndPreviewText
            (
                Lang.Get("PlayerTargetInfoExpand-TargetsTarget"),
                ref ModuleConfig.TargetsTargetPattern
            );

            DrawInputAndPreviewText
            (
                Lang.Get("PlayerTargetInfoExpand-FocusTarget"),
                ref ModuleConfig.FocusTargetPattern
            );
        }
        
        ImGui.SameLine();

        using (var table = ImRaii.Table("PayloadDisplay", 2, ImGuiTableFlags.Borders, tableSize / 1.5f))
        {
            if (table)
            {
                ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Lang.Get("PlayerTargetInfoExpand-AvailablePayload"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(Lang.Get("Description"));

                foreach (var payload in Payloads)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(payload.Placeholder);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(payload.Description);
                }
            }
        }

        return;

        void DrawInputAndPreviewText(string categoryTitle, ref string config)
        {
            using (var categoryTable = ImRaii.Table(categoryTitle, 2, ImGuiTableFlags.BordersOuter, tableSize))
            {
                if (categoryTable)
                {
                    ImGui.TableSetupColumn("###Category", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("真得要六个字").X);
                    ImGui.TableSetupColumn("###Content",  ImGuiTableColumnFlags.None,       50);

                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted($"{categoryTitle}:");

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1f);
                    if (ImGui.InputText($"###{categoryTitle}", ref config, 64))
                        SaveConfig(ModuleConfig);

                    if (DService.Instance().ObjectTable.LocalPlayer is ICharacter chara)
                    {
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextUnformatted($"{Lang.Get("Example")}:");

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(ReplacePatterns(config, Payloads, chara));
                    }
                }
            }

            ImGui.Spacing();
        }
    }

    private static void UpdateTargetInfo(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsVisible) return;

        // 目标
        var target = TargetManager.Target;
        var node0  = addon->GetTextNodeById(16);
        if (node0 != null && target is ICharacter { ObjectKind: ObjectKind.Player } chara0)
            node0->SetText(ReplacePatterns(ModuleConfig.TargetPattern, Payloads, chara0));

        // 目标的目标
        var targetsTarget = TargetManager.Target?.TargetObject;
        var node1         = addon->GetTextNodeById(7);
        if (node1 != null && targetsTarget is ICharacter { ObjectKind: ObjectKind.Player } chara1)
            node1->SetText(ReplacePatterns(ModuleConfig.TargetsTargetPattern, Payloads, chara1));
    }

    private static void UpdateTargetInfoMainTarget(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsVisible) return;

        // 目标
        var target = TargetManager.Target;
        var node0  = addon->GetTextNodeById(10);
        if (node0 != null && target is ICharacter { ObjectKind: ObjectKind.Player } chara0)
            node0->SetText(ReplacePatterns(ModuleConfig.TargetPattern, Payloads, chara0));

        // 目标的目标
        var targetsTarget = TargetManager.Target?.TargetObject;
        var node1         = addon->GetTextNodeById(7);
        if (node1 != null && targetsTarget is ICharacter { ObjectKind: ObjectKind.Player } chara1)
            node1->SetText(ReplacePatterns(ModuleConfig.TargetsTargetPattern, Payloads, chara1));
    }

    private static void UpdateFocusTargetInfo(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsVisible) return;

        // 焦点目标
        var target = TargetManager.FocusTarget;
        var node0  = addon->GetTextNodeById(10);
        if (node0 != null && target is ICharacter { ObjectKind: ObjectKind.Player } chara0)
            node0->SetText(ReplacePatterns(ModuleConfig.FocusTargetPattern, Payloads, chara0));
    }

    private static string ReplacePatterns(string input, IEnumerable<Payload> payloads, ICharacter chara)
    {
        foreach (var payload in payloads)
            input = input.Replace(payload.Placeholder, payload.ValueFunc(chara));

        return input;
    }

    protected override void Uninit()
    {
        DService.Instance().AddonLifecycle.UnregisterListener(UpdateTargetInfo);
        DService.Instance().AddonLifecycle.UnregisterListener(UpdateTargetInfoMainTarget);
        DService.Instance().AddonLifecycle.UnregisterListener(UpdateFocusTargetInfo);
    }

    private class Payload
    (
        string                   placeholder,
        string                   description,
        Func<ICharacter, string> valueFunc
    )
    {
        public string                   Placeholder { get; } = placeholder;
        public string                   Description { get; } = description;
        public Func<ICharacter, string> ValueFunc   { get; } = valueFunc;
    }

    private class Config : ModuleConfiguration
    {
        public string FocusTargetPattern   = "/Level/级 /Name/";
        public string TargetPattern        = "/Name/ [/Job/] «/FCTag/»";
        public string TargetsTargetPattern = "/Name/";
    }
}
