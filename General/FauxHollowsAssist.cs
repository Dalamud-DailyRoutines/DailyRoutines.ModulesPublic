using DailyRoutines.Abstracts;
using DailyRoutines.Infos;
using DailyRoutines.Managers;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DailyRoutines.Modules;

public unsafe class FauxHollowsAssist : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title       = GetLoc("FauxHollowsAssistTitle"),
        Description = GetLoc("FauxHollowsAssistDescription"),
        Category    = ModuleCategories.General,
        Author      = ["Veever"]
    };

    private static Config ModuleConfig = null!;
    private static readonly Throttler<string> Throttler = new();

    private BoardState _board   = new();
    private Solver _solver      = new();

    public override void Init()
    {
        ModuleConfig = LoadConfig<Config>() ?? new();
        Overlay ??= new(this);
        Overlay.WindowName = "AutoFauxHollows-Overlay";
        Overlay.Flags |= ImGuiWindowFlags.AlwaysAutoResize;
        FrameworkManager.Register(true, OnUpdate);
    }

    public override void ConfigUI()
    {
        if (ImGui.Checkbox(GetLoc("FauxHollowsAssist-HighlightBestStep"), ref ModuleConfig.HighlightBestStep))
            ModuleConfig.Save(this);

        if (ImGui.Checkbox(GetLoc("FauxHollowsAssist-PrioritizeSwords"), ref ModuleConfig.PrioritizeSwords))
        {
            _solver.FindSwordsFirst = ModuleConfig.PrioritizeSwords;
            ModuleConfig.Save(this);
        }

        if (ImGui.Checkbox(GetLoc("FauxHollowsAssist-PrioritizeSwordsOverlay"), ref ModuleConfig.ShowSwordsSettingsOverlay))
        {
            Overlay.IsOpen = ModuleConfig.ShowSwordsSettingsOverlay;
            ModuleConfig.Save(this);
        }
    }

    public override void Uninit()
    {
        FrameworkManager.Unregister(OnUpdate);
        base.Uninit();
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Throttler.Throttle("AutoFauxHollows_Update", 1000)) return;

        if (DService.ClientState.TerritoryType != IdyllshireId)
        {
            if (Overlay.IsOpen && ModuleConfig.ShowSwordsSettingsOverlay)
            {
                Overlay.IsOpen = false;
            }
            return;
        }

        var addon = (AddonWeeklyPuzzle*)DService.Gui.GetAddonByName("WeeklyPuzzle", 1);
        bool puzzleActive = addon != null && addon->IsVisible && addon->UldManager.LoadedState == AtkLoadState.Loaded;

        if (puzzleActive)
        {
            if (ModuleConfig.ShowSwordsSettingsOverlay && !Overlay.IsOpen)
                Overlay.IsOpen = true;

            var tileState = ParseTileInformation(addon);
            _board.Update(tileState);

            if (ModuleConfig.HighlightBestStep)
            {
                var solution = _solver.Solve(_board);
                var bestScore = solution.Max();
                if (bestScore == 0)
                    bestScore = -1;

                UpdateAddonColors(addon, solution, bestScore);
            }
        }
        else
        {
            if (Overlay.IsOpen && ModuleConfig.ShowSwordsSettingsOverlay)
            {
                Overlay.IsOpen = false;
            }
        }
    }

    private BoardState.Tile[] ParseTileInformation(AddonWeeklyPuzzle* addon)
    {
        var result = new BoardState.Tile[BoardState.Width * BoardState.Height];
        int tileIndex = 0;
        for (int y = 0; y < BoardState.Height; ++y)
        {
            for (int x = 0; x < BoardState.Width; ++x)
            {
                ref var tileState = ref result[tileIndex++];

                var tileButton = GetTileButton(addon, x, y);
                var tileBackgroundImage = GetBackgroundImageNode(tileButton);
                var tileIconImage = GetIconImageNode(tileButton);
                tileState = (WeeklyPuzzleTexture)tileBackgroundImage->PartId switch
                {
                    WeeklyPuzzleTexture.Hidden => BoardState.Tile.Hidden,
                    WeeklyPuzzleTexture.Blocked => BoardState.Tile.Blocked,
                    WeeklyPuzzleTexture.Blank => !tileIconImage->IsVisible() ? BoardState.Tile.Empty : (WeeklyPuzzlePrizeTexture)tileIconImage->PartId switch
                    {
                        WeeklyPuzzlePrizeTexture.BoxTL => BoardState.Tile.BoxTL,
                        WeeklyPuzzlePrizeTexture.BoxTR => BoardState.Tile.BoxTR,
                        WeeklyPuzzlePrizeTexture.BoxBL => BoardState.Tile.BoxBL,
                        WeeklyPuzzlePrizeTexture.BoxBR => BoardState.Tile.BoxBR,
                        WeeklyPuzzlePrizeTexture.ChestTL => BoardState.Tile.ChestTL,
                        WeeklyPuzzlePrizeTexture.ChestTR => BoardState.Tile.ChestTR,
                        WeeklyPuzzlePrizeTexture.ChestBL => BoardState.Tile.ChestBL,
                        WeeklyPuzzlePrizeTexture.ChestBR => BoardState.Tile.ChestBR,
                        WeeklyPuzzlePrizeTexture.SwordsTL => BoardState.Tile.SwordsTL,
                        WeeklyPuzzlePrizeTexture.SwordsTR => BoardState.Tile.SwordsTR,
                        WeeklyPuzzlePrizeTexture.SwordsML => BoardState.Tile.SwordsML,
                        WeeklyPuzzlePrizeTexture.SwordsMR => BoardState.Tile.SwordsMR,
                        WeeklyPuzzlePrizeTexture.SwordsBL => BoardState.Tile.SwordsBL,
                        WeeklyPuzzlePrizeTexture.SwordsBR => BoardState.Tile.SwordsBR,
                        WeeklyPuzzlePrizeTexture.Commander => BoardState.Tile.Commander,
                        _ => BoardState.Tile.Unknown
                    },
                    _ => BoardState.Tile.Unknown
                };

                if (tileState == BoardState.Tile.Unknown)
                    NotificationError($"Unexpected tile state at {x}x{y}: bg={tileBackgroundImage->PartId}, icon={tileIconImage->PartId}");

                var rotation = tileIconImage->AtkResNode.Rotation;
                if (rotation < 0)
                    tileState |= BoardState.Tile.RotatedL;
                else if (rotation > 0)
                    tileState |= BoardState.Tile.RotatedR;
            }
        }
        return result;
    }

    private void UpdateAddonColors(AddonWeeklyPuzzle* addon, int[] solution, int bestScore)
    {
        int tileIndex = 0;
        for (int y = 0; y < BoardState.Height; ++y)
        {
            for (int x = 0; x < BoardState.Width; ++x)
            {
                var soln = solution[tileIndex++];
                var tileButton = GetTileButton(addon, x, y);
                var tileBackgroundImage = GetBackgroundImageNode(tileButton);
                var (r, g, b) = soln switch
                {
                    Solver.ConfirmedSword => (31, 174, 186),
                    Solver.ConfirmedBoxChest => (255, 105, 180),
                    Solver.PotentialFox => (255, 215, 0),
                    _ => soln == bestScore ? (32, 143, 46) : (0, 0, 0)
                };
                tileBackgroundImage->AtkResNode.AddRed = (short)r;
                tileBackgroundImage->AtkResNode.AddGreen = (short)g;
                tileBackgroundImage->AtkResNode.AddBlue = (short)b;
            }
        }
    }


    private AtkComponentButton* GetTileButton(AddonWeeklyPuzzle* addon, int x, int y) => addon->GameBoard[y][x].Button;
    private AtkImageNode* GetBackgroundImageNode(AtkComponentButton* button) => (AtkImageNode*)button->AtkComponentBase.UldManager.NodeList[3];
    private AtkImageNode* GetIconImageNode(AtkComponentButton* button) => (AtkImageNode*)button->AtkComponentBase.UldManager.NodeList[6];

    public override void OverlayUI()
    {
        if (ModuleConfig.ShowSwordsSettingsOverlay)
        {
            ImGui.TextColored(Orange, "幻巧拼图设置");
            ImGui.Separator();

            if (ImGui.Checkbox("优先寻找大剑", ref ModuleConfig.PrioritizeSwords))
            {
                _solver.FindSwordsFirst = ModuleConfig.PrioritizeSwords;
                ModuleConfig.Save(this);
            }

            ImGui.SameLine();
            ImGuiOm.HelpMarker("选中此选项后将优先寻找大剑");
        }
    }

    private enum WeeklyPuzzleTexture
    {
        // Background Texture enum
        Hidden = 5,
        Blank = 6,
        Blocked = 9,
    }

    private enum WeeklyPuzzlePrizeTexture
    {
        TinyBox = 0,
        TinySwords = 1,
        TinyChest = 2,
        TinyCommander = 3,
        BoxTL = 4,
        BoxTR = 5,
        BoxBL = 6,
        BoxBR = 7,
        ChestTL = 8,
        ChestTR = 9,
        ChestBL = 10,
        ChestBR = 11,
        SwordsTL = 12,
        SwordsTR = 13,
        SwordsML = 14,
        SwordsMR = 15,
        SwordsBL = 16,
        SwordsBR = 17,
        Commander = 18,
    }
    private const int IdyllshireId = 478;

    private class Config : ModuleConfiguration
    {
        public bool HighlightBestStep = true;
        public bool PrioritizeSwords;
        public bool ShowSwordsSettingsOverlay = true;
    }

    public struct BitMask
    {
        public ulong Raw;

        public BitMask(ulong raw = 0) { Raw = raw; }

        public static BitMask Build(params int[] bits)
        {
            var res = new BitMask();
            foreach (var bit in bits)
                res.Set(bit);
            return res;
        }

        public bool this[int index]
        {
            get => (Raw & MaskForBit(index)) != 0;
            set
            {
                if (value)
                    Set(index);
                else
                    Clear(index);
            }
        }

        public void Reset() => Raw = 0;
        public bool Any() => Raw != 0;
        public bool None() => Raw == 0;
        public void Set(int index) => Raw |= MaskForBit(index);
        public void Clear(int index) => Raw &= ~MaskForBit(index);
        public void Toggle(int index) => Raw ^= MaskForBit(index);
        public int NumSetBits() => System.Numerics.BitOperations.PopCount(Raw);
        public int LowestSetBit() => System.Numerics.BitOperations.TrailingZeroCount(Raw); // returns out-of-range value (64) if no bits are set
        public int HighestSetBit() => 63 - System.Numerics.BitOperations.LeadingZeroCount(Raw); // returns out-of-range value (-1) if no bits are set

        public static BitMask operator ~(BitMask a) => new(~a.Raw);
        public static BitMask operator &(BitMask a, BitMask b) => new(a.Raw & b.Raw);
        public static BitMask operator |(BitMask a, BitMask b) => new(a.Raw | b.Raw);
        public static BitMask operator ^(BitMask a, BitMask b) => new(a.Raw ^ b.Raw);

        public IEnumerable<int> SetBits()
        {
            var v = Raw;
            while (v != 0)
            {
                var index = System.Numerics.BitOperations.TrailingZeroCount(v);
                yield return index;
                v &= ~(1ul << index);
            }
        }

        private readonly ulong MaskForBit(int index) => (uint)index < 64 ? (1ul << index) : 0;
    }
    
    private class BoardState
    {
        [Flags]
        public enum Tile
        {
            Unknown = 0,
            Hidden = 1 << 0,
            Blocked = 1 << 1,
            Empty = 1 << 2,
            BoxTL = 1 << 3,
            BoxTR = 1 << 4,
            BoxBL = 1 << 5,
            BoxBR = 1 << 6,
            ChestTL = 1 << 7,
            ChestTR = 1 << 8,
            ChestBL = 1 << 9,
            ChestBR = 1 << 10,
            SwordsTL = 1 << 11,
            SwordsTR = 1 << 12,
            SwordsML = 1 << 13,
            SwordsMR = 1 << 14,
            SwordsBL = 1 << 15,
            SwordsBR = 1 << 16,
            Commander = 1 << 17,
            RotatedL = 1 << 18,
            RotatedR = 1 << 19,
            RotatedEither = RotatedL | RotatedR,
            Box = BoxTL | BoxTR | BoxBL | BoxBR | RotatedEither,
            Chest = ChestTL | ChestTR | ChestBL | ChestBR | RotatedEither,
            BoxChest = Box | Chest,
            Swords = SwordsTL | SwordsTR | SwordsML | SwordsMR | SwordsBL | SwordsBR | RotatedEither,
        }

        public const int Width = 6;
        public const int Height = 6;

        public Tile[] Tiles = new Tile[Width * Height];
        public BitMask Blockers;
        public int SwordsTL = -1;
        public bool SwordsHorizontal = false;
        public int BoxChestTL = -1;

        public bool Update(Tile[] tiles)
        {
            var data = AnalyzeBoard(tiles);
            if (data != null)
            {
                Tiles = tiles;
                (Blockers, SwordsTL, SwordsHorizontal, BoxChestTL) = data.Value;
                return true;
            }
            else
            {
                Array.Fill(Tiles, Tile.Unknown);
                Blockers.Reset();
                SwordsTL = -1;
                SwordsHorizontal = false;
                BoxChestTL = -1;
                return false;
            }
        }

        private static (BitMask blockers, int swordsTL, bool swordsHoriz, int boxChestTL)? AnalyzeBoard(Tile[] tiles)
        {
            BitMask blockers = new();
            int swordsTL = -1;
            bool swordsHoriz = false;
            int boxChestTL = -1;
            for (int i = 0; i < tiles.Length; ++i)
            {
                var t = tiles[i];
                if (t.HasFlag(Tile.Blocked))
                {
                    blockers.Set(i);
                }
                else if ((t & Tile.Swords) != 0)
                {
                    var tl = i - TLOffsetSwords(t);
                    bool horiz = (t & Tile.RotatedEither) != 0;
                    if (tl > i || swordsTL != -1 && (swordsTL != tl || swordsHoriz != horiz))
                        return null;
                    swordsTL = tl;
                    swordsHoriz = horiz;
                }
                else if ((t & Tile.BoxChest) != 0)
                {
                    var tl = i - TLOffsetBoxChest(t);
                    if (tl > i || boxChestTL != -1 && boxChestTL != tl)
                        return null;
                    boxChestTL = tl;
                }
            }
            return (blockers, swordsTL, swordsHoriz, boxChestTL);
        }

        private static int TLOffsetSwords(Tile t) => t switch
        {
            Tile.SwordsTL => 0,
            Tile.SwordsTR => 1,
            Tile.SwordsML => Width,
            Tile.SwordsMR => Width + 1,
            Tile.SwordsBL => Width * 2,
            Tile.SwordsBR => Width * 2 + 1,

            Tile.SwordsTL | Tile.RotatedL => Width,
            Tile.SwordsTR | Tile.RotatedL => 0,
            Tile.SwordsML | Tile.RotatedL => Width + 1,
            Tile.SwordsMR | Tile.RotatedL => 1,
            Tile.SwordsBL | Tile.RotatedL => Width + 2,
            Tile.SwordsBR | Tile.RotatedL => 2,

            Tile.SwordsTL | Tile.RotatedR => 2,
            Tile.SwordsTR | Tile.RotatedR => Width + 2,
            Tile.SwordsML | Tile.RotatedR => 1,
            Tile.SwordsMR | Tile.RotatedR => Width + 1,
            Tile.SwordsBL | Tile.RotatedR => 0,
            Tile.SwordsBR | Tile.RotatedR => Width,

            _ => -1
        };

        private static int TLOffsetBoxChest(Tile t) => t switch
        {
            Tile.BoxTL or Tile.ChestTL => 0,
            Tile.BoxTR or Tile.ChestTR => 1,
            Tile.BoxBL or Tile.ChestBL => Width,
            Tile.BoxBR or Tile.ChestBR => Width + 1,

            Tile.BoxTL | Tile.RotatedL or Tile.ChestTL | Tile.RotatedL => Width,
            Tile.BoxTR | Tile.RotatedL or Tile.ChestTR | Tile.RotatedL => 0,
            Tile.BoxBL | Tile.RotatedL or Tile.ChestBL | Tile.RotatedL => Width + 1,
            Tile.BoxBR | Tile.RotatedL or Tile.ChestBR | Tile.RotatedL => 1,

            Tile.BoxTL | Tile.RotatedR or Tile.ChestTL | Tile.RotatedR => 1,
            Tile.BoxTR | Tile.RotatedR or Tile.ChestTR | Tile.RotatedR => Width + 1,
            Tile.BoxBL | Tile.RotatedR or Tile.ChestBL | Tile.RotatedR => 0,
            Tile.BoxBR | Tile.RotatedR or Tile.ChestBR | Tile.RotatedR => Width,

            _ => -1
        };
    }


    private class Solver
    {
        // special scores
        public const int ConfirmedSword = -2;
        public const int ConfirmedBoxChest = -3;
        public const int PotentialFox = -4;

        public Patterns PatternDB = new();
        public bool FindSwordsFirst;

        public int[] Solve(BoardState board)
        {
            var result = new int[BoardState.Width * BoardState.Height];
            var sheet = MatchingSheet(board);
            if (sheet != null)
            {
                HashSet<(int, bool)> potentialSwords = new();
                HashSet<int> potentialBoxes = new();
                HashSet<ulong> potentialFoxes = new();
                foreach (var (r, c) in MatchingCells(board, sheet))
                {
                    potentialSwords.Add((r.SwordsTL, r.SwordsHorizontal));
                    potentialBoxes.Add(c.ChestTL);
                    potentialFoxes.Add(c.Foxes.Raw);
                }

                var swordsScore = potentialSwords.Count == 1 ? ConfirmedSword : 10;
                var boxScore = potentialBoxes.Count == 1 ? ConfirmedBoxChest : (FindSwordsFirst && potentialSwords.Count > 1) ? 0 : 10;
                foreach (var (tl, h) in potentialSwords)
                    foreach (var i in SwordIndices(tl, h))
                        result[i] += swordsScore;
                foreach (var tl in potentialBoxes)
                    foreach (var i in BoxChestIndices(tl))
                        result[i] += boxScore;
                foreach (var foxes in potentialFoxes)
                    foreach (var f in new BitMask(foxes).SetBits())
                        if (potentialFoxes.Count == 1)
                            result[f] = PotentialFox;
                        else
                            result[f] += 1;
            }
            return result;
        }

        public bool MatchesSheet(BoardState board, Patterns.Sheet sheet) => sheet.Blockers.Raw == board.Blockers.Raw;
        public bool MatchesRow(BoardState board, Patterns.Row row) => board.SwordsTL != -1
            ? (board.SwordsTL == row.SwordsTL && board.SwordsHorizontal == row.SwordsHorizontal)
            : AllHidden(SwordIndices(row.SwordsTL, row.SwordsHorizontal), board);
        public bool MatchesCell(BoardState board, Patterns.Cell cell) => board.BoxChestTL != -1
            ? board.BoxChestTL == cell.ChestTL
            : AllHidden(BoxChestIndices(cell.ChestTL), board);

        public Patterns.Sheet? MatchingSheet(BoardState board) => PatternDB.KnownPatterns.Find(s => MatchesSheet(board, s));
        public IEnumerable<Patterns.Row> MatchingRows(BoardState board, Patterns.Sheet sheet) => sheet.Rows.Where(r => MatchesRow(board, r));
        public IEnumerable<Patterns.Cell> MatchingCells(BoardState board, Patterns.Row row) => row.Cells.Where(c => MatchesCell(board, c));

        public IEnumerable<(Patterns.Row row, Patterns.Cell cell)> MatchingCells(BoardState board, Patterns.Sheet sheet)
        {
            foreach (var r in MatchingRows(board, sheet))
                foreach (var c in MatchingCells(board, r))
                    yield return (r, c);
        }

        public static IEnumerable<int> RectIndices(int tl, int w, int h)
        {
            int sx = tl % BoardState.Width, sy = tl / BoardState.Width;
            for (int y = 0; y < h; ++y)
                for (int x = 0; x < w; ++x)
                    yield return (sy + y) * BoardState.Width + (sx + x);
        }
        public static IEnumerable<int> SwordIndices(int tl, bool horiz) => horiz ? RectIndices(tl, 3, 2) : RectIndices(tl, 2, 3);
        public static IEnumerable<int> BoxChestIndices(int tl) => RectIndices(tl, 2, 2);

        private bool AllHidden(IEnumerable<int> indices, BoardState board) => indices.All(i => board.Tiles[i] == BoardState.Tile.Hidden);
    }

    public class Patterns
    {
        public class Cell
        {
            public int ChestTL;
            public BitMask Foxes;

            public Cell(int chestTL, BitMask foxes)
            {
                ChestTL = chestTL;
                Foxes = foxes;
            }
        }

        public class Row
        {
            public int SwordsTL;
            public bool SwordsHorizontal;
            public Cell[] Cells;

            public Row(int swordsTL, bool swordsHorizontal, Cell[] cells)
            {
                SwordsTL = swordsTL;
                SwordsHorizontal = swordsHorizontal;
                Cells = cells;
            }
        }

        public class Sheet
        {
            public BitMask Blockers;
            public Row[] Rows;

            public Sheet(BitMask blockers, Row[] rows)
            {
                Blockers = blockers;
                Rows = rows;
            }
        }

        public List<Sheet> KnownPatterns = new();

        private static Sheet[] KnownBaseSheets = { // 'up' variants
        new Sheet(BitMask.Build(8, 10, 13, 26, 35), new[] { // A
            new Row(16, false, new[] {
                new Cell(0, BitMask.Build(3, 4, 12, 30)),
                new Cell(14, BitMask.Build(3, 4, 12, 30)),
            }),
            new Row(15, false, new[] {
                new Cell(0, BitMask.Build(9, 17, 33, 34)),
                new Cell(18, BitMask.Build(9, 17, 33, 34)),
            }),
            new Row(21, false, new[] {
                new Cell(0, BitMask.Build(5, 15, 16, 20)),
                new Cell(18, BitMask.Build(5, 15, 16, 20)),
            }),
            new Row(15, true, new[] {
                new Cell(18, BitMask.Build(3, 4, 12, 30)),
                new Cell(27, BitMask.Build(3, 4, 12, 30)),
            }),
            new Row(21, true, new[] {
                new Cell(0, BitMask.Build(5, 15, 16, 20)),
                new Cell(24, BitMask.Build(5, 15, 16, 20)),
            }),
            new Row(14, true, new[] {
                new Cell(0, BitMask.Build(2, 11, 29, 32)),
                new Cell(24, BitMask.Build(2, 11, 29, 32)),
            }),
            new Row(18, false, new[] {
                new Cell(27, BitMask.Build(2, 11, 29, 32)),
                new Cell(16, BitMask.Build(2, 11, 29, 32)),
                new Cell(22, BitMask.Build(9, 17, 33, 34)),
                new Cell(14, BitMask.Build(9, 17, 33, 34)),
            }),
        }),
        new Sheet(BitMask.Build(3, 13, 16, 21, 32), new[] { // B
            new Row(0, true, new[] {
                new Cell(4, BitMask.Build(15, 18, 26, 33)),
                new Cell(28, BitMask.Build(15, 18, 26, 33)),
                new Cell(24, BitMask.Build(10, 14, 20, 22)),
                new Cell(27, BitMask.Build(10, 14, 20, 22)),
            }),
            new Row(22, false, new[] {
                new Cell(24, BitMask.Build(15, 18, 26, 33)),
                new Cell(1, BitMask.Build(15, 18, 26, 33)),
                new Cell(19, BitMask.Build(1, 6, 8, 17)),
                new Cell(4, BitMask.Build(1, 6, 8, 17)),
            }),
            new Row(27, true, new[] {
                new Cell(4, BitMask.Build(1, 6, 8, 17)),
                new Cell(24, BitMask.Build(1, 6, 8, 17)),
            }),
            new Row(18, false, new[] {
                new Cell(0, BitMask.Build(10, 14, 20, 22)),
                new Cell(28, BitMask.Build(10, 14, 20, 22)),
                new Cell(8, BitMask.Build(2, 5, 12, 35)),
                new Cell(22, BitMask.Build(2, 5, 12, 35)),
            }),
            new Row(18, true, new[] {
                new Cell(0, BitMask.Build(2, 5, 12, 35)),
                new Cell(27, BitMask.Build(2, 5, 12, 35)),
            }),
        }),
        new Sheet(BitMask.Build(4, 7, 15, 25, 33), new[] { // С
            new Row(10, false, new[] {
                new Cell(12, BitMask.Build(0, 21, 27, 31)),
                new Cell(28, BitMask.Build(0, 21, 27, 31)),
            }),
            new Row(16, false, new[] {
                new Cell(12, BitMask.Build(8, 24, 34, 35)),
                new Cell(20, BitMask.Build(8, 24, 34, 35)),
            }),
            new Row(22, false, new[] {
                new Cell(2, BitMask.Build(6, 10, 17, 26)),
                new Cell(13, BitMask.Build(6, 10, 17, 26)),
                new Cell(20, BitMask.Build(1, 5, 14, 30)),
                new Cell(12, BitMask.Build(1, 5, 14, 30)),
            }),
            new Row(21, true, new[] {
                new Cell(2, BitMask.Build(6, 10, 17, 26)),
                new Cell(12, BitMask.Build(6, 10, 17, 26)),
                new Cell(13, BitMask.Build(8, 24, 34, 35)),
                new Cell(10, BitMask.Build(8, 24, 34, 35)),
            }),
            new Row(20, true, new[] {
                new Cell(2, BitMask.Build(1, 5, 14, 30)),
                new Cell(10, BitMask.Build(1, 5, 14, 30)),
            }),
            new Row(12, true, new[] {
                new Cell(2, BitMask.Build(0, 21, 27, 31)), // weird, should be one more row?
            }),
        }),
        new Sheet(BitMask.Build(7, 16, 18, 27, 32), new[] { // D
            new Row(2, true, new[] {
                new Cell(14, BitMask.Build(11, 24, 31, 34)),
                new Cell(19, BitMask.Build(11, 24, 31, 34)),
            }),
            new Row(3, true, new[] {
                new Cell(22, BitMask.Build(0, 15, 21, 35)),
                new Cell(24, BitMask.Build(0, 15, 21, 35)),
            }),
            new Row(2, false, new[] {
                new Cell(4, BitMask.Build(1, 13, 17, 26)),
                new Cell(24, BitMask.Build(1, 13, 17, 26)),
            }),
            new Row(8, false, new[] {
                new Cell(28, BitMask.Build(2, 3, 23, 33)),
                new Cell(24, BitMask.Build(2, 3, 23, 33)),
            }),
            new Row(22, false, new[] {
                new Cell(2, BitMask.Build(1, 13, 17, 26)),
                new Cell(4, BitMask.Build(1, 13, 17, 26)),
            }),
            new Row(13, false, new[] {
                new Cell(2, BitMask.Build(0, 15, 21, 35)),
                new Cell(22, BitMask.Build(0, 15, 21, 35)),
            }),
            new Row(13, true, new[] {
                new Cell(2, BitMask.Build(11, 24, 31, 34)),
                new Cell(22, BitMask.Build(11, 24, 31, 34)),
                new Cell(4, BitMask.Build(2, 3, 23, 33)),
                new Cell(28, BitMask.Build(2, 3, 23, 33)),
            }),
        }),
    };

        public Patterns()
        {
            foreach (var s in KnownBaseSheets)
            {
                KnownPatterns.Add(s);
                var r = RotateSheetLeft(s);
                KnownPatterns.Add(r);
                r = RotateSheetLeft(r);
                KnownPatterns.Add(r);
                r = RotateSheetLeft(r);
                KnownPatterns.Add(r);
            }
        }

        private static int RotateCellIndexLeft(int cell)
        {
            var x = cell % BoardState.Width;
            var y = cell / BoardState.Width;
            var yr = x;
            var xr = BoardState.Width - 1 - y;
            return yr * BoardState.Width + xr;
        }

        private static BitMask RotateCellMaskLeft(BitMask cells) => BitMask.Build(cells.SetBits().Select(RotateCellIndexLeft).ToArray());

        private static Cell RotateCellLeft(Cell cell) => new(RotateCellIndexLeft(cell.ChestTL) - 1, RotateCellMaskLeft(cell.Foxes));
        private static Row RotateRowLeft(Row row) => new(RotateCellIndexLeft(row.SwordsTL) - (row.SwordsHorizontal ? 1 : 2), !row.SwordsHorizontal, row.Cells.Select(RotateCellLeft).ToArray());
        private static Sheet RotateSheetLeft(Sheet sheet) => new(RotateCellMaskLeft(sheet.Blockers), sheet.Rows.Select(RotateRowLeft).ToArray());
    }
}
