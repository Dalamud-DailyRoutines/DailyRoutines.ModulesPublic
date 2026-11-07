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
        if (ImGuiOm.ButtonIcon("设置", FontAwesomeIcon.Cog, Lang.Get("Settings")))
            ChatManager.Instance().SendMessage($"/pdr search {GetType().Name}");

        ImGui.Spacing();

        if (selected == null) return;

        var source = selected.MonstersFound.ToList();
        if (source.Count == 0) return;

        smallCardMobIndex = Math.Clamp(smallCardMobIndex, 0, source.Count - 1);

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

        ImGui.TableSetColumnIndex(1);
        DrawOverlayMonsterCard(selected, source[smallCardMobIndex], true);
        var cardHeight = ImGui.GetItemRectSize().Y;

        ImGui.TableSetColumnIndex(0);
        if (smallCardMobIndex > 0)
            DrawOverlayMonsterCard(selected, source[smallCardMobIndex - 1], false);
        else
            DrawOverlayRelayButton(RelayType.BeforeStart, cardHeight);

        ImGui.TableSetColumnIndex(2);
        if (smallCardMobIndex < source.Count - 1)
            DrawOverlayMonsterCard(selected, source[smallCardMobIndex + 1], true);
        else
            DrawOverlayRelayButton(RelayType.AfterEnd, cardHeight);
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
            relayTaskHelper.IsBusy          ||
            autoBroadcast.On                ||
            config.RelayCommands.Count == 0 ||
            string.IsNullOrWhiteSpace(pattern)
        );

        if (ImGuiOm.ButtonIconWithText(FontAwesomeIcon.Microphone, text))
            Relay(type);
    }
}
