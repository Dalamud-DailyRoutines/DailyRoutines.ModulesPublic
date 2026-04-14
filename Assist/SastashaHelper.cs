using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models.Packets.Upstream;
using OmenTools.OmenService;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic;

public unsafe class SastashaHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SastashaHelperTitle"),
        Description = Lang.Get("SastashaHelperDescription"),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };
    
    private ulong                correctCoralDataID;
    private ObjectHighlightColor correctCoralHighlightColor;

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

        correctCoralDataID         = 0;
        correctCoralHighlightColor = ObjectHighlightColor.None;
    }

    private void OnZoneChanged(ushort zone)
    {
        TaskHelper?.Abort();
        FrameworkManager.Instance().Unreg(OnUpdate);
        GamePacketManager.Instance().Unreg(OnPostSendPackt);

        correctCoralDataID         = 0;
        correctCoralHighlightColor = ObjectHighlightColor.None;

        if (GameState.TerritoryType != 1036) return;

        TaskHelper.Enqueue(GetCorrectCoral);
        GamePacketManager.Instance().RegPostSendPacket(OnPostSendPackt);
        FrameworkManager.Instance().Reg(OnUpdate, 2_000);
    }

    private void OnPostSendPackt(int opcode, nint packet, bool isPrioritize)
    {
        if (opcode != UpstreamOpcode.EventStartOpcode) return;

        var packetData = (EventStartPackt*)packet;
        if (packetData->EventID == 983066)
            FrameworkManager.Instance().Unreg(OnUpdate);
    }

    private void OnUpdate(IFramework _)
    {
        if (correctCoralDataID == 0 || correctCoralHighlightColor == ObjectHighlightColor.None) return;

        if (DService.Instance().ObjectTable.SearchObject
                (x => x.ObjectKind == ObjectKind.EventObj && x.DataID == correctCoralDataID) is not { } coral)
            return;

        coral.ToStruct()->Highlight(coral.IsTargetable ? correctCoralHighlightColor : ObjectHighlightColor.None);
    }

    private bool GetCorrectCoral()
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

        NotifyHelper.Instance().Chat
        (
            Lang.GetSe
            (
                "SastashaHelper-Message",
                new SeStringBuilder()
                    .AddUiForeground(LuminaWrapper.GetEObjName(info.CoralDataID), info.UIColor)
                    .Build()
            )
        );

        correctCoralDataID         = info.CoralDataID;
        correctCoralHighlightColor = info.HighlightColor;
        return true;
    }

    #region 常量

    // Book Data ID - Coral Data ID
    private static readonly FrozenDictionary<uint, (uint CoralDataID, ushort UIColor, ObjectHighlightColor HighlightColor)> BookToCoral =
        new Dictionary<uint, (uint CoralDataID, ushort UIColor, ObjectHighlightColor HighlightColor)>
        {
            // 蓝珊瑚
            [2000212] = (2000213, 37, ObjectHighlightColor.Yellow),
            // 红珊瑚
            [2001548] = (2000214, 17, ObjectHighlightColor.Green),
            // 绿珊瑚
            [2001549] = (2000215, 45, ObjectHighlightColor.Red)
        }.ToFrozenDictionary();

    #endregion
}
