// ReSharper disable RedundantUsingDirective.Global

#region OmenTools

global using OmenTools;
global using OmenTools.ImGuiOm;
global using OmenTools.Extensions;
global using IAetheryteList = OmenTools.Dalamud.Services.AetheryteList.Abstractions.IAetheryteList;
global using IAetheryteEntry = OmenTools.Dalamud.Services.AetheryteList.Abstractions.IAetheryteEntry;
global using IPlayerCharacter = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IPlayerCharacter;
global using ICharacter = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.ICharacter;
global using IGameObject = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IGameObject;
global using IObjectTable = OmenTools.Dalamud.Services.ObjectTable.Abstractions.IObjectTable;
global using IEventObj = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IEventObj;
global using INPC = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.INPC;
global using IBattleChara = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IBattleChara;
global using IBattleNPC = OmenTools.Dalamud.Services.ObjectTable.Abstractions.ObjectKinds.IBattleNPC;
global using StatusList = OmenTools.Dalamud.Services.StatusList.Implementations.StatusList;
global using static OmenTools.Global.Globals;
global using static OmenTools.Info.Game.Data.Addons;

#endregion

#region Dalamud

global using Dalamud.Bindings.ImGui;
global using Dalamud.Bindings.ImGuizmo;
global using Dalamud.Bindings.ImPlot;
global using Dalamud.Interface;
global using Dalamud.Interface.Utility.Raii;
global using Dalamud.Game;
global using Dalamud.Plugin.Services;

#endregion

#region C#

global using System.Drawing;

#endregion
