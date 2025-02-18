using System.Collections.Generic;
using System.Linq;
using DailyRoutines.Abstracts;
using DailyRoutines.Managers;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Lumina.Excel.Sheets;
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace DailyRoutines.ModulesPublic.Assist;

public class SastashaHelper : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("SastashaHelperTitle"),
        Description = GetLoc("SastashaHelperDescription"),
        Category    = ModuleCategories.Assist
    };

    // Book Data ID - Coral Data ID
    private static readonly Dictionary<uint, (uint CoralDataID, ushort UIColor, ObjectHighlightColor HighlightColor)> BookToCoral = new()
    {
        // 蓝珊瑚
        [2000212] = (2000213, 37, ObjectHighlightColor.Yellow),
        // 红珊瑚
        [2001548] = (2000214, 17, ObjectHighlightColor.Green),
        // 绿珊瑚
        [2001549] = (2000215, 45, ObjectHighlightColor.Red),
    };

    private static ulong CorrectCoralDataID;
    private static ObjectHighlightColor CorrectCoralHighlightColor;

    public override void Init()
    {
        TaskHelper ??= new() { TimeLimitMS = 30_000 };
        
        DService.ClientState.TerritoryChanged += OnZoneChanged;
        OnZoneChanged(DService.ClientState.TerritoryType);
    }

    private void OnZoneChanged(ushort zone)
    {
        TaskHelper?.Abort();
        FrameworkManager.Unregister(OnUpdate);

        CorrectCoralDataID = 0;
        CorrectCoralHighlightColor = ObjectHighlightColor.None;
        if (zone != 1036) return;
        
        TaskHelper.Enqueue(GetCorrectCoral);
        FrameworkManager.Register(false, OnUpdate);
    }

    private static unsafe void OnUpdate(IFramework _)
    {
        if (!Throttler.Throttle("SastashaHelper-OnUpdate", 2_000)) return;
        if (CorrectCoralDataID == 0 || CorrectCoralHighlightColor == ObjectHighlightColor.None) return;

        var coral = DService.ObjectTable.FirstOrDefault(
            x => x.ObjectKind == ObjectKind.EventObj && x.DataId == CorrectCoralDataID);
        if (coral == null) return;

        coral.ToStruct()->Highlight(coral.IsTargetable ? CorrectCoralHighlightColor : ObjectHighlightColor.None);
    }
    
    private static bool? GetCorrectCoral()
    {
        if (DService.ClientState.LocalPlayer is null || BetweenAreas || !IsScreenReady()) return false;
        
        var book = DService.ObjectTable
                           .FirstOrDefault(x => x.IsTargetable && x.ObjectKind == ObjectKind.EventObj && 
                                                BookToCoral.ContainsKey(x.DataId));
        if (book == null) return false;

        var info = BookToCoral[book.DataId];

        Chat(GetSLoc("SastashaHelper-Message",
                     new SeStringBuilder()
                         .AddUiForeground(LuminaCache.GetRow<EObjName>(info.CoralDataID)!.Value.Singular.ExtractText(),
                                          info.UIColor).Build()));
        
        CorrectCoralDataID         = info.CoralDataID;
        CorrectCoralHighlightColor = info.HighlightColor;
        return true;
    }

    public override void Uninit()
    {
        DService.ClientState.TerritoryChanged -= OnZoneChanged;
        OnZoneChanged(0);
        
        base.Uninit();
    }
}
