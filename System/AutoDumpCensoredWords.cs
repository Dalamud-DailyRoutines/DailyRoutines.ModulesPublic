#if DEBUG
using System.Runtime.InteropServices;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using OmenTools.Interop.Game.Models;
using OmenTools.OmenService;
using Task = System.Threading.Tasks.Task;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoDumpCensoredWords : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = "自动导出屏蔽词库",
        Description = "自动提取所有屏蔽词导出至文件",
        Category    = ModuleCategory.System
    };

    public override ModulePermission Permission { get; } = new() { CNOnly = true };

    private static readonly CompSig VulgarInstanceOffsetBaseSig =
        new("48 8B 81 ?? ?? ?? ?? 48 85 C0 74 ?? 48 8B D3");

    private nint vulgarInstanceOffset;

    protected override void Init()
    {
        if (vulgarInstanceOffset == nint.Zero)
            vulgarInstanceOffset = VulgarInstanceOffsetBaseSig.GetStatic();
    }

    protected override void ConfigUI()
    {
        if (ImGui.Button("Dump 全部屏蔽词"))
        {
            Task.Run(() =>
            {
                var words = DumpAllWords();
                var path  = Path.Combine(ConfigDirectoryPath, "CensoredWords.txt");
                File.WriteAllLines(path, words.OrderBy(x => x));
                NotifyHelper.Instance().Chat("导出成功");
            });
        }

        ImGuiOm.HelpMarker("遍历内存中的屏蔽词 trie / 哈希表");
    }

    private HashSet<string> DumpAllWords()
    {
        var allWords  = new HashSet<string>();
        var framework = Framework.Instance();
        if (framework == null) return allWords;

        for (var slot = 0; slot < 2; slot++)
        {
            var instancePtr = Marshal.ReadIntPtr((nint)framework + vulgarInstanceOffset + (slot * 8));
            if (instancePtr == nint.Zero) continue;
            var filterData = (byte*)Marshal.ReadIntPtr(instancePtr + 16);
            if (filterData == null) continue;
            var parser = new DicPointerParser(filterData);
            foreach (var words in parser.DumpAll().Values)
            foreach (var w in words)
                allWords.Add(w);
        }

        return allWords;
    }

    private class DicPointerParser
    (
        byte* basePtr
    )
    {
        private uint ReadU32(int off) => *(uint*)(basePtr + off);

        private ushort ReadU16(int off) => *(ushort*)(basePtr + off);

        private byte ReadU8(int off) => *(basePtr + off);

        private int GetTrieOffset(int level) => (int)ReadU32(0x8110 + (4 * level));

        private bool IsCharClassified(int charCode)
        {
            var idx = charCode >> 5;
            var bit = 1        << (charCode & 0x1F);
            return (ReadU32(0x0C + (4 * idx)) & bit) != 0;
        }

        private List<string> TraverseTrie(int level)
        {
            var words   = new List<string>();
            var trieOff = GetTrieOffset(level);
            if (trieOff == 0) return words;
            var trie       = trieOff;
            var BASE       = trie + 0x22C;
            var offset0    = (int)ReadU32(trie + 0x00);
            var offset1    = (int)ReadU32(trie + 0x04);
            var offset2    = (int)ReadU32(trie + 0x08);
            var offset3    = (int)ReadU32(trie + 0x0C);
            var offset4    = (int)ReadU32(trie + 0x10);
            var charLookup = BASE + offset0;
            var contTable  = BASE + offset1;
            var childLists = BASE + offset2;
            var byteSeqs   = BASE + offset3;
            var nodeArray  = BASE + offset4;

            int GetNode(int idx) => nodeArray + (16 * idx);

            byte GetNodeType(int node) => ReadU8(node);

            byte GetNodeCount(int node) => ReadU8(node + 4);

            ushort GetNodeCont(int node) => ReadU16(node + 8);

            uint GetNodeDataIdx(int node) => ReadU32(node + 0x0C);

            void Traverse(int node, string prefix)
            {
                var nodeType = GetNodeType(node);
                var dataIdx  = (int)(GetNodeDataIdx(node) >> 1);
                var contIdx  = GetNodeCont(node);

                if (nodeType == 0)
                {
                    var count          = GetNodeCount(node);
                    var childListStart = childLists + (2 * dataIdx);

                    for (var i = 0; i < count; i++)
                    {
                        var childChar    = ReadU16(childListStart + (2 * i));
                        var childNodeIdx = ReadU16(contTable      + (2 * (contIdx + i)));
                        var newPrefix    = prefix + (char)childChar;
                        if (childNodeIdx == 0) words.Add(newPrefix);
                        else Traverse(GetNode(childNodeIdx), newPrefix);
                    }
                }
                else if (nodeType == 1)
                {
                    var seqStart = byteSeqs + (2 * dataIdx);
                    var suffix   = "";
                    var si       = 0;

                    while (true)
                    {
                        var ch = ReadU16(seqStart + (2 * si));
                        if (ch == 0) break;
                        suffix += (char)ch;
                        si++;
                    }

                    var word = prefix + suffix;

                    if (contIdx == 0) words.Add(word);
                    else
                    {
                        var childNodeIdx = ReadU16(contTable + (2 * contIdx));
                        if (childNodeIdx == 0) words.Add(word);
                        else Traverse(GetNode(childNodeIdx), word);
                    }
                }
            }

            var highByteTable = trie + 0x2C;

            for (var hb = 0; hb < 256; hb++)
            {
                var block = ReadU16(highByteTable + (2 * hb));
                if (block == 0) continue;

                for (var lb = 0; lb < 256; lb++)
                {
                    var lookupIdx = (block << 8) | lb;
                    var nodeIdx   = ReadU16(charLookup + (2 * lookupIdx));
                    if (nodeIdx == 0) continue;
                    var charCode = (hb << 8) | lb;
                    Traverse(GetNode(nodeIdx), ((char)charCode).ToString());
                }
            }

            return words;
        }

        private List<string> TraverseHash()
        {
            var words   = new List<string>();
            var trieOff = GetTrieOffset(3);
            if (trieOff == 0) return words;
            var trie            = trieOff;
            var offset0         = (int)ReadU32(trie + 0x00);
            var offset1         = (int)ReadU32(trie + 0x04);
            var offset2         = (int)ReadU32(trie + 0x08);
            var hashTable       = trie + offset0 + 0x1C;
            var bucketEntryBase = trie + offset1 + 0x1C;
            var wordDataBase    = trie + offset2 + 0x1C;

            for (var bucketIdx = 0; bucketIdx < 1024; bucketIdx++)
            {
                var entryIdx = (int)ReadU32(hashTable + (4 * bucketIdx));

                while (entryIdx != 0)
                {
                    var entryOff    = bucketEntryBase + (8 * (entryIdx - 1));
                    var charListIdx = (int)ReadU32(entryOff);
                    var nextEntry   = (int)ReadU32(entryOff + 4);
                    var wordOff     = wordDataBase + (2 * charListIdx);
                    var word        = "";
                    var i           = 0;

                    while (true)
                    {
                        var ch = ReadU16(wordOff + (2 * i));
                        if (ch == 0) break;

                        if (ch <= 0x20 && ((0x100002400L >> ch) & 1) != 0)
                        {
                            i++;
                            continue;
                        }

                        if (IsCharClassified(ch))
                        {
                            i++;
                            continue;
                        }

                        word += (char)ch;
                        i++;
                    }

                    if (word.Length > 0) words.Add(word);
                    entryIdx = nextEntry;
                }
            }

            return words;
        }

        public Dictionary<string, List<string>> DumpAll()
        {
            var results = new Dictionary<string, List<string>>();
            foreach (var level in new[] { 0, 1, 2, 4 })
                results[$"L{level}"] = TraverseTrie(level).Distinct().ToList();
            results["L3"] = TraverseHash().Distinct().ToList();
            return results;
        }
    }
}

#endif
