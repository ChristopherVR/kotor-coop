# Measures a SHARED conversation end to end, against a live two-instance session.
#
#   pwsh tools\dlgprobe.ps1 [-Tag end_trask] [-Seconds 45]
#
# The question this exists to answer is not "did a conversation open" — the plugin log has
# always said that — but "did ONE conversation run, and how long did the follower's copy take
# to deliver a line". Both were guessed at before: the follower's content watchdog was set to
# a number nobody had measured, and a conversation that opened and closed repeatedly looked
# identical, from the outside, to one that stayed open.
#
# So it samples the two dialogue serials over time. A serial that climbs steadily is churn —
# each open/close pair bumps it twice. A serial that settles is one conversation holding.
#
# -NoJoin samples the OWNER side alone. By default the client is made to click the same NPC a
# few seconds later, which is the click-to-join path — without it the client never enters the
# conversation at all and the node columns can only ever show one side.
param(
    [string]$Tag = 'end_trask',
    [int]$Seconds = 45,
    [switch]$NoJoin,
    # Run it the other way round: the CLIENT starts the conversation and the host joins. This
    # is the direction the interaction lock exists for — the host OWNS the NPC, so it is the
    # host's AI that keeps walking it around while a client talks to it.
    [switch]$FromClient,
    [int]$JoinAfter = 4,
    [int]$PickAfter = 10,
    [string]$PickKey = '1',
    [string]$Log1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-plugin.log',
    [string]$Log2 = 'D:\Games\swkotor-gog-p2\kotorcoop-plugin.log'
)

$ErrorActionPreference = 'Stop'

$found = @(Get-Process swkotor -ErrorAction SilentlyContinue | Sort-Object StartTime)
if ($found.Count -lt 2) { throw "expected 2 swkotor processes, found $($found.Count)" }

Add-Type @'
using System; using System.Runtime.InteropServices;
public class DP {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect,__a1; public UIntPtr RegionSize; public uint State,Protect,Type,__a2; }
  public static byte[] R(IntPtr h, long a, int n){ byte[] b=new byte[n]; int r; if(!ReadProcessMemory(h,new IntPtr(a),b,n,out r)||r!=n) return null; return b; }
  public static uint U32(IntPtr h, long a){ var b=R(h,a,4); return b==null?0:BitConverter.ToUInt32(b,0); }
  public static bool W32(IntPtr h, long a, int v){ int w; return WriteProcessMemory(h,new IntPtr(a),BitConverter.GetBytes(v),4,out w); }
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

$h1 = [DP]::OpenProcess(0x1F0FFF, $false, $found[0].Id)
$h2 = [DP]::OpenProcess(0x1F0FFF, $false, $found[1].Id)
$b1 = [DP]::FindBlock($h1)
$b2 = [DP]::FindBlock($h2)
if ($b1 -eq 0 -or $b2 -eq 0) { throw "plugin shared block not found (host $b1, client $b2)" }

# Block fields (SharedBlock.cs).
$OffLocalSerial   = 0xCE8
$OffLocalPartner  = 0x1200
$OffRemoteActive  = 0x1228
$OffRemotePartner = 0x1240
$OffTestTag       = 0x1D70   # test hook: tag to open a conversation with
$OffTestSerial    = 0x1D90   # ... bumped to fire it

# Node sync (version 32). THE column to watch: the index of the dialogue entry each instance
# is showing. The serials above only say "something changed"; these say whether the two
# screens are on the SAME LINE, which is the actual claim the feature makes. -1 = unknown.
$OffLocalNode     = 0x1320
$OffRemoteNode    = 0x1328

# Interaction locks (version 33). "lock" is how many objects a PEER holds here — the agent
# filters this instance's own claim out — so a nonzero value on the OWNER's side is the whole
# channel working: somebody else has asked it to hold one of its objects still.
$OffLockCount     = 0x1358

function AsInt32($u) { [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]$u), 0) }

function LineCount($p) { if (Test-Path $p) { @(Get-Content $p).Count } else { 0 } }
function NewLines($p, $since) {
    if (-not (Test-Path $p)) { return @() }
    $all = @(Get-Content $p)
    if ($all.Count -le $since) { return @() }
    return $all[$since..($all.Count - 1)]
}

$mark1 = LineCount $Log1
$mark2 = LineCount $Log2

"host pid $($found[0].Id) block 0x{0:X}   client pid $($found[1].Id) block 0x{1:X}" -f $b1, $b2
"opening a conversation with '$Tag' on the {0} ..." -f $(if ($FromClient) { 'CLIENT' } else { 'HOST' })

$tagBytes = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes($Tag), $tagBytes, $Tag.Length)

function FireTestOpen($h, $b) {
    for ($i = 0; $i -lt 32; $i += 4) {
        [DP]::W32($h, $b + $OffTestTag + $i, [BitConverter]::ToInt32($tagBytes, $i)) | Out-Null
    }
    [DP]::W32($h, $b + $OffTestSerial, [int]([DP]::U32($h, $b + $OffTestSerial) + 1)) | Out-Null
}

