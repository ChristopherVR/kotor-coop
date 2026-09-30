# Read-only native combat acceptance snapshot. No write access or engine calls.
# Native HP/round offsets: Odyssey.Core/Combat and research/death-handling.md.
param([Parameter(Mandatory)][int]$ProcId, [uint32[]]$EnemyIds = @(), [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
if (-not ('PeerCombatReader' -as [type])) {
Add-Type @'

using System; using System.Runtime.InteropServices;
public class PeerCombatReader {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI {
    public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect, __a1;
    public UIntPtr RegionSize; public uint State, Protect, Type, __a2; }
  public static uint Epoch(string module) { unchecked { uint hash=2166136261; foreach(char c in module) { hash ^= char.ToLowerInvariant(c); hash *=16777619; } return hash==0 ? 1 : hash; } }
  public static IntPtr Open(int pid){ return OpenProcess(0x0410, false, (uint)pid); }
  public static byte[] Read(IntPtr h, long a, int n){
    byte[] b = new byte[n]; int r;
    if(!ReadProcessMemory(h, new IntPtr(a), b, n, out r) || r != n) return null;
    return b; }
  public static long FindBlock(IntPtr h, byte[] sig){
    long addr = 0; int sz = Marshal.SizeOf(typeof(MBI));
    while(addr < 0x7FFFFFFF){
      MBI m; if(VirtualQueryEx(h, new IntPtr(addr), out m, sz) == 0) break;
      long size = (long)m.RegionSize; if(size <= 0) break;
      bool ok = m.State == 0x1000 && m.Type == 0x20000 && (m.Protect & 0x101) == 0
             && (m.Protect & (0x02|0x04|0x08|0x20|0x40|0x80)) != 0;
      if(ok && size <= 64L*1024*1024){
        byte[] buf = Read(h, (long)m.BaseAddress, (int)size);
        if(buf != null)
          for(int i = 0; i + sig.Length <= buf.Length; i += 4){
            bool hit = true;
            for(int j = 0; j < sig.Length; j++){
              if(j >= 8 && j < 12) continue;
              if(buf[i+j] != sig[j]) { hit = false; break; }
            }
            if(hit) return (long)m.BaseAddress + i;
          }
      }
      long next = (long)m.BaseAddress + size; if(next <= addr) break; addr = next;
    }
    return 0; }
}
'@
}
$process = Get-Process -Id $ProcId
$handle = [PeerCombatReader]::Open($ProcId)
if ($handle -eq [IntPtr]::Zero) { throw 'Cannot open target for read access.' }
function Read-Bytes([long]$address,[int]$count) {
    $bytes = [PeerCombatReader]::Read($handle,$address,$count)
    if ($null -eq $bytes) { throw ('Unreadable native address 0x{0:X}' -f $address) }
    return ,$bytes
}
function Word([long]$address) { [BitConverter]::ToUInt32((Read-Bytes $address 4),0) }
function Value([int]$offset) { [BitConverter]::ToUInt32($snapshot,$offset) }
function Find-Object([uint32]$id) {
    if (!$id -or $id -eq 0x7F000000) { return 0L }
    $bucket = ((($id -shr 31) -band 1) -shl 12) -bor ($id -band 0xFFF)
    $entry = Word ($tree + $bucket * 4)
    for ($walk=0; $entry -and $walk -lt 64; $walk++) {
        if ((Word $entry) -eq ($id -band 0x7FFFFFFF)) { return [long](Word ($entry + 4)) }
        $entry = Word ($entry + 8)
    }
    return 0L
}
# EngineGlobals.TryReadModuleName: server mode is a WORD, not a DWORD.
function Native-Module([long]$internal) {
    if ((Word ($internal+0x100B4)) -ne 0 -or [BitConverter]::ToUInt16((Read-Bytes ($internal+0x10008) 2),0) -ne 2) { throw 'Native module is loading or not running.' }
    $info=Word ($internal+0x10004)
    if (!$info) { throw 'Native server info unavailable.' }
    $text=Word ($info+0xFC); $length=Word ($info+0x100)
    if (!$text -or !$length -or $length -gt 32) { throw 'Invalid native module string.' }
    [Text.Encoding]::ASCII.GetString((Read-Bytes $text $length)).TrimEnd([char]0)
}
function Body([uint32]$id) {
    $address = Find-Object $id
    if (!$address) { return @{ Id=$id; Resolved=$false } }
    $bytes = Read-Bytes $address 0xA90
    if ([BitConverter]::ToUInt32($bytes,0) -ne 0x007470C8 -or [BitConverter]::ToUInt32($bytes,4) -ne $id) {
        return @{ Id=$id; Resolved=$false; Reason='Current object is not the selected creature' }
    }
    $round = [BitConverter]::ToUInt32($bytes,0x9C8)
    $target = if ($round) { Word ($round + 0x9CC) } else { 0 }
    if ((Find-Object $id) -ne $address) { throw 'Object changed during snapshot; retry.' }
    return @{ Id=$id; Resolved=$true; Address=$address; BaseHp=[BitConverter]::ToInt16($bytes,0xDC);
        TemporaryHp=[BitConverter]::ToInt32($bytes,0xE4); HpDowned=([BitConverter]::ToInt16($bytes,0xDC) -le 0);
        NativeTarget=$target; Round=$round; MoveState=[BitConverter]::ToInt32($bytes,0xA8C);
        Commandable=[BitConverter]::ToUInt32($bytes,0xE8); Plot=[BitConverter]::ToUInt32($bytes,0xF8) }
}
try {
    $signature = [byte[]](0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31,0,0,0,0,0x01,0xEE,0xFF,0xC0)
    $block = [PeerCombatReader]::FindBlock($handle,$signature)
    if (!$block) { throw 'Shared block not found.' }
    if ((Word ($block + 8)) -lt 83) { throw 'Attributed damage requires shared version >=83.' }
    $result = $null
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        try {
            $serial = Word ($block + 0x396C); $sequence = Word ($block + 0x8700)
            if ($sequence -band 1) { continue }
            $snapshot = Read-Bytes $block $(if ((Word ($block+8)) -ge 86) { 0xBC00 } elseif ((Word ($block+8)) -ge 85) { 0xBA00 } else { 0xB800 })
            if ((Value 0x8700) -ne $sequence -or (Word ($block + 0x8700)) -ne $sequence) { continue }
            if ((Value 0x870C) -ne $serial) { throw 'Attributed bank belongs to another world load.' }
            $manager = Word 0x7A39FC
            if (!$manager) { throw 'Native app manager unavailable.' }
            $server = Word ($manager + 8)
            if (!$server) { throw 'Native server unavailable during menu/load.' }
            $internal = Word ($server + 4)
            if (!$internal) { throw 'Native server internal unavailable during load.' }
            $array = Word ($internal + 0x1005C)
            if (!$array) { throw 'Current server object array unavailable.' }
            $tree = Word $array
            $nativeModule=Native-Module $internal
            $nativeEpoch=[PeerCombatReader]::Epoch($nativeModule)
            if ((Value 0x8704) -ne $nativeEpoch) { throw 'Attributed epoch does not match the current native module namespace.' }
            if (!$tree) { throw 'Current server object array unavailable.' }
            $appliedBefore=Word ($block+0x2014)
            $rows = @(); $peers = @(); $threat = @()
            $count = Value 0x8708
            if ($count -gt 1024) { throw 'Invalid attributed bank count.' }
            for ($i=0; $i -lt $count; $i++) {
                $offset = 0x8710 + $i * 12
                $rows += @{ Peer=Value $offset; Enemy=Value ($offset+4); AcceptedCumulative=Value ($offset+8) }
            }
            for ($slot=0; $slot -lt 8; $slot++) {
                $offset=0x50 + $slot * 0x40
                if (Value $offset) {
                    $phantom = Value (0x1F80 + $slot*4)
                    $peers += @{ Slot=$slot; Peer=Value ($offset+4); Phantom=$phantom; Native=(Body $phantom) }
                }
            }
            for ($i=0; $i -lt [Math]::Min((Value 0x4B80),6); $i++) {
                $offset=0x4B88 + $i * 20
                $threat += @{ Enemy=Value $offset; DiagnosticTarget=Value ($offset+4); Top=Value ($offset+8);
                    TopThreat=(Value ($offset+12))/100.0; CurrentThreat=(Value ($offset+16))/100.0 }
            }
            $ids = if ($EnemyIds.Count) { $EnemyIds } else { @($rows.Enemy + $threat.Enemy | Sort-Object -Unique | Select-Object -First 16) }
            $enemies = @($ids | ForEach-Object { Body $_ })
            $localPlayer=Body (Value 0x2264)
            if ((Word ($block+0x2014)) -ne $appliedBefore) { continue }
            if ((Native-Module $internal) -ne $nativeModule) { continue }
            if ((Word ($internal + 0x1005C)) -ne $array -or (Word $array) -ne $tree -or (Word ($block+0x396C)) -ne $serial) { continue }
            if ((Word ($block+0x8700)) -ne $sequence -or (Word ($block+0x4B84)) -ne (Value 0x4B84)) { continue }
            $result = @{ Utc=[DateTime]::UtcNow.ToString('o'); Pid=$ProcId; ProcessStartTicks=$process.StartTime.ToUniversalTime().Ticks;
                Version=Value 8; WorldLoadSerial=$serial; AttributedWorldLoadSerial=Value 0x870C;
                WorldLoadSerialNote='Unannounced teardown counter; zero is valid for the initial world and ordinary warps do not increment it';
                NativeNamespace=@{ Module=$nativeModule; ModuleEpoch=$nativeEpoch; ObjectArray=$array; ObjectTree=$tree };
                ThreatGeneration=Value 0x4B84; NativeDamageIncarnation=$(if ((Value 8) -ge 86) { Value 0xB980 } else { 0 }); AttributedSequence=$sequence; Epoch=Value 0x8704;
                LocalPlayer=$localPlayer; DamageDealtEnabled=Value 0x2118;
                AppliedEffects=$appliedBefore; AppliedEffectsScope='Global successful engine calls, not damage amount';
                PerPeerAppliedDebt=$null; DebtNote='Private managed ledger is not exported; compare HP/tempHP and application-counter deltas';
                Peers=$peers; Accepted=$rows; Enemies=$enemies; Threat=$threat;
                ThreatNote='Diagnostic rows; use Enemies.NativeTarget as actual native target oracle' }
            break
        } catch { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 20 }
    }
    if (!$result) { throw 'Publication kept changing; retry probe.' }
    $json = $result | ConvertTo-Json -Depth 8
    if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$json) }
    $json
} finally { [PeerCombatReader]::CloseHandle($handle) > $null }
