using System.Collections.Generic;
using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class SastashaHelper : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("SastashaHelperTitle"),
        Description = GetLoc("SastashaHelperDescription"),
        Category    = ModuleCategories.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    // Book Data ID - Coral Data ID
    private static readonly Dictionary<uint, (uint CoralDataID, ushort UIColor, ObjectHighlightColor HighlightColor)> BookToCoral = new()
    {
        // 蓝珊瑚
        [2000212] = (2000213, 37, ObjectHighlightColor.Yellow),
        // 红珊瑚
        [2001548] = (2000214, 17, ObjectHighlightColor.Green),
        // 绿珊瑚
        [2001549] = (2000215, 45, ObjectHighlightColor.Red)
    };

    private static ulong                CorrectCoralDataID;
    private static ObjectHighlightColor CorrectCoralHighlightColor;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }
    
    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        FrameworkManager.Instance().Unreg(OnUpdate);
        GamePacketManager.Instance().Unreg(OnPostSendPackt);

        CorrectCoralDataID         = 0;
        CorrectCoralHighlightColor = ObjectHighlightColor.None;
    }
    
    private void OnZoneChanged(ushort zone)
    {
        TaskHelper?.Abort();
        FrameworkManager.Instance().Unreg(OnUpdate);
        GamePacketManager.Instance().Unreg(OnPostSendPackt);

        CorrectCoralDataID         = 0;
        CorrectCoralHighlightColor = ObjectHighlightColor.None;
        
        if (GameState.TerritoryType != 1036) return;
        
        TaskHelper.Enqueue(GetCorrectCoral);
        GamePacketManager.Instance().RegPostSendPacket(OnPostSendPackt);
        FrameworkManager.Instance().Reg(OnUpdate, 2_000);
    }

    private static void OnPostSendPackt(int opcode, nint packet, bool isPrioritize)
    {
        if (opcode != UpstreamOpcode.EventStartOpcode) return;
        
        var packetData = (EventStartPackt*)packet;
        if (packetData->EventID == 983066)
            FrameworkManager.Instance().Unreg(OnUpdate);
    }
    
    private static void OnUpdate(IFramework _)
    {
        if (CorrectCoralDataID == 0 || CorrectCoralHighlightColor == ObjectHighlightColor.None) return;

        if (DService.Instance().ObjectTable.SearchObject
                (x => x.ObjectKind == ObjectKind.EventObj && x.DataID == CorrectCoralDataID) is not { } coral)
            return;

        coral.ToStruct()->Highlight(coral.IsTargetable ? CorrectCoralHighlightColor : ObjectHighlightColor.None);
    }

    private static bool GetCorrectCoral()
    {
        if (!UIModule.IsScreenReady()) return false;

        var book = DService.Instance().ObjectTable
                           .SearchObject
                           (
                               x => x is { IsTargetable: true, ObjectKind: ObjectKind.EventObj } && BookToCoral.ContainsKey(x.DataID),
                               IObjectTable.EventRange
                           );
        if (book == null) return false;

        var info = BookToCoral[book.DataID];

        Chat
        (
            GetSLoc
            (
                "SastashaHelper-Message",
                new SeStringBuilder()
                    .AddUiForeground(LuminaWrapper.GetEObjName(info.CoralDataID), info.UIColor)
                    .Build()
            )
        );

        CorrectCoralDataID         = info.CoralDataID;
        CorrectCoralHighlightColor = info.HighlightColor;
        return true;
    }
}
