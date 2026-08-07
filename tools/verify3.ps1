# The THREE-PLAYER battery. Everything here is a check that two players could not fail.
#
# Assumes tools\demo3.ps1 has all three instances in-world with all three agents connected.
# Reads real engine and shared-block state, never the agent's own bookkeeping — same rule as
# tools\verify.ps1, and for the same reason: an agent that believes something is not evidence.
#
#   pwsh tools\verify3.ps1
#   pwsh tools\verify3.ps1 -Pids 1234,5678,9012
#
# Exit code is the number of failed checks, so 0 means everything passed.
param(
    [int[]]$Pids = @(),
    [string[]]$GameDirs = @(
        'C:\GOG Games\Star Wars KOTOR (GOG)',
        'D:\Games\swkotor-gog-p2',
        'D:\Games\swkotor-gog-p3'),
    [datetime]$Since = (Get-Date).AddMinutes(-30)
)

$ErrorActionPreference = 'Stop'

if ($Pids.Count -eq 0) {
    $found = @(Get-Process swkotor -ErrorAction SilentlyContinue | Sort-Object StartTime)
    if ($found.Count -lt 3) {
        throw "expected 3 swkotor processes, found $($found.Count); pass -Pids explicitly"
    }

    $Pids = @($found[0].Id, $found[1].Id, $found[2].Id)
}

Add-Type @'
using System; using System.Runtime.InteropServices;
public class V3 {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect,__a1; public UIntPtr RegionSize; public uint State,Protect,Type,__a2; }
  public static byte[] R(IntPtr h, long a, int n){ byte[] b=new byte[n]; int r; if(!ReadProcessMemory(h,new IntPtr(a),b,n,out r)||r!=n) return null; return b; }
  public static uint U32(IntPtr h, long a){ var b=R(h,a,4); return b==null?0:BitConverter.ToUInt32(b,0); }
  public static string Str(IntPtr h, long a, int n){ var b=R(h,a,n); if(b==null) return ""; int e=Array.IndexOf(b,(byte)0); if(e<0)e=n; return System.Text.Encoding.ASCII.GetString(b,0,e); }
  public static long FindBlock(IntPtr h){
    byte[] sig = { 0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31, 0,0,0,0, 0x01,0xEE,0xFF,0xC0 };
    long addr=0; int sz=Marshal.SizeOf(typeof(MBI));
    while(addr<0x7FFFFFFF){ MBI m; if(VirtualQueryEx(h,new IntPtr(addr),out m,sz)==0) break;
      long size=(long)m.RegionSize; if(size<=0) break;
      if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
        var buf=R(h,(long)m.BaseAddress,(int)size);
        if(buf!=null) for(int i=0;i+16<=buf.Length;i+=4){ bool hit=true;
          for(int j=0;j<16;j++){ if(j>=8&&j<12) continue; if(buf[i+j]!=sig[j]){hit=false;break;} }
          if(hit) return (long)m.BaseAddress+i; } }
      long next=(long)m.BaseAddress+size; if(next<=addr) break; addr=next; }
    return 0; }
}
'@

# SharedBlock offsets. Kept in one place so a layout change breaks loudly here rather than
# quietly producing plausible numbers.
$OffRemoteCount = 0x48
$OffSlots = 0x50
$SlotStride = 0x40
$OffCommand = 0x254
$OffPhantomCount = 0x25C
$OffPhantomIdLegacy = 0x264      # slot 0 mirror ONLY - see SharedBlock.OffsetPhantomIds
$OffKeepAlive = 0x268
$OffPausesCleared = 0x26C
# ⚠ 0x1F80, not 0x1DE0. The table was RELOCATED when the three-peer layout landed, and
# 0x1DE0 is now OffsetEncounterWatch. Reading the old address made twelve checks fail against
# a live, correct session: the "phantom id" came back 0 on the host and 1 on both clients,
# which is precisely what the encounter watch flag holds (1 only on a peer that does not own
# enemies). A stale offset does not read as garbage — it reads as a plausible wrong answer.
$OffPhantomIdTable = 0x1F80      # the real 8-entry table (SharedBlock.OffsetPhantomIdTable)
$OffPhantomHitSlots = 0x1CD0
$PhantomHitStride = 0x10
$SlotCapacity = 8

$results = [ordered]@{}
function Check($name, $ok, $detail) {
    $results[$name] = [pscustomobject]@{ Pass = $ok; Detail = $detail }
    "{0,-40} {1}  {2}" -f $name, $(if ($ok) { 'PASS' } else { 'FAIL' }), $detail
}

