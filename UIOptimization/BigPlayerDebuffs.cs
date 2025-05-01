using System;
using DailyRoutines.Abstracts;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;


namespace DailyRoutines.ModulesPublic
{
    public unsafe class BigPlayerDebuffsModule : DailyModuleBase
    {
        public override ModuleInfo Info { get; } = new()
        {
            Title = GetLoc("BigPlayerDebuffsTitle"),
            Description = GetLoc("BigPlayerDebuffsDescription"),
            Category = ModuleCategories.UIOptimization,
        };

        private static Config ModuleConfig = null!;
        
        private int _currentPlayerDebuffs = -1;
        private int _currentSecondRowOffset = 41;

        public override void Init()
        {
            ModuleConfig = LoadConfig<Config>() ?? new();
            
            DService.Framework.Update += OnFrameworkUpdate;
        }

        public override void Uninit()
        {
            ResetTargetStatus();
            DService.Framework.Update -= OnFrameworkUpdate;
        }

        public override void ConfigUI()
        {
            if (ImGui.SliderFloat(GetLoc("BigPlayerDebuffs-ScaleRatio"), ref ModuleConfig.BuffScale, 1.0f, 4.0f))
            {
                _currentPlayerDebuffs = -1;
                
                SaveConfig(ModuleConfig);
            }
            
            ImGui.Spacing();
            ImGui.TextWrapped(GetLoc("BigPlayerDebuffs-Description"));
        }

        private void OnFrameworkUpdate(object framework)
        {
            try
            {
                if (!Enabled) return;
                UpdateTargetStatus();
            }
            catch (Exception ex)
            {
                DService.Log.Error(ex.ToString());
            }
        }

        private void UpdateTargetStatus()
        {
            if (DService.Targets.Target is IBattleChara target)
            {
                var playerAuras = 0;
                var localPlayerId = DService.ClientState.LocalPlayer?.EntityId;

                for (var i = 0; i < 30; i++)
                {
                    if (target.StatusList[i].SourceId == localPlayerId) playerAuras++;
                }

                if (this._currentPlayerDebuffs != playerAuras)
                {
                    var playerScale = ModuleConfig.BuffScale;

                    var targetInfoUnitBase = HelpersOm.GetAddonByName<AtkUnitBase>("_TargetInfo");
                    if (targetInfoUnitBase == null) return;
                    if (targetInfoUnitBase->UldManager.NodeList == null || targetInfoUnitBase->UldManager.NodeListCount < 53) return;

                    var targetInfoStatusUnitBase = HelpersOm.GetAddonByName<AtkUnitBase>("_TargetInfoBuffDebuff");
                    if (targetInfoStatusUnitBase == null) return;
                    if (targetInfoStatusUnitBase->UldManager.NodeList == null || targetInfoStatusUnitBase->UldManager.NodeListCount < 32) return;

                    this._currentPlayerDebuffs = playerAuras;

                    var adjustOffsetY = -(int)(41 * (playerScale-1.0f)/4.5);
                    var xIncrement = (int)((playerScale - 1.0f) * 25);

                    // 处理分离式目标框架
                    var growingOffsetX = 0;
                    for (var i = 0; i < 15; i++)
                    {
                        var node = targetInfoStatusUnitBase->UldManager.NodeList[31 - i];
                        node->X = i * 25 + growingOffsetX;

                        if (i < playerAuras)
                        {
                            node->ScaleX = playerScale;
                            node->ScaleY = playerScale;
                            node->Y = adjustOffsetY;
                            growingOffsetX += xIncrement;
                        }
                        else
                        {
                            node->ScaleX = 1.0f;
                            node->ScaleY = 1.0f;
                            node->Y = 0;
                        }
                        node->DrawFlags |= 0x1;
                    }

                    growingOffsetX = 0;
                    for (var i = 0; i < 15; i++)
                    {
                        var node = targetInfoUnitBase->UldManager.NodeList[32 - i];
                        node->X = i * 25 + growingOffsetX;

                        if (i < playerAuras)
                        {
                            node->ScaleX = playerScale;
                            node->ScaleY = playerScale;
                            node->Y = adjustOffsetY;
                            growingOffsetX += xIncrement;
                        }
                        else
                        {
                            node->ScaleX = 1.0f;
                            node->ScaleY = 1.0f;
                            node->Y = 0;
                        }
                        node->DrawFlags |= 0x1;
                    }

                    var newSecondRowOffset = (playerAuras > 0) ? (int)(playerScale*41) : 41;

                    if (newSecondRowOffset != this._currentSecondRowOffset)
                    {
                        for (var i = 16; i >= 2; i--)
                        {
                            targetInfoStatusUnitBase->UldManager.NodeList[i]->Y = newSecondRowOffset;
                            targetInfoStatusUnitBase->UldManager.NodeList[i]->DrawFlags |= 0x1;
                        }
                        for (var i = 17; i >= 3; i--)
                        {
                            targetInfoUnitBase->UldManager.NodeList[i]->Y = newSecondRowOffset;
                            targetInfoUnitBase->UldManager.NodeList[i]->DrawFlags |= 0x1;
                        }
                        this._currentSecondRowOffset = newSecondRowOffset;
                    }

                    targetInfoStatusUnitBase->UldManager.NodeList[1]->DrawFlags |= 0x4;
                    targetInfoStatusUnitBase->UldManager.NodeList[1]->DrawFlags |= 0x1;
                    targetInfoUnitBase->UldManager.NodeList[2]->DrawFlags |= 0x4;
                    targetInfoUnitBase->UldManager.NodeList[2]->DrawFlags |= 0x1;
                }
            }
        }

