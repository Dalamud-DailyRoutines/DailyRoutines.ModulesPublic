using System.Collections.Generic;
using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;

namespace DailyRoutines.ModulesPublic;

// TODO: 整合成一个忍术模块
public unsafe class AutoThrottleTenChiJin : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoThrottleTenChiJinTitle"),
        Description = GetLoc("AutoThrottleTenChiJinDescription"),
        Category    = ModuleCategories.Action,
    };
    
    private static readonly HashSet<uint> ShinobiActionsStart   = [2259, 2261, 2263];
    private static readonly HashSet<uint> ShinobiActionsProcess = [18805, 18806, 18807];
    private static readonly HashSet<uint> NinJiTsuActions       = [2265, 2266, 2267, 2268, 2269, 2270, 2271, 16491, 16492];
    
    private static readonly HashSet<uint> UsedShinobiActions = [];

    protected override void Init() => GamePacketManager.RegPreSendPacket(OnPreSendActionPacket);
    
    private static void OnPreSendActionPacket(ref bool isPrevented, int opcode, ref byte* packet, ref ushort priority)
    {
        if (opcode != GamePacketOpcodes.UseActionOpcode) return;

        if (LocalPlayerState.ClassJob != 30) return;
        
        var data = (UseActionPacket*)packet;
        if (ShinobiActionsStart.Contains(data->ActionID))
        {
            UsedShinobiActions.Clear();
            UsedShinobiActions.Add((data->ActionID % 2259 / 2) + 18805);
        }
        else if (ShinobiActionsProcess.Contains(data->ActionID))
        {
            if (!UsedShinobiActions.Add(data->ActionID)) 
                isPrevented = true;
        }
        else if (NinJiTsuActions.Contains(data->ActionID))
            UsedShinobiActions.Clear();
    }

    protected override void Uninit() => GamePacketManager.Unreg(OnPreSendActionPacket);
}
