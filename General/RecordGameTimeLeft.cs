using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Linq;
using System.Runtime.InteropServices;
using System;
using Dalamud.Plugin.Services;
using DailyRoutines.Infos;
using OmenTools;
using DailyRoutines.Managers;
using Dalamud.Game.Gui.Dtr;
using ImGuiNET;
using DailyRoutines.Helpers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace DailyRoutines.Modules;

public class RecordGameTimeLeft : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("RecordGameTimeLeft"),
        Description = GetLoc("RecordGameTimeLeftDesc"),
        Category = ModuleCategories.General,
        Author = ["Due"]
    };

    private const string Command = "/timeleft";
    private static Config ModuleConfig = null!;
    private static IDtrBarEntry? Entry = null;

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        Entry = DService.DtrBar.Get("DailyRoutines-GameTimeLeft");
        Entry.Shown = false;

        DService.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_CharaSelectRemain", OnLobby);

        CommandManager.AddCommand(Command, new(OnCommand)
        {
            HelpMessage = GetLoc("RecordGameTimeLeft-CommandHelp"),
        });

        FrameworkManager.Register(false, OnUpdate);
    }

    public override ModulePermission Permission => new() { CNOnly = true };

    public override void ConfigUI()
    {

        ImGui.Text(GetLoc("RecordGameTimeLeft-ReferOnly"));

        if (ModuleConfig.hasMonthly)
        {
            ImGui.Text(GetLoc("RecordGameTimeLeft-MonthSubscribe"));
        }
        else
        {
            if (ModuleConfig.timeTill != null)
            {
                ImGui.Text($"{GetLoc("RecordGameTimeLeft-LastRecordTime")}{ModuleConfig.lastSuccessRecord}。 {GetLoc("RecordGameTimeLeft-TimeTill")} {ModuleConfig.timeTill}");
            }
            else
            {
                ImGui.Text(GetLoc("RecordGameTimeLeft-NoData"));
            }
        }

        ImGui.Spacing();

        var showOnDTR = ModuleConfig.showOnDTR;
        if (ImGui.Checkbox(GetLoc("RecordGameTimeLeft-DisplayOnBar"), ref showOnDTR))
        {
            ModuleConfig.showOnDTR = showOnDTR;
            ModuleConfig.Save(this);
        }

        ImGui.Spacing();

        if (ImGui.Button($"{GetLoc("RecordGameTimeLeft-Reset")}##TL_Reset"))
        {
            ResetConfig();
        }
    }

    public override void Uninit()
    {
        Entry?.Remove();

        DService.AddonLifecycle.UnregisterListener(OnLobby);
        FrameworkManager.Unregister(OnUpdate);

        base.Uninit();
    }

    private void OnCommand(string command, string args)
    {
        if (command is Command)
        {
            ModuleConfig.showOnDTR = !ModuleConfig.showOnDTR;
            ModuleConfig.Save(this);
            if (ModuleConfig.showOnDTR)
            {
                NotifyHelper.Chat(GetLoc("RecordGameTimeLeft-DisplayOn"));
            }
            else
            {
                NotifyHelper.Chat(GetLoc("RecordGameTimeLeft-DisplayOff"));
            }
        }
    }

    private unsafe void OnLobby(AddonEvent eventType, AddonArgs? args)
    {
        if (DService.ClientState.IsLoggedIn == false)
        {
            if (AgentLobby.Instance() != null)
            {
                if (ModuleConfig.lastSuccessRecord != null)
                {
                    if (DateTime.Now.Subtract(DateTime.Parse(ModuleConfig.lastSuccessRecord)).TotalMinutes < 1)
                    {
                        return;
                    }
                }
                try
                {
                    var info = AgentLobby.Instance()->LobbyData.LobbyUIClient.SubscriptionInfo;
                    if (info->DaysRemaining != 0)
                    {
                        ModuleConfig.hasMonthly = true;
                        ModuleConfig.Save(this);
                        return;
                    }
                    ModuleConfig.timeLeft = getTime(*(info));
                    ModuleConfig.timeTill = DateTime.Now.AddSeconds(int.Parse(ModuleConfig.timeLeft)).ToString();
                    ModuleConfig.lastSuccessRecord = DateTime.Now.ToString();
                    ModuleConfig.Save(this);
                    return;
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    private unsafe void OnUpdate(IFramework framework)
    {
        if (ModuleConfig.showOnDTR)
        {
            if (ModuleConfig.timeTill != null)
            {
                Entry.Text = $"{GetLoc("RecordGameTimeLeft-TimeTill")} {DateTime.Parse(ModuleConfig.timeTill):MM-dd HH:mm}";
                Entry.Shown = true;
            }
            else
            {
                Entry.Shown = false;
            }
        }
        else
        {
            Entry.Shown = false;
        }
    }

    private static string getTime(LobbySubscriptionInfo str)
    {
        var size = Marshal.SizeOf(str);
        var arr = new byte[size];
        var ptr = IntPtr.Zero;

        try
        {
            ptr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(str, ptr, true);
            Marshal.Copy(ptr, arr, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        var ret = "";
        // 理论最多到 FFFFFF, 没有人冲到这个数吧
        // 什么神奇程序员反过来存的
        ret = string.Join("", arr.Skip(24).Take(3).Reverse().Select(x => x.ToString("X2")));
        ret = Convert.ToInt32(ret, 16).ToString();
        return ret;
    }

    private class Config : ModuleConfiguration
    {
        public string? lastSuccessRecord { get; set; } = null;
        public bool showOnDTR { get; set; } = false;
        public string? timeLeft { get; set; } = null;
        public string? timeTill { get; set; } = null;
        public bool hasMonthly { get; set; } = false;
    }

    private void ResetConfig()
    {
        ModuleConfig.lastSuccessRecord = null;
        ModuleConfig.showOnDTR = false;
        ModuleConfig.timeLeft = null;
        ModuleConfig.timeTill = null;
        ModuleConfig.hasMonthly = false;
        ModuleConfig.Save(this);
    }
}