        private void ResetTargetStatus()
        {
            var targetInfoUnitBase = HelpersOm.GetAddonByName<AtkUnitBase>("_TargetInfo");
            if (targetInfoUnitBase == null) return;
            if (targetInfoUnitBase->UldManager.NodeList == null || targetInfoUnitBase->UldManager.NodeListCount < 53) return;

            var targetInfoStatusUnitBase = HelpersOm.GetAddonByName<AtkUnitBase>("_TargetInfoBuffDebuff");
            if (targetInfoStatusUnitBase == null) return;
            if (targetInfoStatusUnitBase->UldManager.NodeList == null || targetInfoStatusUnitBase->UldManager.NodeListCount < 32) return;

            for (var i = 0; i < 15; i++)
            {
                var node = targetInfoStatusUnitBase->UldManager.NodeList[31 - i];
                node->ScaleX = 1.0f;
                node->ScaleY = 1.0f;
                node->X = i * 25;
                node->Y = 0;
                node->DrawFlags |= 0x1;

                node = targetInfoUnitBase->UldManager.NodeList[32 - i];
                node->ScaleX = 1.0f;
                node->ScaleY = 1.0f;
                node->X = i * 25;
                node->Y = 0;
                node->DrawFlags |= 0x1;
            }

            for (var i = 17; i >= 2; i--)
            {
                targetInfoStatusUnitBase->UldManager.NodeList[i]->Y = 41;
                targetInfoStatusUnitBase->UldManager.NodeList[i]->DrawFlags |= 0x1;
            }
            
            for (var i = 18; i >= 3; i--)
            {
                targetInfoUnitBase->UldManager.NodeList[i]->Y = 41;
                targetInfoUnitBase->UldManager.NodeList[i]->DrawFlags |= 0x1;
            }

            targetInfoStatusUnitBase->UldManager.NodeList[1]->DrawFlags |= 0x4;
            targetInfoStatusUnitBase->UldManager.NodeList[2]->DrawFlags |= 0x4;
            
            _currentPlayerDebuffs = -1;
            _currentSecondRowOffset = 41;
        }
        
        private class Config : ModuleConfiguration
        {
            public float BuffScale = 1.4f;
        }
    }
}