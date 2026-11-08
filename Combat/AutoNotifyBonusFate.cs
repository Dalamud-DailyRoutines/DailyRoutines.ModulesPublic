using System.Collections.Frozen;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Fates;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Info.Game.Enums;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

internal class AutoNotifyBonusFate : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoNotifyBonusFateTitle"),
        Description = Lang.Get("AutoNotifyBonusFateDescription"),
        Category    = ModuleCategory.Combat,
        Author      = ["Due"]
    };

    public override string ModuleName => nameof(AutoNotifyBonusFate);

    private Config config = null!;

    private readonly HashSet<ushort> notifiedFates = [];

    protected override void Init()
    {
        config     =   Config.Load(this) ?? new();
        TaskHelper ??= new();

        IClientState.Instance().TerritoryChanged += OnZoneChanged;
        OnZoneChanged(0);
    }

    protected override void Uninit()
    {
        IClientState.Instance().TerritoryChanged -= OnZoneChanged;
        ExecuteCommandManager.Instance().Unreg(OnPostExecuteCommand);

        notifiedFates.Clear();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Checkbox(Lang.Get("OpenMap"), ref config.AutoOpenMap))
            config.Save(this);
    }

    private void OnZoneChanged
    (
        uint u
    )
    {
        ExecuteCommandManager.Instance().Unreg(OnPostExecuteCommand);
        TaskHelper.Abort();
        notifiedFates.Clear();

        if (!ValidTerritories.Contains(GameState.TerritoryType)) return;

        ExecuteCommandManager.Instance().RegPost(OnPostExecuteCommand);
    }

    private void OnPostExecuteCommand
    (
        ExecuteCommandFlag command,
        uint               param1,
        uint               param2,
        uint               param3,
        uint               param4
    )
    {
        if (command != ExecuteCommandFlag.LoadFate) return;

        TaskHelper.Abort();
        TaskHelper.DelayNext(200);
        TaskHelper.Enqueue(UpdateAndNotify);
    }

    private void UpdateAndNotify()
    {
        if (!ValidTerritories.Contains(GameState.TerritoryType) ||
            GameState.Map == 0)
            return;

        var fateTable = IFateTable.Instance();

        if (fateTable.Length == 0)
        {
            if (notifiedFates.Count > 0)
                notifiedFates.Clear();

            return;
        }

        var currentBonusFates = new HashSet<ushort>();

        foreach (var fate in fateTable)
        {
            if (!fate.HasBonus) continue;

            currentBonusFates.Add(fate.FateId);

            if (notifiedFates.Add(fate.FateId))
                NotifyFate(fate);
        }

        if (notifiedFates.Count > 0)
            notifiedFates.IntersectWith(currentBonusFates);
    }

    private unsafe void NotifyFate
    (
        IFate fate
    )
    {
        var message = Lang.GetSe
        (
            "AutoNotifyBonusFate-Chat",
            new Dictionary<string, object>
            {
                ["fate"] = ReadOnlySeString.CreateMapLink
                (
                    fate.Position,
                    displayNameOverride: $"{fate.Name}（{fate.Progress}%）"
                ),
            }
        );

        NotifyHelper.Instance().Chat(message, false);
        NotifyHelper.Toast(message);

        NotifyHelper.Instance().TrayInfo
        (
            $"{fate.Name}（{fate.Progress}%）",
            Lang.Get("AutoNotifyBonusFate-Notification")
        );

        if (config.AutoOpenMap)
            AgentMap.Instance()->SetMapFlagAndOpen(GameState.Map, fate.Position, fate.Name.ToString());
    }

    private class Config : ModuleConfig
    {
        public bool AutoOpenMap = true;
    }

    #region 常量

    private static readonly FrozenSet<uint> ValidTerritories =
        LuminaGetter.Get<TerritoryType>()
                    .Where(x => x.TerritoryIntendedUse.RowId == 1)
                    .Where(x => x.ExVersion.Value.RowId      >= 2)
                    .Select(x => x.RowId)
                    .ToFrozenSet();

    #endregion
}
