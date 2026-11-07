using System.Globalization;
using DailyRoutines.Common.Extensions;
using FFXIVClientStructs.FFXIV.Client.System.Framework;

namespace DailyRoutines.ModulesPublic;

public partial class AutoMarksFinder
{
    protected override void OverlayUI()
    {
        DrawTrainsSelectCombo(300f * GlobalUIScale, selection);
        var selected = SelectedTrain;

        ImGui.SameLine();
        if (ImGuiOm.ButtonIcon("设置", FontAwesomeIcon.Cog, Lang.Get("Settings")))
            ChatManager.Instance().SendMessage($"/pdr search {GetType().Name}");

        if (movement.IsBusy &&
            scanner?.IsRunning != true)
        {
            ImGui.SameLine();
            if (ImGuiOm.ButtonIcon("###StopMovement", FontAwesomeIcon.Stop, Lang.Get("Stop")))
                movement.Stop();
        }

        ImGui.Spacing();

        DrawAutoBroadcastControls();

        ImGui.Separator();

        if (selected == null) return;

        var source = selected.MonstersFound;
        if (source.Count == 0) return;

        var monsterIndex = selection.MonsterIndex;

        using var table = ImRaii.Table("##SmallCardTable", 3, ImGuiTableFlags.Resizable);
        if (!table) return;

        ImGui.TableSetupColumn("Left",  ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Mid",   ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow();

        ImGui.TableNextColumn();

        if (monsterIndex > 0)
        {
            if (ImGuiOm.ButtonIcon("##FormerMob", FontAwesomeIcon.ArrowLeft))
                selection.MonsterIndex--;
        }

        ImGui.TableNextColumn();
        ImGuiOm.Text($"{Lang.Get("Current")}");

        ImGui.TableNextColumn();

        if (monsterIndex < source.Count - 1)
        {
            if (ImGuiOm.ButtonIcon("##NextMob", FontAwesomeIcon.ArrowRight))
                selection.MonsterIndex++;
        }

        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(1);
        monsterIndex = selection.MonsterIndex;
        DrawOverlayMonsterCard(selected, source[monsterIndex], true);
        var cardHeight = ImGui.GetItemRectSize().Y;

        ImGui.TableSetColumnIndex(0);
        if (monsterIndex > 0)
            DrawOverlayMonsterCard(selected, source[monsterIndex - 1], false);
        else
            DrawOverlayRelayButton(RelayType.BeforeStart, cardHeight);

        ImGui.TableSetColumnIndex(2);
        if (monsterIndex < source.Count - 1)
            DrawOverlayMonsterCard(selected, source[monsterIndex + 1], true);
        else
            DrawOverlayRelayButton(RelayType.AfterEnd, cardHeight);
    }

    private void DrawAutoBroadcastControls()
    {
        using var heading   = ImRaii.Heading1(Lang.Get("AutoMarksFinder-AutoBroadcast"));
        var       serverNow = Framework.GetServerTime();
        ImGui.TextUnformatted
        (
            Lang.Get
            (
                "AutoMarksFinder-AutoBroadcastServerTime",
                new Dictionary<string, object>
                {
                    ["timeText"] = DateTimeOffset.FromUnixTimeSeconds(serverNow).ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                }
            )
        );

        using (ImRaii.Disabled(autoBroadcast.IsRunning))
        {
            ImGui.SetNextItemWidth(120 * GlobalUIScale);
            if (ImGui.InputTextWithHint("###Departure", "HH:mm:ss", ref autoBroadcast.DepartTimeInput, 16))
                autoBroadcast.InvalidateDeparture();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("AutoMarksFinder-AutoBroadcastApply")))
                autoBroadcast.ApplyDeparture();

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("AutoMarksFinder-AutoBroadcastUseCurrentTime")))
                autoBroadcast.ApplyDeparture(true);
        }

        if (autoBroadcast.DepartAt is { } departure)
        {
            ImGui.TextUnformatted
            (
                Lang.Get
                (
                    "AutoMarksFinder-AutoBroadcastDepartTimeSet",
                    new Dictionary<string, object>
                    {
                        ["timeText"] = DateTimeOffset.FromUnixTimeSeconds(departure).ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    }
                )
            );
        }

        if (autoBroadcast.IsRunning)
        {
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Stop, Lang.Get("Stop")))
                autoBroadcast.Stop();

            ImGui.SameLine();
            ImGui.TextUnformatted(autoBroadcast.Status);
        }
        else
        {
            using var disabled = ImRaii.Disabled
            (
                autoBroadcast.DepartAt == null                         ||
                SelectedTrain is not { MonstersFound.Count: > 0 }      ||
                config.RelayCommands.Count == 0                        ||
                string.IsNullOrWhiteSpace(config.RelayPositionPattern) ||
                scanner?.IsRunning == true                             ||
                relay.IsBusy                                           ||
                !GameState.IsLoggedIn
            );
            if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Play, Lang.Get("Start")))
                autoBroadcast.Start();
        }
    }

    private void DrawOverlayMonsterCard(NotoriousMonsterList train, NotoriousMonsterFound info, bool showPosition)
    {
        using var group = ImRaii.Group();

        using (FontManager.Instance().UIFont120.Push())
        {
            ImGui.TextColored
            (
                info.IsAlive ?
                    KnownColor.LightGreen.ToVector4() :
                    KnownColor.Red.ToVector4(),
                info.GetBNPCName()
            );
        }

        ImGui.SameLine();
        DrawMonsterStatusToggle(train, info);

        ImGui.TextUnformatted(FormatZoneInfo(info.GetZoneName(), info.Instance));

        if (!showPosition)
            return;

        DrawPositionColumn(info);
        DrawAetheryteColumn(info);
    }

    private void DrawOverlayRelayButton(RelayType type, float cardHeight)
    {
        var (pattern, label) = type == RelayType.BeforeStart ?
                                   (config.AutoRelayBeforeStartPattern, "AutoMarksFinder-RelayTrainStart") :
                                   (config.AutoRelayAfterEndPattern, "AutoMarksFinder-RelayTrainEnd");

        var text  = Lang.Get(label);
        var style = ImGui.GetStyle();
        var buttonWidth = ImGui.CalcTextSize(FontAwesomeIcon.Microphone.ToIconString()).X +
                          ImGui.CalcTextSize(text).X                                      +
                          (style.FramePadding.X * 2)                                      +
                          style.ItemSpacing.X;
        ImGuiOm.CenterAlignFor(buttonWidth);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0, (cardHeight - ImGui.GetFrameHeight()) / 2));

        using var disabled = ImRaii.Disabled
        (
            relay.IsBusy                    ||
            autoBroadcast.IsRunning         ||
            config.RelayCommands.Count == 0 ||
            string.IsNullOrWhiteSpace(pattern)
        );

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Microphone, text))
            relay.Send(type, SelectedTrain);
    }
}
