using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using TinyPinyin;
using Control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;

namespace DailyRoutines.ModulesPublic;

public class OrnamentActionCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("OrnamentActionCommandTitle"),
        Description = Lang.Get("OrnamentActionCommandDescription", COMMAND),
        Category    = ModuleCategory.Assist
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    protected override void Init() =>
        CommandManager.Instance().AddSubCommand(COMMAND, new(OnCommand) { HelpMessage = Lang.Get("OrnamentActionCommand-CommandHelp") });
    
    protected override void Uninit() =>
        CommandManager.Instance().RemoveSubCommand(COMMAND);

    private static unsafe void OnCommand(string command, string args)
    {
        args = args.Trim().TrimQuotationMarks().Trim();
        if (string.IsNullOrEmpty(args)) return;

        var localPlayer = Control.GetLocalPlayer();
        if (localPlayer == null) return;

        var ornament = localPlayer->OrnamentData.OrnamentId;
        if (!LuminaGetter.TryGetRow(ornament, out Ornament ornamentRow)) return;

        var isInputActionID = uint.TryParse(args, out var inputActionID);
        
        foreach (var actionRef in ornamentRow.Action.Value.Actions)
        {
            if (actionRef.RowId == 0) continue;
            
            var action = actionRef.Value;

            if (isInputActionID && inputActionID == action.RowId)
            {
                UseActionManager.Instance().UseAction(ActionType.Action, action.RowId);
                break;
            }
            
            var actionName   = action.Name.ToString();
            var actionPinyin = PinyinHelper.GetPinyin(actionName, string.Empty);
            
            if (!actionName.Contains(args, StringComparison.OrdinalIgnoreCase) &&
                !actionPinyin.Contains(args, StringComparison.OrdinalIgnoreCase))
                continue;
            
            UseActionManager.Instance().UseAction(ActionType.Action, action.RowId);
            break;
        }
    }

    #region 常量

    private const string COMMAND = "oac";

    #endregion
}
