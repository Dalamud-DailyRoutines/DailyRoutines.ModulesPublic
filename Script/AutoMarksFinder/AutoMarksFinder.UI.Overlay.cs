using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    protected override void OverlayUI()
    {
        var selected =
            selectedTrainIndex == -1 ||
            selectedTrainIndex > config.Trains.Count - 1 ?
                null :
                config.Trains[selectedTrainIndex];
        
        if (DrawTrainsSelectCombo(300f * GlobalUIScale, ref selectedTrainIndex))
        {
            smallCardMobIndex = 0;
            StopAutoBroadcast();
            selected = SelectedTrain;
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        using (ImRaii.Disabled(selected == null))
        {
            ImGui.SameLine();

            if (ImGuiOm.ButtonIcon("导出", FontAwesomeIcon.FileExport, Lang.Get("Export")))
            {
                if (selected != null)
                    ExportToClipboard(selected);
            }
        }

        ImGui.SameLine();

        if (ImGuiOm.ButtonIcon("导入", FontAwesomeIcon.FileImport, Lang.Get("Import")))
        {
            var imported = ImportFromClipboard<NotoriousMonsterList>();

            if (imported != null)
            {
                foreach (var monster in imported.MonstersFound)
                    monster.SetState(true);

                config.Trains.Add(imported);

                aliveMonitor?.InitializeTracks(imported);

                config.Save(this);
            }
        }

        ImGui.SameLine();
        ImGui.TextDisabled("|");

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon("设置", FontAwesomeIcon.Cog, Lang.Get("Settings")))
            ChatManager.Instance().SendMessage($"/pdr search {GetType().Name}");

        ImGui.Spacing();

        if (selected == null) return;

        var source = selected.MonstersFound.ToList();
        if (source.Count == 0) return;

        using var table = ImRaii.Table("##SmallCardTable", 3, ImGuiTableFlags.Resizable);
        if (!table) return;

        ImGui.TableSetupColumn("Left",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mid",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        if (smallCardMobIndex > 0)
        {
            if (ImGuiOm.ButtonIcon("##FormerMob", FontAwesomeIcon.ArrowLeft))
                smallCardMobIndex--;
        }

        ImGui.TableNextColumn();
        ImGuiOm.Text($"{Lang.Get("Current")}");

        ImGui.TableNextColumn();

        if (smallCardMobIndex < source.Count - 1)
        {
            if (ImGuiOm.ButtonIcon("##NextMob", FontAwesomeIcon.ArrowRight))
                smallCardMobIndex++;
        }

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        if (smallCardMobIndex > 0)
        {
            var info = source[smallCardMobIndex - 1];

            using (FontManager.Instance().UIFont120.Push())
            {
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );
            }

            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
        }

        ImGui.TableNextColumn();
        {
            var info = source[smallCardMobIndex];

            using (FontManager.Instance().UIFont120.Push())
            {
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );
            }

            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
        }

        ImGui.TableNextColumn();

        if (smallCardMobIndex < source.Count - 1)
        {
            var info = source[smallCardMobIndex + 1];

            using (FontManager.Instance().UIFont120.Push())
            {
                ImGui.TextColored
                (
                    info.IsAlive ?
                        KnownColor.LightGreen.ToVector4() :
                        KnownColor.Red.ToVector4(),
                    $"{info.GetBNPCName()}"
                );
            }

            ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));
        }

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        {
            var info = source[smallCardMobIndex];
            DrawPositionColumn(info);

            ImGui.SameLine();
            DrawAetheryteColumn(info);

            DrawMonsterStatusToggle(selected, info);

            ImGui.SameLine();
            DrawTeleportToMonsterZone(info);
        }

        ImGui.TableNextColumn();

        if (smallCardMobIndex < source.Count - 1)
        {
            var info = source[smallCardMobIndex + 1];
            DrawPositionColumn(info);

            ImGui.SameLine();
            DrawAetheryteColumn(info);

            DrawMonsterStatusToggle(selected, info);

            ImGui.SameLine();
            DrawTeleportToMonsterZone(info);
        }
    }
}
