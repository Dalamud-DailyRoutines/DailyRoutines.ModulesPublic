using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using DailyRoutines.Abstracts;

namespace DailyRoutines.ModulesPublic;

public unsafe class AutoAllowMultipleGames : DailyModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = GetLoc("AutoAllowMultipleGamesTitle"),
        Description = GetLoc("AutoAllowMultipleGamesDescription"),
        Category    = ModuleCategories.System,
        Author      = ["Fragile"]
    };

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(
        ulong ProcessHandle, int ProcessInformationClass, void* ProcessInformation, uint ProcessInformationLength, uint* ReturnLength);

    [DllImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NtQueryObject(ulong Handle, int ObjectInformationClass, void* ObjectInformation, uint ObjectInformationLength, uint* ReturnLength);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(ulong Handle);

    protected override void Init()
    {
        foreach (var handle in EnumHandles())
        {
            if (ObjectNameOrTypeName(handle, true) == "Mutant")
            {
                var name = ObjectNameOrTypeName(handle, false);
                if (name.Contains("6AA83AB5-BAC4-4a36-9F66-A309770760CB_ffxiv_game0", StringComparison.Ordinal)) 
                    CloseHandle(handle);
            }
        }
    }

    private static List<ulong> EnumHandles()
    {
        List<ulong> ret        = [];
        uint        bufferSize = 0x8000;
        while (true)
        {
            var buffer = new byte[bufferSize];
            fixed (byte* pbuf = &buffer[0])
            {
                var psnap = (ProcessHandleSnapshotInformation*)pbuf;
                psnap->NumberOfHandles = 0;
                // ProcessHandleInformation == 51
                uint retSize = 0;
                var  status  = NtQueryInformationProcess(ulong.MaxValue, 51, pbuf, bufferSize, &retSize);
                if ((uint)status == 0xC0000004) // STATUS_INFO_LENGTH_MISMATCH
                {
                    bufferSize = retSize;
                    continue;
                }

                if (status >= 0)
                {
                    var handles = (ProcessHandleTableEntryInfo*)(psnap + 1);
                    for (ulong i = 0; i < psnap->NumberOfHandles; ++i)
                        ret.Add(handles[i].HandleValue);
                }

                break;
            }
        }

        return ret;
    }

    private static string ObjectNameOrTypeName(ulong handle, bool typeName)
    {
        const uint bufferSize = 1024;
        var        buffer     = new byte[bufferSize];
        fixed (byte* pBuf = &buffer[0])
        {
            uint retSize = 0;
            var  status  = NtQueryObject(handle, typeName ? 2 : 1, pBuf, bufferSize, &retSize);
            if (status >= 0)
            {
                var name = (UnicodeString*)pBuf;
                if (name->Buffer != null)
                    return Encoding.Unicode.GetString(name->Buffer, name->Length);
            }
        }

        return string.Empty;
    }

    private struct ProcessHandleTableEntryInfo
    {
        public ulong HandleValue;
        public ulong HandleCount;
        public ulong PointerCount;
        public uint  GrantedAccess;
        public uint  ObjectTypeIndex;
        public uint  HandleAttributes;
        public uint  Reserved;
    }

    private struct ProcessHandleSnapshotInformation
    {
        public ulong NumberOfHandles;
        public ulong Reserved;
    }

    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public byte*  Buffer;
    }
}