$startH = if ($FromClient) { $h2 } else { $h1 }
$startB = if ($FromClient) { $b2 } else { $b1 }
$joinH  = if ($FromClient) { $h1 } else { $h2 }
$joinB  = if ($FromClient) { $b1 } else { $b2 }

FireTestOpen $startH $startB

''
'  t     host#  client#   remoteActive  hostNode  clientNode  wantNode  hLock  cLock   hostPartner   clientPartner'
$samples = @()
for ($t = 0; $t -lt $Seconds; $t++) {
    Start-Sleep -Seconds 1
    $s1 = [DP]::U32($h1, $b1 + $OffLocalSerial)
    $s2 = [DP]::U32($h2, $b2 + $OffLocalSerial)
    # From the JOINER's side: it is the follower, whichever instance it happens to be.
    $ra = [DP]::U32($joinH, $joinB + $OffRemoteActive)
    $p1 = [DP]::Str($h1, $b1 + $OffLocalPartner, 32)
    $p2 = [DP]::Str($h2, $b2 + $OffLocalPartner, 32)

    # Signed: -1 is a real value here ("no conversation, or the index could not be trusted"),
    # and reading it as 4294967295 would hide exactly the case worth seeing. Reinterpret the
    # bits rather than casting — [int] on 0xFFFFFFFF throws rather than wrapping.
    $n1 = AsInt32 ([DP]::U32($h1, $b1 + $OffLocalNode))
    $n2 = AsInt32 ([DP]::U32($h2, $b2 + $OffLocalNode))
    $nw = AsInt32 ([DP]::U32($joinH, $joinB + $OffRemoteNode))

    $l1 = [DP]::U32($h1, $b1 + $OffLockCount)
    $l2 = [DP]::U32($h2, $b2 + $OffLockCount)

    $samples += [pscustomobject]@{ T = $t; H = $s1; C = $s2; HN = $n1; CN = $n2; W = $nw; HL = $l1; CL = $l2 }
    '{0,3}   {1,6}  {2,7}   {3,12}  {4,8}  {5,10}  {6,8}  {7,5}  {8,5}   {9,-12}  {10}' -f `
        $t, $s1, $s2, $ra, $n1, $n2, $nw, $l1, $l2, $p1, $p2

    # The client's click on the same NPC. Deliberately AFTER the host's conversation is up,
    # so the gate is asked the real question — "is this a join or a competitor?" — rather
    # than the easy one it gets when nobody holds the token yet.
    if (-not $NoJoin -and $t -eq $JoinAfter) {
        "  -> {0} clicks '{1}' (join)" -f $(if ($FromClient) { 'host' } else { 'client' }), $Tag
        FireTestOpen $joinH $joinB
    }

    # The owner ADVANCES the conversation. Holding the same node is only half the claim: the
    # question that matters is whether the follower moves when the owner does, and no amount
    # of watching a conversation sit still can answer it.
    if ($PickAfter -gt 0 -and $t -eq $PickAfter) {
        "  -> host answers (key '$PickKey')"
        & (Join-Path $PSScriptRoot 'sendkey.ps1') -Keys $PickKey -ProcId $found[0].Id | Out-Null
    }
}

''
'--- new plugin log lines, HOST ---'
NewLines $Log1 $mark1 | Where-Object { $_ -match 'dialogue|conversation' }
''
'--- new plugin log lines, CLIENT ---'
NewLines $Log2 $mark2 | Where-Object { $_ -match 'dialogue|conversation' }

''
# The verdict. Over a held conversation the serials should move a handful of times (real
# line changes) and then settle — NOT climb once every second or two, which is the signature
# of a conversation being reopened.
$hostDelta   = $samples[-1].H - $samples[0].H
$clientDelta = $samples[-1].C - $samples[0].C
"serial movement over $Seconds s: host +$hostDelta, client +$clientDelta"
if ($hostDelta -gt ($Seconds / 3) -or $clientDelta -gt ($Seconds / 3)) {
    'VERDICT: still churning — the serials climb faster than lines could plausibly change.'
} else {
    'VERDICT: serials settled — consistent with ONE conversation holding open.'
}

# The node verdict, which is the one that says whether the two screens agree. Counted only
# over samples where BOTH instances had a conversation with a known node: a follower that has
# not opened yet is not a disagreement, it is a follower that has not opened yet.
$paired = @($samples | Where-Object { $_.HN -ge 0 -and $_.CN -ge 0 })
if ($paired.Count -eq 0) {
    'NODES: never both in a conversation with a known node — nothing to compare.'
} else {
    $together = @($paired | Where-Object { $_.HN -eq $_.CN }).Count
    "NODES: on the same entry for $together of $($paired.Count) paired samples."
    if ($together -eq $paired.Count) {
        'VERDICT: the two conversations held the same node throughout.'
    } else {
        $apart = $paired | Where-Object { $_.HN -ne $_.CN } | Select-Object -First 5
        'VERDICT: they diverged. First disagreements (t, host, client):'
        $apart | ForEach-Object { '  t={0,-3} host={1,-5} client={2}' -f $_.T, $_.HN, $_.CN }
    }
}

[DP]::CloseHandle($h1) | Out-Null
[DP]::CloseHandle($h2) | Out-Null
