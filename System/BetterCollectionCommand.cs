using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public unsafe class BetterCollectionCommand : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("BetterCollectionCommandTitle"),
        Description = Lang.Get("BetterCollectionCommandDescription"),
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { AllDefaultEnabled = true };

    private string[] validCommands;
    
    protected override void Init()
    {
        var row = LuminaGetter.GetRowOrDefault<TextCommand>(24);
        validCommands =
        [
            .. new[]
            {
                row.Command.ToString(),
                row.ShortCommand.ToString(),
                row.Alias.ToString(),
                row.ShortAlias.ToString()
            }.Where(x => !string.IsNullOrEmpty(x))
        ];
        
        ChatManager.Instance().RegPreExecuteCommandInner(OnCommand);
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnCommand);

    private void OnCommand
    (
        ref bool             isPrevented,
        ref ReadOnlySeString message
    )
    {
        var splited = message.ToString().Split
        (
            ' ',
            2,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        
        if (splited.Length != 2) 
            return;

        var command = splited[0];
        var param   = splited[1];

        foreach (var validCommand in validCommands)
        {
            if (command != validCommand) 
                continue;

            foreach (var row in LuminaGetter.Get<McGuffin>())
            {
                if (row.UIData.RowId == 0)
                    continue;

                var mcGuffinName = row.UIData.Value.Name.ToString();
                if (string.IsNullOrEmpty(mcGuffinName))
                    continue;
                
                if (!mcGuffinName.Contains(param, StringComparison.OrdinalIgnoreCase))
                    continue;

                
                AgentMcGuffin.Instance()->OpenMcGuffin(row.RowId);
                isPrevented = true;
                return;
            }
        }
    }
}