$handles = @()
$blocks = @()
foreach ($p in $Pids) {
    $h = [V3]::OpenProcess(0x0438, $false, $p)
    if ($h -eq [IntPtr]::Zero) { throw "cannot open pid $p" }
    $handles += $h
    $b = [V3]::FindBlock($h)
    if ($b -eq 0) { throw "no KOCOOP block in pid $p (is the plugin installed there?)" }
    $blocks += $b
    "pid {0,-6} block 0x{1:X}  version {2}" -f $p, $b, [V3]::U32($h, $b + 8)
}
''

# --- 1. three live instances, three live plugins -------------------------------------
Check 'three instances alive' ($Pids.Count -eq 3) ("pids: " + ($Pids -join ', '))

$beats1 = @(0..2 | ForEach-Object { [V3]::U32($handles[$_], $blocks[$_] + 0x4C) })
Start-Sleep -Milliseconds 600
$beats2 = @(0..2 | ForEach-Object { [V3]::U32($handles[$_], $blocks[$_] + 0x4C) })
$beating = @(0..2 | Where-Object { $beats2[$_] -ne $beats1[$_] }).Count
Check 'all three plugins are ticking' ($beating -eq 3) "$beating/3 heartbeats advanced"

# --- 2. ONE agent per game process ---------------------------------------------------
#
# ⚠ The check that would have caught the stray third peer. The plugin auto-starts an agent
# (coop.ini auto_start_agent) AND the harness starts one, so without the per-pid attach mutex
# a game can carry two agents, both writing the same block and both joining the session as
# separate players. demo3.ps1 turns auto-start off; this proves it took.
$agents = @(Get-Process KotorCoop.Agent -ErrorAction SilentlyContinue)
Check 'exactly three agents running' ($agents.Count -eq 3) "$($agents.Count) KotorCoop.Agent processes"

# --- 3. every instance sees the other TWO --------------------------------------------
for ($i = 0; $i -lt 3; $i++) {
    $h = $handles[$i]; $b = $blocks[$i]
    $range = [V3]::U32($h, $b + $OffRemoteCount)
    $active = 0
    $peerIds = @()
    $names = @()
    for ($s = 0; $s -lt $SlotCapacity; $s++) {
        $slot = $b + $OffSlots + ($s * $SlotStride)
        if ([V3]::U32($h, $slot) -ne 0) {
            $active++
            $peerIds += [V3]::U32($h, $slot + 4)
            $names += [V3]::Str($h, $slot + 0x20, 24)
        }
    }

    Check "instance $($i + 1): two remote peers" ($active -eq 2) `
        "range=$range active=$active peers=[$($peerIds -join ',')] names=[$($names -join ',')]"

    # Distinct, non-zero peer ids. Two players cannot show an aliasing bug here; three can.
    $distinct = @($peerIds | Where-Object { $_ -ne 0 } | Sort-Object -Unique).Count
    Check "instance $($i + 1): peer ids distinct" ($distinct -eq $active -and $active -gt 0) `
        "$distinct distinct of $active"
}

