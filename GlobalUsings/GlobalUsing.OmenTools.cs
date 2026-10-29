// ReSharper disable RedundantUsingDirective.Global

#region OmenTools

global using OmenTools;
global using OmenTools.ImGuiOm;
global using OmenTools.Extensions;
global using OmenTools.Info.Game.Enums;
global using OmenTools.OmenService;
global using IAetheryteList = OmenTools.Dalamud.Services.Game.UI.Abstractions.IAetheryteList;
global using IAetheryteEntry = OmenTools.Dalamud.Services.Game.UI.Abstractions.IAetheryteEntry;
global using IPlayerCharacter = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IPlayerCharacter;
global using ICharacter = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.ICharacter;
global using IGameObject = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IGameObject;
global using IObjectTable = OmenTools.Dalamud.Services.Game.Object.Abstractions.IObjectTable;
global using IEventObj = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IEventObj;
global using INPC = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.INPC;
global using IBattleChara = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IBattleChara;
global using IBattleNPC = OmenTools.Dalamud.Services.Game.Object.Abstractions.ObjectKinds.IBattleNPC;
global using IDrawObject = OmenTools.Dalamud.Services.Graphics.Scene.Abstractions.IDrawObject;
global using IEventHandler = OmenTools.Dalamud.Services.Game.Event.Abstractions.IEventHandler;
global using ILuaActor = OmenTools.Dalamud.Services.Game.Event.Abstractions.ILuaActor;
global using ISceneObject = OmenTools.Dalamud.Services.Graphics.Scene.Abstractions.ISceneObject;
global using ISharedGroupLayoutInstance = OmenTools.Dalamud.Services.LayoutEngine.Group.Abstractions.ISharedGroupLayoutInstance;
global using StatusList = OmenTools.Dalamud.Services.Game.StatusList;
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
global using Dalamud.Plugin;
global using Dalamud.Plugin.Services;

#endregion

#region C#

global using System.Drawing;

#endregion
