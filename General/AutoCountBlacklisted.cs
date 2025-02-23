using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace DailyRoutines.Modules;

public unsafe class AutoCountBlacklisted : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("AutoCountBlacklistedTitle"),
        Description = GetLoc("AutoCountBlacklistedDescription"),
        Category    = ModuleCategories.General,
        Author      = ["ToxicStar"],
    };
    
    private static readonly CompSig InfoProxyBlackListUpdateSig = new("48 89 5C 24 ?? 4C 8B 91 ?? ?? ?? ?? 33 C0");
    private delegate void InfoProxyBlackListUpdateDelegate(InfoProxyBlacklist.BlockResult* outBlockResult, ulong accountId, ulong contentId);
    private static Hook<InfoProxyBlackListUpdateDelegate>? InfoProxyBlackListUpdateHook;

    private static Config         ModuleConfig = null!;
    private static IDtrBarEntry?  DtrEntry;
    private static int            LastCheckNum = 0;
    private static HashSet<ulong> BlacklistHashSet = [];

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        ResetBlackList();

        InfoProxyBlackListUpdateHook ??= InfoProxyBlackListUpdateSig.GetHook<InfoProxyBlackListUpdateDelegate>(InfoProxyBlackListUpdateDetour);
        InfoProxyBlackListUpdateHook.Enable();

        DtrEntry ??= DService.DtrBar.Get("DailyRoutines-AutoCountBlacklisted");
        if (DtrEntry != null) DtrEntry.Shown = true;

        FrameworkManager.Register(false, OnUpdate);
    }

    public override void ConfigUI()
    {
        ImGui.SetNextItemWidth(150f * GlobalFontScale);
        if (ImGui.InputInt(GetLoc("Radius"), ref ModuleConfig.CheckRange))
            ModuleConfig.CheckRange = Math.Max(1, ModuleConfig.CheckRange);

        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendChat"), ref ModuleConfig.SendChat))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendNotification"), ref ModuleConfig.SendNotification))
            SaveConfig(ModuleConfig);

        if (ImGui.Checkbox(GetLoc("SendTTS"), ref ModuleConfig.SendTTS))
            SaveConfig(ModuleConfig);
    }
    
    private void InfoProxyBlackListUpdateDetour(InfoProxyBlacklist.BlockResult* outBlockResult, ulong accountID, ulong contentID)
    {
        InfoProxyBlackListUpdateHook.Original(outBlockResult, accountID, contentID);

        // 触发了黑名单更新
        if (outBlockResult != null && outBlockResult->BlockedCharacterIndex != BlacklistHashSet.Count)
            ResetBlackList();
    }

    private static void ResetBlackList()
    {
        // 启动/更新时，统计一次
        var tempHashSet = new HashSet<ulong>();
        foreach (var blockCharacter in InfoProxyBlacklist.Instance()->BlockedCharacters)
        {
            if (blockCharacter.Id is not 0)
            {
                // blockCharacter.Id = accountId for new, contentId for old
                tempHashSet.Add(blockCharacter.Id);

                // BlockedCharacters 只增不减，必须使用 BlockedCharactersCount 处理变化后的数量
                if (tempHashSet.Count >= InfoProxyBlacklist.Instance()->BlockedCharactersCount)
                    break;
            }
        }
        BlacklistHashSet = tempHashSet;
    }

    private static void OnUpdate(IFramework _)
    {
        if (!Throttler.Throttle("AutoCountBlacks-OnUpdate")) return;
        
        if (DtrEntry is null) return;
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return;

        var tooltip = new StringBuilder();
        var blackNum = 0;
        var myPos = localPlayer.Position;
        var checkRange = ModuleConfig.CheckRange * ModuleConfig.CheckRange;
        foreach (var obj in DService.ObjectTable)
        {
            if (obj is not null && obj.ObjectKind is ObjectKind.Player)
            {
                var needCheckPos = obj.Position;
                if (Vector3.DistanceSquared(myPos, needCheckPos) <= checkRange)
                {
                    var chara = obj.ToBCStruct();
                    if (chara is null) continue;

                    if (!PresetData.Worlds.TryGetValue(chara->HomeWorld, out var world)) continue;

                    // Character.Id = accountId for new, contentId for old
                    if (BlacklistHashSet.Contains(chara->Character.ContentId) || BlacklistHashSet.Contains(chara->Character.AccountId))
                    {
                        tooltip.AppendLine($"{obj.Name}@{world.Name.ToString()}");
                        blackNum++;
                    }
                }
            }
        }

        if (LastCheckNum < blackNum)
        {
            var message = GetLoc("AutoCountBlacks-DtrEntry-Text", tooltip.ToString().Trim());
            if (ModuleConfig.SendChat) Chat(message);
            if (ModuleConfig.SendNotification) NotificationInfo(message);
            if (ModuleConfig.SendTTS) Speak(message);
        }
        LastCheckNum = blackNum;

        DtrEntry.Text = GetLoc("AutoCountBlacks-DtrEntry-Text", blackNum.ToString());
        DtrEntry.Tooltip = tooltip.ToString().Trim();
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);

        DtrEntry?.Remove();
        DtrEntry = null;

        base.Uninit();
    }

    public class Config : ModuleConfiguration
    {
        public int CheckRange = 2;
        public bool SendChat = true;
        public bool SendNotification = true;
        public bool SendTTS = true;
    }
}
