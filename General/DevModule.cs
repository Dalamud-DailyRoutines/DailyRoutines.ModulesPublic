using DailyRoutines.Abstracts;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using KamiToolKit.Nodes;

public unsafe class DevModule : DailyModuleBase
{
    private delegate void LeaveNoviceNetworkDelegate(InfoProxyDetail* detail);
    private static readonly LeaveNoviceNetworkDelegate LeaveNoviceNetwork =
        new CompSig("48 89 5C 24 ?? 57 48 83 EC ?? 4C 8B 49 ?? 48 8B D9 49 0F BA E1").GetDelegate<LeaveNoviceNetworkDelegate>();

    protected override void ConfigUI()
    {
        var agentPtr = (nint)AgentDetail.Instance();
        ImGui.Text($"{*(byte*)(agentPtr + 64)}");

        if (ImGui.Button("测试离开"))
        {
            LeaveNoviceNetwork(InfoProxyDetail.Instance());
        }
    }
}
