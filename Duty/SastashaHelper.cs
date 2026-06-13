using System.Collections.Frozen;
using System.Numerics;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using KamiToolKit.Classes;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using OmenTools.OmenService.ImGuiZoneObject;
using ModuleBase = DailyRoutines.Common.Module.Abstractions.ModuleBase;
using ObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;
using DObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic.Duty;

public unsafe class SastashaHelper : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("SastashaHelperTitle"),
        Description = Lang.Get("SastashaHelperDescription", LuminaWrapper.GetContentName(4)),
        Category    = ModuleCategory.Duty
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private uint correctBookID;
    
    private ZoneIndicatorHandle? handle;

    protected override void Init()
    {
        TaskHelper ??= new() { TimeoutMS = 30_000 };

        DService.Instance().ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);

        handle = ImGuiZoneObjectIndicator.Instance().RegisterPermanent
        (
            1036,
            () =>
            {
                if (correctBookID == 0 || !BookToCoral.TryGetValue(correctBookID, out var data)) return [];

                var director = EventFramework.Instance()->GetContentDirector();
                if (director == null) return [];

                // 已经完成了
                var todos = director->DirectorTodos;
                if (todos[0].CurrentCount != 0)
                    return [];

                if (LocalPlayerState.DistanceTo2DSquared(FirstBossCenter) > 625)
                    return [];

                var gameObject = EventObjectManager.Instance()->FindFirst
                (ptr =>
                    {
                        var gameObject = (GameObject*)ptr;
                        return gameObject             != null                &&
                               gameObject->ObjectKind == ObjectKind.EventObj &&
                               gameObject->BaseId     == data.CoralDataID;
                    }
                );
                if (gameObject == null)
                    return [];

                return [IGameObject.Create((nint)gameObject)];
            },
            gameObject => new()
            {
                Text      = $"→ {gameObject.Name} ←",
                TextScale = 1.6f,
                TextColor = ColorHelper.GetColor(BookToCoral[correctBookID].UIColor)
            }
        );
    }

    protected override void Uninit()
    {
        DService.Instance().ClientState.TerritoryChanged -= OnZoneChanged;
        
        handle?.Unregister();
        handle = null;

        correctBookID = 0;
    }

    private void OnZoneChanged(uint u)
    {
        TaskHelper?.Abort();
        correctBookID = 0;

        if (GameState.TerritoryType != DUTY_ZONE_ID) return;

        TaskHelper.Enqueue(GetCorrectCoral);
    }

    private bool GetCorrectCoral()
    {
        if (!UIModule.IsScreenReady()) return false;
        
        var director = EventFramework.Instance()->GetContentDirector();
        if (director == null) return false;

        // 已经完成了
        var todos = director->DirectorTodos;
        if (todos[0].CurrentCount != 0)
            return true;

        var book = DService.Instance().ObjectTable
                           .SearchObject
                           (
                               x => x is { IsTargetable: true, ObjectKind: DObjectKind.EventObj } && 
                                    BookToCoral.ContainsKey(x.DataID),
                               IObjectTable.EventRange
                           );
        if (book == null) return false;

        var info = BookToCoral[book.DataID];

        using var rented = new RentedSeStringBuilder();
        NotifyHelper.Instance().Chat
        (
            Lang.GetSe
            (
                "SastashaHelper-Message",
                rented.Builder
                      .PushColorType(info.UIColor)
                      .Append(LuminaWrapper.GetEObjName(info.CoralDataID))
                      .PopColorType()
                      .ToReadOnlySeString()
            )
        );

        correctBookID = book.DataID;
        return true;
    }

    #region 常量

    // Book Data ID - Data
    private static readonly FrozenDictionary<uint, (uint CoralDataID, ushort UIColor)> BookToCoral =
        new Dictionary<uint, (uint CoralDataID, ushort UIColor)>
        {
            // 蓝珊瑚
            [2000212] = (2000213, 37),
            // 红珊瑚
            [2001548] = (2000214, 17),
            // 绿珊瑚
            [2001549] = (2000215, 45)
        }.ToFrozenDictionary();

    private static readonly Vector2 FirstBossCenter = new(75.0f, -45.0f);

    private const uint DUTY_ZONE_ID = 1036;

    #endregion
}
