// Original Author: Asvel
using DailyRoutines.Abstracts;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Lumina.Extensions;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DailyRoutines.Modules;

public class AutoConvertMapLink : DailyModuleBase
{
    public override ModuleInfo Info => new()
    {
        Title = GetLoc("AutoConvertMapLinkTitle"),
        Description = GetLoc("AutoConvertMapLinkDescription"),
        Category = ModuleCategories.System,
        Author = ["KirisameVanilla"],
    };

    private static readonly CompSig MessageParseCompSig =
        new("E8 ?? ?? ?? ?? 48 8B D0 48 8D 4C 24 30 E8 ?? ?? ?? ?? 48 8B 44 24 30 80 38 00 0F 84");

    private delegate IntPtr MessageParseDelegate(IntPtr a, IntPtr b);
    private static Hook<MessageParseDelegate>? MessageParseHook;

    public override void Init()
    {
        MessageParseHook ??=
            DService.Hook.HookFromSignature<MessageParseDelegate>(MessageParseCompSig.Get(), ParseMessageDetour);

        MessageParseHook.Enable();
    }

    private readonly Random random = new();
    public int GenerateRawPosition(float visibleCoordinate, short offset, ushort factor)
    {
        visibleCoordinate += (float)random.NextDouble() * 0.07f;
        var scale = factor / 100.0f;
        var scaledPos = ((((visibleCoordinate - 1.0f) * scale / 41.0f) * 2048.0f) - 1024.0f) / scale;
        return (int)Math.Ceiling(scaledPos - offset) * 1000;
    }

    private readonly Regex mapLinkPattern = new(
        @"\uE0BB(?<map>.+?)(?<instance>[\ue0b1-\ue0b9])? \( (?<x>\d{1,2}\.\d)  , (?<y>\d{1,2}\.\d) \)",
        RegexOptions.Compiled);

    private IntPtr ParseMessageDetour(IntPtr a, IntPtr b)
    {
        var ret = MessageParseHook.Original(a, b);
        try
        {
            var pMessage = Marshal.ReadIntPtr(ret);
            var length = 0;
            while (Marshal.ReadByte(pMessage, length) != 0) length++;
            var message = new byte[length];
            Marshal.Copy(pMessage, message, 0, length);

            var parsed = SeString.Parse(message);
            foreach (var payload in parsed.Payloads)
            {
                if (payload is AutoTranslatePayload p && p.Encode()[3] == 0xC9 && p.Encode()[4] == 0x04)
                {
                    return ret;
                }
            }
            for (var i = 0; i < parsed.Payloads.Count; i++)
            {
                if (parsed.Payloads[i] is not TextPayload payload) continue;
                var match = mapLinkPattern.Match(payload.Text);
                if (!match.Success) continue;

                var mapName = match.Groups["map"].Value;
                
                var zone = PresetSheet.Zones.Values.FirstOrNull(x => x.PlaceName.Value.Name.ExtractText() == mapName);
                if (zone is null) {
                    DService.Log.Warning("Can't find map {0}", mapName);
                    continue;
                }

                var (territoryId, mapId) = (zone.Value.RowId, zone.Value.Map.RowId);

                if (!LuminaGetter.TryGetRow<Lumina.Excel.Sheets.Map>(mapId, out var map))
                {
                    continue;
                }

                var rawX = GenerateRawPosition(float.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture), map.OffsetX, map.SizeFactor);
                var rawY = GenerateRawPosition(float.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture), map.OffsetY, map.SizeFactor);
                if (match.Groups["instance"].Value != "")
                {
                    mapId |= (match.Groups["instance"].Value[0] - 0xe0b0u) << 16;
                }
                
                var newPayloads = new List<Payload>();
                if (match.Index > 0)
                {
                    newPayloads.Add(new TextPayload(payload.Text[..match.Index]));
                }
                newPayloads.Add(new PreMapLinkPayload(territoryId, mapId, rawX, rawY));
                if (match.Index + match.Length < payload.Text.Length)
                {
                    newPayloads.Add(new TextPayload(payload.Text[(match.Index + match.Length)..]));
                }
                parsed.Payloads.RemoveAt(i);
                parsed.Payloads.InsertRange(i, newPayloads);

                var newMessage = parsed.Encode();
                var messageCapacity = Marshal.ReadInt64(ret + 8);
                if (newMessage.Length + 1 > messageCapacity)
                {
                    return ret;
                }
                Marshal.WriteInt64(ret + 16, newMessage.Length + 1);
                Marshal.Copy(newMessage, 0, pMessage, newMessage.Length);
                Marshal.WriteByte(pMessage, newMessage.Length, 0x00);

                break;
            }
        }
        catch (Exception ex)
        {
            DService.Log.Error(ex, "Exception on HandleParseMessageDetour.");
        }
        return ret;
    }

    public class PreMapLinkPayload(uint territoryTypeId, uint mapId, int rawX, int rawY) : Payload
    {
        public override PayloadType Type => PayloadType.AutoTranslateText;

        private readonly int rawZ = -30000;

        protected override byte[] EncodeImpl()
        {
            var territoryBytes = MakeInteger(territoryTypeId);
            var mapBytes = MakeInteger(mapId);
            var xBytes = MakeInteger(unchecked((uint)rawX));
            var yBytes = MakeInteger(unchecked((uint)rawY));
            var zBytes = MakeInteger(unchecked((uint)this.rawZ));

            var chunkLen = 4 + territoryBytes.Length + mapBytes.Length + xBytes.Length + yBytes.Length + zBytes.Length;

            var bytes = new List<byte>()
            {
                START_BYTE,
                (byte)SeStringChunkType.AutoTranslateKey, (byte)chunkLen, 0xC9, 0x04
            };
            bytes.AddRange(territoryBytes);
            bytes.AddRange(mapBytes);
            bytes.AddRange(xBytes);
            bytes.AddRange(yBytes);
            bytes.AddRange(zBytes);
            bytes.Add(0x01);  // FIXME: what is this?
            bytes.Add(END_BYTE);

            return bytes.ToArray();
        }

        protected override void DecodeImpl(BinaryReader reader, long endOfStream) => throw new NotImplementedException();
    }
}
