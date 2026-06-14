using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using OmenTools.Interop.Game.Models;

namespace DailyRoutines.ModulesPublic.Interface;

public unsafe class SimplifiedSystemMenu : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SimplifiedSystemMenuTitle"),
        Description = Lang.Get("SimplifiedSystemMenuDescription"),
        Category    = ModuleCategory.Interface
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private Hook<AgentHUD.Delegates.OpenSystemMenu> OpenSystemMenuHook = null!;

    private static readonly CompSig GenerateSystemMenuSig = new
        ("40 53 55 56 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 49 8B D9");
    private delegate byte                             GenerateSystemMenuDelegate(AgentHUD* agent, AtkValue* atkValueArgs, int valueCount, ulong eventKind);
    private          Hook<GenerateSystemMenuDelegate> GenerateSystemMenuHook;
    
    protected override void Init()
    {
        OpenSystemMenuHook = DService.Instance().Hook.HookFromMemberFunction
        (
            typeof(AgentHUD.MemberFunctionPointers),
            "OpenSystemMenu",
            (AgentHUD.Delegates.OpenSystemMenu)OpenSystemMenuDetour
        );
        OpenSystemMenuHook.Enable();

        GenerateSystemMenuHook = GenerateSystemMenuSig.GetHook<GenerateSystemMenuDelegate>(GenerateSystemMenuDetour);
        GenerateSystemMenuHook.Enable();
    }

    private byte GenerateSystemMenuDetour(AgentHUD* agent, AtkValue* atkValueArgs, int valueCount, ulong eventKind)
    {
        // 关闭菜单 (eventKind == 102)
        // 直接执行主命令 (eventKind 1..100, atkValueArgs 此时为 NULL, valueCount 为 0)
        if (eventKind == 102 || atkValueArgs == null)
            return GenerateSystemMenuHook.Original(agent, atkValueArgs, valueCount, eventKind);
        
        // 不是我们要处理的情况 (atkValueArgs[0].Int 需为 1)
        if (atkValueArgs[0].Int != 1)
            return GenerateSystemMenuHook.Original(agent, atkValueArgs, valueCount, eventKind);
        
        // 由 OpenSystemMenu 处理 (bit 256 置位时为 SystemMenu 路径)
        if ((atkValueArgs[4].Int & 256) != 0)
            return GenerateSystemMenuHook.Original(agent, atkValueArgs, valueCount, eventKind);

        List<int> commands = [];
        for (var i = START_INDEX; i < valueCount; i++)
        {
            var atkValue = atkValueArgs[i];
            if (atkValue.Type == AtkValueType.Undefined)
                break;
            
            if (BlockedCommandID.Contains((uint)atkValue.Int)) continue;
            commands.Add(atkValue.Int);
        }
        
        using var values = new RentedAtkValues(valueCount);
        for (var i = 0; i < START_INDEX; i++)
            values[i].Copy(&atkValueArgs[i]);

        for (var i = 0; i < commands.Count; i++)
            values[i + START_INDEX].SetInt(commands[i]);
        
        values[4].SetInt(commands.Count);
        return GenerateSystemMenuHook.Original(agent, values, valueCount, eventKind);
    }

    private void OpenSystemMenuDetour(AgentHUD* agent, AtkValue* atkValueArgs, uint menuSize)
    {
        if (menuSize > MAX_ENTRIES)
        {
            OpenSystemMenuHook.Original(agent, atkValueArgs, menuSize);
            return;
        }

        using var values = new RentedAtkValues(START_INDEX + (MAX_ENTRIES * 2));

        for (var i = 0; i < START_INDEX; i++)
            values[i].Copy(&atkValueArgs[i]);

        var newMenuSize = 0;

        for (var i = 0; i < menuSize; i++)
        {
            var sourceIndex = START_INDEX + i;
            if (BlockedCommandID.Contains((uint)atkValueArgs[sourceIndex].Int)) continue;

            var targetIndex = START_INDEX + newMenuSize;
            values[targetIndex].Copy(&atkValueArgs[sourceIndex]);
            values[targetIndex + MAX_ENTRIES].Copy(&atkValueArgs[sourceIndex + MAX_ENTRIES]);
            newMenuSize++;
        }

        values[3].SetInt(newMenuSize);
        OpenSystemMenuHook.Original(agent, values, (uint)newMenuSize);
    }

    private const int MAX_ENTRIES = 20;
    private const int START_INDEX = 5;

    private static readonly FrozenSet<uint> BlockedCommandID =
    [
        30, // 新手指南
        32, // 隐私政策
        37, // 许可证
        68, // 资讯中心
        90  // 官方网站
    ];
}
