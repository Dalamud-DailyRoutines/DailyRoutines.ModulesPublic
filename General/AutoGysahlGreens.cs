using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using OmenTools;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System.Linq;
using OmenTools.Helpers;
using ImGuiNET;
using System;
using static OmenTools.Helpers.HelpersOm;
using static DailyRoutines.Helpers.NotifyHelper;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace DailyRoutines.Modules;

public unsafe class AutoGysahlGreens : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        //Title = GetLoc("AutoGysahlGreensTitle"),
        //Description = GetLoc("AutoGysahlGreensDescription"),
        Title = "自动使用基萨尔野菜",
        Description = "在野外时，自动使用基萨尔野菜",
        Category = ModuleCategories.General,
        Author = ["Veever"]
    };

    private static Config ModuleConfig = null!;
    private static readonly HashSet<ushort> ValidTerritory;
    private static DateTime lastUpdateTime = DateTime.MinValue;
    public static float checkValue = 5f;

    static AutoGysahlGreens()
    {
        ValidTerritory = LuminaCache.Get<TerritoryType>()
                                    .Where(x => x.TerritoryIntendedUse.RowId == 1)
                                    .Select(x => (ushort)x.RowId)
                                    .ToHashSet();
    }

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);

        ImGui.SliderFloat("更改检测延迟/秒", ref checkValue, 0.0f, 60.0f, "%.2f");
        ImGui.Text($"更改检测延迟/秒: {checkValue}");

        //ImGui.SliderFloat(GetLoc("Changedelay"), ref checkValue, 0.0f, 60.0f, "%.2f");
        //ImGui.Text($"{GetLoc("ChangedelayText")}");
    }

    private static void OnZoneChanged(ushort zone)
    {
        FrameworkManager.Unregister(OnUpdate);

        if (ValidTerritory.Contains(zone))
            FrameworkManager.Register(false, OnUpdate);
    }

    private static void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if ((DateTime.Now - lastUpdateTime).TotalMilliseconds < (checkValue * 1000))
            return;

        lastUpdateTime = DateTime.Now;

        if (!ThrottlerHelper.Throttler.Throttle("AutoGysahlGreens-OnUpdateCheck", (int)(checkValue * 1000))) return;
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return;

        if (!HasGysahlGreens())
        {
            var notificationMessage = "未检测背包内有基萨尔野菜，请补充";
            //var notificationMessage = GetLoc("AutoGysahlGreens-Notification");
            if (ModuleConfig.SendChat) Chat(notificationMessage);
            if (ModuleConfig.SendNotification) NotificationInfo(notificationMessage);
            if (ModuleConfig.SendTTS) Speak(notificationMessage);
            return;
        }

        if (IsChocoboSummoned()) return;

        UseActionManager.UseActionLocation(ActionType.Item, 4868, 0xE0000000, default, 0xFFFF);
    }

    private static unsafe bool IsChocoboSummoned()
    {
        return UIState.Instance()->Buddy.CompanionInfo.TimeLeft > 0;
    }

    private static unsafe bool HasGysahlGreens()
    {
        return InventoryManager.Instance()->GetInventoryItemCount(4868) > 0;
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Unregister(OnUpdate);
    }

    private class Config : ModuleConfiguration
    {
        public bool SendChat;
        public bool SendNotification;
        public bool SendTTS;
    }
}
