using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DailyRoutines.Abstracts;
using DailyRoutines.Helpers;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ImGuiNET;
using Lumina.Excel.GeneratedSheets;
using OmenTools;
using OmenTools.Helpers;
using OmenTools.Infos;

namespace DailyRoutines.Modules;

public unsafe class AutoCountBlacks : DailyModuleBase
{
    private static Config ModuleConfig = null!;
    private static IDtrBarEntry DtrEntry;
    private static HashSet<ulong> BlackHashSet = new();

    private static readonly CompSig InfoProxyBlackListUpdateSig = new("48 89 5C 24 ?? 4C 8B 91 ?? ?? ?? ?? 33 C0");
    private delegate void InfoProxyBlackListUpdateDelegate(InfoProxyBlacklist.BlockResult* outBlockResult, ulong accountId, ulong contentId);
    private static Hook<InfoProxyBlackListUpdateDelegate>? InfoProxyBlackListUpdateHook;

    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoCountBlacksTitle"),
        Description = GetLoc("AutoCountBlacksDescription"),
        Category = ModuleCategories.General,
        Author = ["ToxicStar"],
    };

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();

        ResetBlackList();

        InfoProxyBlackListUpdateHook ??= InfoProxyBlackListUpdateSig.GetHook<InfoProxyBlackListUpdateDelegate>(InfoProxyBlackListUpdateDetour);
        InfoProxyBlackListUpdateHook.Enable();

        DtrEntry ??= DService.DtrBar.Get("DailyRoutines-AutoCountBlacks");
        DtrEntry.Shown = true;

        FrameworkManager.Register(false, OnUpdate);
    }

    private void InfoProxyBlackListUpdateDetour(InfoProxyBlacklist.BlockResult* outBlockResult, ulong accountId, ulong contentId)
    {
        InfoProxyBlackListUpdateHook.Original(outBlockResult, accountId, contentId);

        //触发了黑名单更新
        if (outBlockResult->BlockedCharacterIndex != BlackHashSet.Count)
        {
            ResetBlackList();
        }
    }

    private static void ResetBlackList()
    {
        //启动/更新时，统计一次
        var tempHashSet = new HashSet<ulong>();
        foreach (var blockCharacter in InfoProxyBlacklist.Instance()->BlockedCharacters)
        {
            if (blockCharacter.Id is not 0)
            {
                //blockCharacter.Id = accountId for new, contentId for old
                tempHashSet.Add(blockCharacter.Id);

                //BlockedCharacters只增不减，必须使用BlockedCharactersCount处理变化后的数量
                if (tempHashSet.Count >= InfoProxyBlacklist.Instance()->BlockedCharactersCount)
                {
                    break;
                }
            }
        }
        BlackHashSet = tempHashSet;
    }

    private static void OnUpdate(IFramework _)
    {
        if (!Throttler.Throttle("AutoCountBlacks-OnUpdate")) return;
        if (DtrEntry is null) return;
        if (DService.ClientState.LocalPlayer is not { } localPlayer) return;

        var tooltip = new StringBuilder();
        int blackNum = 0;
        var myPos = localPlayer.Position;
        var checkRange = ModuleConfig.CheckRange * ModuleConfig.CheckRange;
        foreach (var obj in DService.ObjectTable)
        {
            if (obj is not null && obj.ObjectKind is ObjectKind.Player)
            {
                var needCheckPos = obj.Position;
                if (Vector3.DistanceSquared(myPos, needCheckPos) <= checkRange)
                {
                    var chara = (BattleChara*)obj.Address;
                    if (chara is null)
                    {
                        continue;
                    }

                    if (!PresetData.TryGetCNWorld(chara->HomeWorld, out var world))
                    {
                        continue;
                    }

                    //Character.Id = accountId for new, contentId for old
                    if (BlackHashSet.Contains(chara->Character.ContentId) || BlackHashSet.Contains(chara->Character.AccountId))
                    {
                        tooltip.AppendLine($"{obj.Name}@{world.Name.ToString()}");
                        blackNum++;
                    }
                }
            }
        }

        DtrEntry.Text = string.Format(GetLoc("AutoCountBlacks-DtrEntry-Text"), blackNum.ToString());
        DtrEntry.Tooltip = tooltip.ToString().Trim();
    }

    public override void ConfigUI()
    {
        if (ImGui.InputInt(GetLoc("AutoCountBlacks-Range"), ref ModuleConfig.CheckRange))
        {
            ModuleConfig.CheckRange = Math.Max(1, ModuleConfig.CheckRange);
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            SaveConfig(ModuleConfig);
        }
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
    }
}
