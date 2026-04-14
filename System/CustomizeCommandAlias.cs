using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Lumina.Text.ReadOnly;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic;

public class CustomizeCommandAlias : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("CustomizeCommandAliasTitle"),
        Description = Lang.Get("CustomizeCommandAliasDescription"),
        Category    = ModuleCategory.System
    };

    private Config config = null!;

    private CompiledAliasEntry[] activeAliasEntries = [];

    private string sourceCommandInput = string.Empty;
    private string targetCommandInput = string.Empty;

    protected override void Init()
    {
        config = Config.Load(this) ?? new();
        RefreshActiveAliasEntries();

        ChatManager.Instance().RegPreExecuteCommandInner(OnPreExecuteCommandInner);
    }

    protected override void Uninit() =>
        ChatManager.Instance().Unreg(OnPreExecuteCommandInner);

    protected override void ConfigUI()
    {
        using (ImRaii.Disabled
               (
                   string.IsNullOrEmpty(sourceCommandInput) ||
                   string.IsNullOrEmpty(targetCommandInput) ||
                   !sourceCommandInput.StartsWith('/')      ||
                   !targetCommandInput.StartsWith('/')
               ))
        {
            if (ImGuiOm.ButtonIconWithTextVertical(FontAwesomeIcon.Plus, Lang.Get("Add")))
            {
                if (!HasDuplicateAlias(sourceCommandInput))
                {
                    var entry = new AliasEntry
                    {
                        Enabled       = true,
                        Alias         = sourceCommandInput,
                        TargetCommand = targetCommandInput
                    };

                    config.AliasEntries.Add(entry);
                    SaveAndRefresh(this);

                    sourceCommandInput = string.Empty;
                    targetCommandInput = string.Empty;
                }
            }
        }


        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.InputText($"{Lang.Get("Alias")}##AliasInput", ref sourceCommandInput, 128);
        
            ImGui.InputText($"{Lang.Get("Target")}##TargetCommandInput", ref targetCommandInput, 128);
        }
        
        if (config.AliasEntries.Count == 0)
            return;
        
        ImGui.NewLine();

        using var aliasTable = ImRaii.Table("##CustomizeCommandAliasTable", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!aliasTable) return;

        ImGui.TableSetupColumn($"##Enabled",          ImGuiTableColumnFlags.WidthFixed,   ImGui.GetTextLineHeightWithSpacing());
        ImGui.TableSetupColumn(Lang.Get("Alias"),     ImGuiTableColumnFlags.WidthStretch, 0.28f);
        ImGui.TableSetupColumn(Lang.Get("Target"),    ImGuiTableColumnFlags.WidthStretch, 0.42f);
        ImGui.TableSetupColumn(Lang.Get("Operation"), ImGuiTableColumnFlags.WidthStretch, 0.18f);
        ImGui.TableHeadersRow();

        for (var i = 0; i < config.AliasEntries.Count; i++)
        {
            var entry = config.AliasEntries[i];

            using var id = ImRaii.PushId($"CustomizeCommandAlias_{i}");

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var isEnabled = entry.Enabled;

            if (ImGui.Checkbox("##Enabled", ref isEnabled))
            {
                entry.Enabled = isEnabled;
                SaveAndRefresh(this);
            }

            ImGui.TableNextColumn();
            var aliasInput = entry.Alias;
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("##Alias", ref aliasInput, 128);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (!string.IsNullOrEmpty(aliasInput))
                {
                    if (!HasDuplicateAlias(aliasInput, i))
                    {
                        entry.Alias = aliasInput;
                        SaveAndRefresh(this);
                    }
                }
            }

            ImGui.TableNextColumn();
            var targetInput = entry.TargetCommand;
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputText("##Target", ref targetInput, 128);

            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (!string.IsNullOrEmpty(targetInput))
                {
                    entry.TargetCommand = targetInput;
                    SaveAndRefresh(this);
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(targetInput);

            ImGui.TableNextColumn();

            if (ImGuiOm.ButtonIcon("Delete", FontAwesomeIcon.TrashAlt, Lang.Get("Delete")))
            {
                config.AliasEntries.RemoveAt(i);
                SaveAndRefresh(this);

                i--;
            }
        }
    }

    private void OnPreExecuteCommandInner(ref bool isPrevented, ref ReadOnlySeString message)
    {
        var span = this.activeAliasEntries.AsSpan();
        if (span.IsEmpty)
            return;

        var messageText = message.ToString();
        if (string.IsNullOrEmpty(messageText))
            return;

        var messageSpan = messageText.AsSpan();
        var firstChar   = messageSpan[0];
        if (!firstChar.Equals('/')) return;

        foreach (ref readonly var entry in span)
        {
            if (firstChar          != entry.FirstChar  ||
                messageSpan.Length < entry.AliasLength ||
                !messageSpan.StartsWith(entry.Alias, StringComparison.Ordinal))
                continue;

            message = messageSpan.Length == entry.AliasLength
                          ? new(entry.TargetCommand)
                          : new(string.Concat(entry.TargetCommand, messageSpan[entry.AliasLength..]));
            return;
        }
    }

    private void SaveAndRefresh(ModuleBase module)
    {
        config.Save(module);
        RefreshActiveAliasEntries();
    }

    private void RefreshActiveAliasEntries()
    {
        var aliasEntries = CollectionsMarshal.AsSpan(config.AliasEntries);
        var activeCount  = 0;

        foreach (ref readonly var entry in aliasEntries)
        {
            if (entry.Enabled && !string.IsNullOrEmpty(entry.Alias) && !string.IsNullOrEmpty(entry.TargetCommand))
                activeCount++;
        }

        if (activeCount == 0)
        {
            activeAliasEntries = [];
            return;
        }

        var compiledEntries = GC.AllocateUninitializedArray<CompiledAliasEntry>(activeCount);
        var index           = 0;

        foreach (ref readonly var entry in aliasEntries)
        {
            if (!entry.Enabled || string.IsNullOrEmpty(entry.Alias) || string.IsNullOrEmpty(entry.TargetCommand))
                continue;

            compiledEntries[index++] = new(entry.Alias, entry.TargetCommand);
        }

        activeAliasEntries = compiledEntries;
    }

    private bool HasDuplicateAlias(ReadOnlySpan<char> alias, int excludedIndex = -1)
    {
        var aliasEntries = CollectionsMarshal.AsSpan(config.AliasEntries);

        for (var i = 0; i < aliasEntries.Length; i++)
        {
            if (i == excludedIndex || !alias.SequenceEqual(aliasEntries[i].Alias))
                continue;

            return true;
        }

        return false;
    }
    
    private class Config : ModuleConfig
    {
        public List<AliasEntry> AliasEntries = [];
    }

    private class AliasEntry
    {
        public bool   Enabled       { get; set; } = true;
        public string Alias         { get; set; } = string.Empty;
        public string TargetCommand { get; set; } = string.Empty;
    }

    private readonly record struct CompiledAliasEntry
    (
        string Alias,
        string TargetCommand
    )
    {
        public readonly string Alias         = Alias;
        public readonly string TargetCommand = TargetCommand;
        public readonly int    AliasLength   = Alias.Length;
        public readonly char   FirstChar     = Alias[0];
    }
}