# --- 4. two phantom bodies per instance, in the RELOCATED id table --------------------
for ($i = 0; $i -lt 3; $i++) {
    $h = $handles[$i]; $b = $blocks[$i]
    $count = [V3]::U32($h, $b + $OffPhantomCount)
    $ids = @()
    for ($s = 0; $s -lt $SlotCapacity; $s++) {
        $id = [V3]::U32($h, $b + $OffPhantomIdTable + ($s * 4))
        if ($id -ne 0) { $ids += ('0x{0:X}' -f $id) }
    }

    Check "instance $($i + 1): two phantoms alive" ($count -eq 2 -and $ids.Count -eq 2) `
        "count=$count ids=[$($ids -join ',')]"
}

# --- 5. THE OVERLAP REGRESSION -------------------------------------------------------
#
# The old phantom-id "array" at 0x264 had room for exactly one entry: 0x268 is the agent's
# keep-alive flag and 0x26C is the plugin's pause counter. Writing the SECOND phantom's
# object id — which only happens with a third player — put a creature id on top of both.
# These two assertions are the whole bug, stated as data.
for ($i = 0; $i -lt 3; $i++) {
    $h = $handles[$i]; $b = $blocks[$i]
    $keepAlive = [V3]::U32($h, $b + $OffKeepAlive)
    $legacy = [V3]::U32($h, $b + $OffPhantomIdLegacy)
    $table1 = [V3]::U32($h, $b + $OffPhantomIdTable + 4)

    # Keep-alive is a boolean the agent writes; an object id is a large number.
    Check "instance $($i + 1): keep-alive not clobbered" ($keepAlive -le 1) `
        ("0x268 = 0x{0:X} (an object id here means the id table overlaps again)" -f $keepAlive)

    Check "instance $($i + 1): slot-0 mirror agrees" `
        ($legacy -eq [V3]::U32($h, $b + $OffPhantomIdTable)) `
        ("0x264=0x{0:X} table[0]=0x{1:X}" -f $legacy, [V3]::U32($h, $b + $OffPhantomIdTable))

    Check "instance $($i + 1): second phantom has an id" ($table1 -ne 0) `
        ("table[1] = 0x{0:X}" -f $table1)
}

# --- 6. the hit table lines up with the bodies ---------------------------------------
#
# Slot i's damage total must be recorded against slot i's body. The agent bills a peer for
# whatever it reads here, so a mismatch is "the wrong player takes the damage" — the failure
# that is unreachable with one remote peer and routine with two.
for ($i = 0; $i -lt 3; $i++) {
    $h = $handles[$i]; $b = $blocks[$i]
    $bad = @()
    for ($s = 0; $s -lt $SlotCapacity; $s++) {
        $body = [V3]::U32($h, $b + $OffPhantomIdTable + ($s * 4))
        $watched = [V3]::U32($h, $b + $OffPhantomHitSlots + ($s * $PhantomHitStride))
        if ($body -ne $watched) { $bad += ('slot {0}: body 0x{1:X} watched 0x{2:X}' -f $s, $body, $watched) }
    }

    Check "instance $($i + 1): hit table matches bodies" ($bad.Count -eq 0) `
        $(if ($bad.Count -eq 0) { 'all 8 slots agree' } else { $bad -join '; ' })
}

# --- 7. the crash probe is the new one, and nothing died -----------------------------
for ($i = 0; $i -lt 3; $i++) {
    $log = Join-Path $GameDirs[$i] 'kotorcoop-plugin.log'
    $lines = if (Test-Path $log) { @(Get-Content $log) } else { @() }

    Check "instance $($i + 1): probe installed" `
        ($null -ne ($lines | Select-String 'unhandled-exception hook installed' | Select-Object -First 1)) `
        $log

    # ⚠ THIS PROCESS'S SECTION OF THE LOG ONLY. The plugin appends across runs, so scanning
    # the whole file reported a crash from hours earlier as a fault in a healthy session —
    # the same "stale evidence" trap verify.ps1 documents for its own log checks. Each launch
    # writes "crash probe installed", so the last one marks where this process began.
    $probeAt = ($lines | Select-String 'crash probe installed' | Select-Object -Last 1).LineNumber
    if ($probeAt) { $lines = $lines[($probeAt - 1)..($lines.Count - 1)] }

    # ⚠ 0x406D1388 is MS_VC_EXCEPTION — the documented "name this thread" exception every
    # MSVC-built process raises for the debugger. It is not a fault, the game raises it on its
    # own without the plugin, and counting it failed this check on three healthy instances.
    $fatal = @($lines | Select-String 'FATAL|FIRST-CHANCE' |
        Where-Object { $_ -notmatch '0x406D1388' })
    Check "instance $($i + 1): no faults logged" ($fatal.Count -eq 0) `
        $(if ($fatal.Count -eq 0) { 'clean' } else { ($fatal | Select-Object -Last 2 | ForEach-Object { $_.Line }) -join ' | ' })
}

# A fail-fast leaves NOTHING in the plugin log, so the Application log is not optional.
$crashes = @(
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $Since } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like '*swkotor*' })
Check 'no swkotor crash in the event log' ($crashes.Count -eq 0) `
    $(if ($crashes.Count -eq 0) { "since $($Since.ToString('HH:mm'))" } else { ($crashes | Select-Object -First 1).TimeCreated })

# --- 8. all three are still driving --------------------------------------------------
for ($i = 0; $i -lt 3; $i++) {
    $cmd = [V3]::U32($handles[$i], $blocks[$i] + $OffCommand)
    Check "instance $($i + 1): plugin driving" ($cmd -eq 1) "command=$cmd (1 = spawn and drive)"
}

''
'summary: {0} passed, {1} failed' -f ($results.Values | Where-Object Pass).Count, ($results.Values | Where-Object { -not $_.Pass }).Count
foreach ($h in $handles) { [V3]::CloseHandle($h) | Out-Null }
exit ($results.Values | Where-Object { -not $_.Pass }).Count
