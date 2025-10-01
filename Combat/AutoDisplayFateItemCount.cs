using System.Numerics;
using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using Lumina.Excel.Sheets;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDisplayFateItemCount : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoDisplayFateItemCountTitle"),
        Description = GetLoc("AutoDisplayFateItemCountDescription"),
        Category    = ModuleCategories.Combat
    };

    protected override void Init()
    {
        Overlay       ??= new(this);
        Overlay.Flags |=  ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoTitleBar;
        
        GameState.EnterFate += OnEnterFate;
        if (FateManager.Instance()->CurrentFate != null)
            OnEnterFate(0);
    }

    protected override void Uninit()
    {
        GameState.EnterFate -= OnEnterFate;
        
        base.Uninit();
    }

    private void OnEnterFate(uint _) => Overlay.IsOpen = true;

    protected override void OverlayUI()
    {
        var currentFate = FateManager.Instance()->CurrentFate;
        if (currentFate == null                                                  ||
            !LuminaGetter.TryGetRow<Fate>(currentFate->FateId, out var fateData) ||
            fateData.EventItem.RowId == 0)
        {
            Overlay.IsOpen = false;
            return;
        }

        if (!DService.Texture.TryGetFromGameIcon(new(fateData.EventItem.Value.Icon), out var texture) ||
            !IsAddonAndNodesReady(ToDoList)) 
            return;
        
        ImGui.SetWindowPos(new Vector2(ToDoList->X, ToDoList->Y));
        
        ImGui.Image(texture.GetWrapOrEmpty().Handle, new(ImGui.GetTextLineHeightWithSpacing()));
        
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGuiOm.TextOutlined(KnownColor.Orange.ToVector4(), $"{fateData.EventItem.Value.Singular}");

        ImGui.Spacing();
        
        using var table = ImRaii.Table("##Table", 2);
        if (!table) return;
        
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGuiOm.TextOutlined(KnownColor.White.ToVector4(), $"{GetLoc("AutoDisplayFateItemCount-HoldCount")}:");

        ImGui.TableNextColumn();
        ImGuiOm.TextOutlined(KnownColor.White.ToVector4(), $"{InventoryManager.Instance()->GetInventoryItemCount(fateData.EventItem.RowId)}");

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGuiOm.TextOutlined(KnownColor.White.ToVector4(), $"{GetLoc("AutoDisplayFateItemCount-HandInCount")}:");

        ImGui.TableNextColumn();
        ImGuiOm.TextOutlined(KnownColor.White.ToVector4(), $"{currentFate->HandInCount}");
    }
}
