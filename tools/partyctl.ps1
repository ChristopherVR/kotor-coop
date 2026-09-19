# Reads or forces CSWPartyTable+0xF0 (pt_controlled_npc) in a running KOTOR: the field a script's
# SwitchPlayerCharacter sets to the rescuer's NPC slot for the whole Leviathan brig segment
# (docs/research/leviathan.md 3.3). RIG STAGING ONLY: it flips the field the solo-segment trigger
# watches WITHOUT running the engine's own switch, so it proves the mod's wiring (host segment ->
# joiner parks and spectates), not the story. Always put it back to -1 afterwards.
#
#   pwsh tools\partyctl.ps1 -ProcId 1234              # read
#   pwsh tools\partyctl.ps1 -ProcId 1234 -Set 3       # pretend a script switched to NPC slot 3
#   pwsh tools\partyctl.ps1 -ProcId 1234 -Set -1      # back to normal
#
# Chain (Odyssey.Core.cs / Odyssey.Journal.cs): appManager = [0x7A39FC]; server = [appManager+8];
# serverInternal = [server+4]; partyTable = serverInternal + 0x1B770; the field is partyTable+0xF0.
param(
    [Parameter(Mandatory = $true)][int]$ProcId,
    [Nullable[int]]$Set = $null
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PartyCtlMem {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  public static int ReadInt(IntPtr h, long addr) {
    byte[] b = new byte[4]; int r;
    if (!ReadProcessMemory(h, new IntPtr(addr), b, 4, out r) || r != 4) throw new Exception("read failed at 0x" + addr.ToString("X"));
    return BitConverter.ToInt32(b, 0);
  }
}
'@

$h = [PartyCtlMem]::OpenProcess(0x0438, $false, [uint32]$ProcId)
if ($h -eq [IntPtr]::Zero) { throw "cannot open process $ProcId" }
try {
    $app = [PartyCtlMem]::ReadInt($h, 0x7A39FC)
    $server = [PartyCtlMem]::ReadInt($h, [long]$app + 8)
    $internal = [PartyCtlMem]::ReadInt($h, [long]$server + 4)
    $field = [long]$internal + 0x1B770 + 0xF0
    $now = [PartyCtlMem]::ReadInt($h, $field)
    "pid $ProcId partyTable+0xF0 @0x$($field.ToString('X')) = $now"
    if ($null -ne $Set) {
        $bytes = [BitConverter]::GetBytes([int]$Set)
        $w = 0
        if (-not [PartyCtlMem]::WriteProcessMemory($h, [IntPtr]$field, $bytes, 4, [ref]$w)) { throw "write failed" }
        "set to $Set (was $now); read back " + [PartyCtlMem]::ReadInt($h, $field)
    }
}
finally { [void][PartyCtlMem]::CloseHandle($h) }
