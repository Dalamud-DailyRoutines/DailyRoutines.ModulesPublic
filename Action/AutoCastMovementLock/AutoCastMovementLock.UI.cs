using DailyRoutines.Common.Extensions;
using DailyRoutines.Extensions;

namespace DailyRoutines.ModulesPublic;

public unsafe partial class AutoCastMovementLock
{
    protected override void ConfigUI()
    {
        var changed = false;

        using var tabBar = ImRaii.TabBar("AutoCastMovementLock");
        if (!tabBar) return;

        using (var item = ImRaii.TabItem(Lang.Get("General")))
        {
            if (item)
            {
                using (ImRaii.Heading1(Lang.Get("AutoCastMovementLock-EnabledJobs")))
                    changed |= jobSelectCombo.DrawCheckbox();

                ImGui.NewLine();

                changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-InCombatOnlyMoveWhenSlideCast"), ref config.InCombatOnlyMoveWhenSlideCast);
                changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-EnablePVP"),                     ref config.IsEnabledPVP);

                ImGui.NewLine();

                changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-LockTeleportCast"), ref config.LockTeleportCast);
                changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-LockReturnCast"),   ref config.LockReturnCast);
            }
        }

        using (ImRaii.ItemWidth(200f * GlobalUIScale))
        using (var item = ImRaii.TabItem(Lang.Get("AutoCastMovementLock-KeyButtonSettings")))
        {
            if (item)
            {
                using (ImRaii.PushId("Keyboard"))
                using (ImRaii.Heading1(Lang.Get("Keyboard")))
                {
                    changed |= DrawBindingCombo(Lang.Get("AutoCastMovementLock-KeyButtonToTempUnlock"), ref config.KeyToTempUnlock);
                    changed |= DrawBindingCombo(Lang.Get("AutoCastMovementLock-KeyButtonToTempLock"),   ref config.KeyToTempLock);

                    changed |= ImGui.Checkbox(Lang.Get("AutoCastMovementLock-MouseButtonsToTempUnlock"), ref config.MouseButtonsToTempUnlock);
                }

                ImGui.NewLine();

                using (ImRaii.PushId("Gamepad"))
                using (ImRaii.Heading1(Lang.Get("Gamepad")))
                {
                    changed |= DrawBindingCombo(Lang.Get("AutoCastMovementLock-KeyButtonToTempUnlock"), ref config.ButtonToTempUnlock);
                    changed |= DrawBindingCombo(Lang.Get("AutoCastMovementLock-KeyButtonToTempLock"),   ref config.ButtonToTempLock);
                }
            }
        }

        if (changed)
        {
            config.Save(this);
            inputInterceptor.Reset();
        }
    }

    private static bool DrawBindingCombo<T>
    (
        string label,
        ref T  value
    ) where T : struct, Enum
    {
        using var combo = ImRaii.Combo(label, value.ToString());
        if (!combo) return false;

        foreach (var key in Enum.GetValues<T>().Distinct())
        {
            if (!ImGui.Selectable(key.ToString(), key.Equals(value))) continue;

            value = key;
            return true;
        }

        return false;
    }
}
