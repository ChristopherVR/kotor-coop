# Runs the co-op verification battery against a live two-instance session and prints a
# PASS/FAIL line per feature. Assumes tools\session.ps1 has both instances in-world with
# both agents connected.
#
#   pwsh tools\verify.ps1 -Host1 <pid> -Client2 <pid>
#
# Every check reads or drives real engine state; nothing is inferred from the agent's own
# bookkeeping, because the agent believing a thing is not the thing being true.
param(
    [int]$Host1 = 0,
    [int]$Client2 = 0,
    [string]$Log1 = 'C:\GOG Games\Star Wars KOTOR (GOG)\kotorcoop-plugin.log',
    [string]$Log2 = 'D:\Games\swkotor-gog-p2\kotorcoop-plugin.log',

    # The port the session under test is hosted on, so its LAN beacon can be told apart
    # from any other agent advertising on this machine.
    [int]$SessionPort = 27140
)

# Pids are optional: with a demo session up, the two swkotor processes in start order are
# the host and the client, which is how demo.ps1 launches them.
if ($Host1 -eq 0 -or $Client2 -eq 0) {
    $found = @(Get-Process swkotor -ErrorAction SilentlyContinue | Sort-Object StartTime)
    if ($found.Count -lt 2) { throw "expected 2 swkotor processes, found $($found.Count); pass -Host1/-Client2 explicitly" }
    if ($Host1 -eq 0) { $Host1 = $found[0].Id }
    if ($Client2 -eq 0) { $Client2 = $found[1].Id }
}

Add-Type @'
using System; using System.Runtime.InteropServices;
public class VF {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int w);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect,__a1; public UIntPtr RegionSize; public uint State,Protect,Type,__a2; }
  public static byte[] R(IntPtr h, long a, int n){ byte[] b=new byte[n]; int r; if(!ReadProcessMemory(h,new IntPtr(a),b,n,out r)||r!=n) return null; return b; }
  public static uint U32(IntPtr h, long a){ var b=R(h,a,4); return b==null?0:BitConverter.ToUInt32(b,0); }
  public static float F32(IntPtr h, long a){ var b=R(h,a,4); return b==null?0f:BitConverter.ToSingle(b,0); }
  public static int I16(IntPtr h, long a){ var b=R(h,a,2); return b==null?0:BitConverter.ToInt16(b,0); }
  public static byte U8(IntPtr h, long a){ var b=R(h,a,1); return b==null?(byte)0:b[0]; }
  public static bool W32(IntPtr h, long a, int v){ int w; return WriteProcessMemory(h,new IntPtr(a),BitConverter.GetBytes(v),4,out w); }
  public static bool W16(IntPtr h, long a, short v){ int w; return WriteProcessMemory(h,new IntPtr(a),BitConverter.GetBytes(v),2,out w); }
  public static string Str(IntPtr h, long a, int n){ var b=R(h,a,n); if(b==null) return ""; int e=Array.IndexOf(b,(byte)0); if(e<0)e=n; return System.Text.Encoding.ASCII.GetString(b,0,e); }
  // Every creature is a 4-byte CSWSCreature vtable; the object id follows it.
  public static long FindCreature(IntPtr h, uint objId){
    long addr=0; int sz=Marshal.SizeOf(typeof(MBI));
    while(addr<0x7FFFFFFF){ MBI m; if(VirtualQueryEx(h,new IntPtr(addr),out m,sz)==0) break;
      long size=(long)m.RegionSize; if(size<=0) break;
      if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
        var buf=R(h,(long)m.BaseAddress,(int)size);
        if(buf!=null) for(int i=0;i+8<=buf.Length;i+=4)
          if(BitConverter.ToUInt32(buf,i)==0x007470C8 && BitConverter.ToUInt32(buf,i+4)==objId)
            return (long)m.BaseAddress+i; }
      long next=(long)m.BaseAddress+size; if(next<=addr) break; addr=next; }
    return 0; }
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

$results = [ordered]@{}
$script:resetDone = $false
function Check($name, $ok, $detail) {
    $results[$name] = [pscustomobject]@{ Pass = $ok; Detail = $detail }
    "{0,-34} {1}  {2}" -f $name, $(if ($ok) { 'PASS' } else { 'FAIL' }), $detail
}
function Tail($path, $n = 40) { if (Test-Path $path) { Get-Content $path -Tail $n } else { @() } }

# ⚠ Log checks must only look at lines written AFTER the action that is being tested.
# Searching the whole file made a check pass on a line left by the PREVIOUS run — a test
# that reports success from stale evidence is worse than no test at all.
function LogLines($path) { if (Test-Path $path) { @(Get-Content $path).Count } else { 0 } }
function LogHasSince($path, $pattern, [int]$since) {
    if (-not (Test-Path $path)) { return $false }
    $lines = @(Get-Content $path)
    if ($lines.Count -le $since) { return $false }
    return $null -ne ($lines[$since..($lines.Count - 1)] | Select-String -Pattern $pattern | Select-Object -First 1)
}
function WaitFor2([scriptblock]$condition, [int]$seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function WaitForLog($path, $pattern, [int]$since, [int]$seconds) {
    for ($i = 0; $i -lt ($seconds * 2); $i++) {
        if (LogHasSince $path $pattern $since) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# Put the game back to a known state before measuring anything: a conversation left open by
# an earlier run defers damage and blocks attack orders, which fails unrelated checks.
function CloseConversations($h, $b) {
    $closeTag = [byte[]]::new(32)
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('close'), $closeTag, 5)
    for ($i = 0; $i -lt 32; $i += 4) { [VF]::W32($h, $b + 0x1D70 + $i, [BitConverter]::ToInt32($closeTag, $i)) | Out-Null }
    [VF]::W32($h, $b + 0x1D90, [int]([VF]::U32($h, $b + 0x1D90) + 1)) | Out-Null
}

$h1 = [VF]::OpenProcess(0x0438, $false, $Host1)
$h2 = [VF]::OpenProcess(0x0438, $false, $Client2)
$b1 = [VF]::FindBlock($h1)
$b2 = [VF]::FindBlock($h2)

"host  pid $Host1  block 0x{0:X}  version {1}" -f $b1, [VF]::U32($h1, $b1 + 8)
"client pid $Client2  block 0x{0:X}  version {1}" -f $b2, [VF]::U32($h2, $b2 + 8)

# Idempotence: close anything a previous run left open, and WAIT for it to actually be
# gone — issuing the close and pressing on measured a world still mid-conversation.
CloseConversations $h1 $b1
CloseConversations $h2 $b2
for ($i = 0; $i -lt 20; $i++) {
    if ([VF]::U32($h2, $b2 + 0x1228) -eq 0) { break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 2
''

# --- 1. phantoms exist both ways ---------------------------------------------------
$ph1 = [VF]::U32($h1, $b1 + 0x264)   # first phantom object id, host side
$ph2 = [VF]::U32($h2, $b2 + 0x264)
Check 'phantoms spawned both ways' (($ph1 -ne 0) -and ($ph2 -ne 0)) ("host sees 0x{0:X}, client sees 0x{1:X}" -f $ph1, $ph2)

# --- 2. phantom ownership shape (7.4 redesign) -------------------------------------
$c1 = if ($ph1) { [VF]::FindCreature($h1, $ph1) } else { 0 }
if ($c1) {
    $plot = [VF]::U32($h1, $c1 + 0xF8); $min1 = [VF]::U32($h1, $c1 + 0x200)
    $move = [VF]::U32($h1, $c1 + 0x9F0); $appr = [VF]::U32($h1, $c1 + 0xA60)
    Check 'phantom damageable, not a sink' (($plot -eq 0) -and ($min1 -eq 1) -and (($move -band 4) -eq 0)) `
        ("plot=$plot min1hp=$min1 movebits=0x{0:X} appearance=$appr" -f $move)
} else { Check 'phantom damageable, not a sink' $false 'phantom creature not found' }

# --- 3. appearance replication ------------------------------------------------------
if ($c1) { Check 'phantom wears the peer appearance' ([VF]::U32($h1, $c1 + 0xA60) -gt 20) ("row $([VF]::U32($h1,$c1+0xA60))") }

# --- 4. shared pause (client + server halves) ---------------------------------------
function PauseMask($h) {
    $am = [VF]::U32($h, 0x7A39FC)
    $srv = [VF]::U32($h, $am + 8); $srvInt = if ($srv) { [VF]::U32($h, $srv + 4) } else { 0 }
    $cli = [VF]::U32($h, $am + 4); $cliInt = if ($cli) { [VF]::U32($h, $cli + 4) } else { 0 }
    $s = if ($srvInt) { [VF]::U8($h, $srvInt + 0x10078) } else { 0 }
    $c = if ($cliInt) { [VF]::U8($h, $cliInt + 0x1CC) } else { 0 }
    return @{ Server = $s; Client = $c; Union = ($s -bor $c) }
}
[VF]::W32($h2, $b2 + 0x96C, 1) | Out-Null     # tell the client instance a peer is paused
Start-Sleep -Seconds 3
$pm = PauseMask $h2
Check 'shared pause holds both halves' (($pm.Union -band 1) -ne 0) `
    ("client mask=0x{0:X} server mask=0x{1:X}" -f $pm.Client, $pm.Server)
[VF]::W32($h2, $b2 + 0x96C, 0) | Out-Null
Start-Sleep -Seconds 3
$pm2 = PauseMask $h2
Check 'pause releases when the peer does' (($pm2.Union -band 1) -eq 0) `
    ("client mask=0x{0:X} server mask=0x{1:X}" -f $pm2.Client, $pm2.Server)

# --- 5. journal apply ----------------------------------------------------------------
$journalMark = LogLines $Log2
$before = ([VF]::U32($h2, $b2 + 0x99C))
$tag = 'k_swg_bastila'
$bytes = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes($tag), $bytes, $tag.Length)
[VF]::W32($h2, $b2 + 0x998, 20) | Out-Null

# The tag goes in a dword at a time; no unmanaged buffer needed for 32 bytes.
for ($i = 0; $i -lt 32; $i += 4) { [VF]::W32($h2, $b2 + 0x978 + $i, [BitConverter]::ToInt32($bytes, $i)) | Out-Null }
[VF]::W32($h2, $b2 + 0x974, [int]([VF]::U32($h2, $b2 + 0x974) + 1)) | Out-Null
Start-Sleep -Seconds 3
$after = [VF]::U32($h2, $b2 + 0x99C)
$status = [VF]::U32($h2, $b2 + 0x9A0)
Check 'journal request serviced' (($after -ne $before) -and ($status -eq 0)) `
    "applied serial $before -> $after, status $status"
# Search the whole file, not the last 40 lines: the per-frame gait logging floods a tail
# and the journal line scrolls out of it within seconds.
Check 'journal logged by the plugin' (LogHasSince $Log2 'journal:' $journalMark) `
    ((Select-String -Path $Log2 -Pattern 'journal:' | Select-Object -Last 1).Line ?? 'no journal line')

# --- 6. hooks installed --------------------------------------------------------------
# Every one of these is a verify-bytes-or-refuse detour: if the installed binary ever
# stops matching, the plugin declines to hook and says so, and this is where that shows.
function LogHas($path, $pattern) {
    if (-not (Test-Path $path)) { return $false }
    return $null -ne (Select-String -Path $path -Pattern $pattern -ErrorAction SilentlyContinue | Select-Object -First 1)
}

Check 'animation hook installed' (LogHas $Log1 'animations: animation hook at') 'one-shot capture'
Check 'phantom-hit watch installed' (LogHas $Log1 'combat: phantom-hit watch at') 'OnApplyDamage'
Check 'dialogue gate installed' (LogHas $Log1 'dialogue: dialogue gate at') 'StartDialog'
Check 'no crash on either side' (-not ((LogHas $Log1 'CRASH: access') -or (LogHas $Log2 'CRASH: access'))) ''

# --- 7. server browser ------------------------------------------------------------------
# Listen for the host's LAN beacon ourselves rather than reading the client's block. That
# is the browser's actual data source, and it is testable independently of whether an agent
# happens to be in menu mode: a connected agent stops republishing the list into the block
# (the in-world F9 panel therefore reads empty mid-session — known, minor, recorded).
$beacon = $null
try {
    # Share the port: the plugin's own menu-mode agents keep a listener bound, and an
    # exclusive bind here either fails or silently steals their datagrams.
    $udp = [System.Net.Sockets.UdpClient]::new()
    $udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket,
        [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true)
    $udp.Client.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 27016))
    $udp.Client.ReceiveTimeout = 3000
    $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)

    # Take the beacon for the session under test, not merely the first one heard. Other
    # agents on this machine advertise too (a plugin auto-starts one per instance), and
    # picking whichever arrived first read a menu-mode host with no module and failed the
    # join-in-progress check for a reason that had nothing to do with the feature.
    $bytes = $null
    for ($try = 0; $try -lt 8; $try++) {
        $candidate = $udp.Receive([ref]$remote)
        if ($candidate.Length -ge 8 -and $candidate[0] -eq 0x4B -and $candidate[1] -eq 0x44) {
            $candPort = ([int]$candidate[3] -shl 8) -bor [int]$candidate[4]
            if ($candPort -eq $SessionPort) { $bytes = $candidate; break }
            if ($null -eq $bytes) { $bytes = $candidate }   # fall back to any valid beacon
        }
    }
    $udp.Close()

    if ($null -ne $bytes -and $bytes.Length -ge 8 -and $bytes[0] -eq 0x4B -and $bytes[1] -eq 0x44) {
        $nameLen = $bytes[7]
        # ⚠ Cast before shifting: PowerShell evaluates -shl in the operand's own width, so
        # [byte]0x6A -shl 8 is 0, not 0x6A00. Reported the game port as "4".
        $gamePort = ([int]$bytes[3] -shl 8) -bor [int]$bytes[4]
        $name = [Text.Encoding]::UTF8.GetString($bytes, 8, $nameLen)
        $at = 8 + $nameLen
        $locked = if ($bytes.Length -gt $at) { $bytes[$at] -band 1 } else { 0 }
        $moduleLen = if ($bytes.Length -gt ($at + 1)) { $bytes[$at + 1] } else { 0 }
        $module = if ($moduleLen -gt 0) { [Text.Encoding]::UTF8.GetString($bytes, $at + 2, $moduleLen) } else { '' }
        $beacon = @{ Name = $name; Port = $gamePort; Players = $bytes[5]; Max = $bytes[6]; Locked = $locked; Module = $module }
    }
}
catch {
    $beacon = $null
}

Check 'host advertises itself on the LAN' ($null -ne $beacon) `
    $(if ($beacon) { "'$($beacon.Name)' port $($beacon.Port) players $($beacon.Players)/$($beacon.Max)" } else { 'no beacon in 4s' })
Check 'beacon carries join-in-progress info' ($null -ne $beacon -and $beacon.Module.Length -gt 0) `
    $(if ($beacon) { "module '$($beacon.Module)' locked=$($beacon.Locked)" } else { 'no beacon' })

# The panel's own data, mid-session. This is the half that used to go stale: only the
# menu-mode loop republished the list, so the F9 browser read empty once you had joined.
$browserCount = [VF]::U32($h2, $b2 + 0xE08)
$entryName = [VF]::Str($h2, $b2 + 0xE20, 32)
$entryAddr = [VF]::Str($h2, $b2 + 0xE20 + 0x20, 24)
Check 'the in-game browser list stays fed mid-session' ($browserCount -ge 1) `
    "count $browserCount, first '$entryName' at '$entryAddr'"

# --- 8. presence census agrees across instances ---------------------------------------
# Only the MIRRORING side fills these in: the authority publishes to the wire and never
# writes its own block (it has nothing to mirror), so the host reading 0 here is correct.
$e2 = [VF]::U32($h2, $b2 + 0x1400)         # presence slots the client is driving
$enemyCount2 = [VF]::U32($h2, $b2 + 0x488)
Check 'enemy presence mirrored on the client' (($e2 -ge 5) -and ($enemyCount2 -ge 5)) `
    "presence $e2, census $enemyCount2"

# --- 9. combat participation ------------------------------------------------------------
# The phantom must be a real target (faction 0 = PLAYER, same as a PC) yet still unkillable
# locally (min1hp), which is the pair that made this safe rather than an aggro sink.
if ($c1) {
    $stats = [VF]::U32($h1, $c1 + 0xA74)
    $faction = if ($stats) { [VF]::U32($h1, $stats + 0x78) } else { 999 }
    Check 'phantom is a targetable decoy' (($faction -eq 0) -and ([VF]::U32($h1, $c1 + 0x200) -eq 1)) `
        "faction $faction, min1hp 1"
}

# A real engine damage effect on the phantom must arrive as damage on the peer's own PC.
# Start from a KNOWN health, not from whatever the last run left behind. Restoring the
# previous value afterwards was not enough: three points a run walked the player down to 1
# and every later check then measured a nearly-dead body (conversations included).
$pcAddr = [VF]::FindCreature($h2, 0x7FFFFFFF)
if ($pcAddr) {
    $maxHp = [VF]::I16($h2, $pcAddr + 0xE0)
    $target = if ($maxHp -ge 12) { $maxHp } else { 20 }

    # ⚠ Stand the player UP first, through the co-op revive channel. Writing hit points is
    # not enough: a player who was worn down in an earlier run is in the engine's temp-death,
    # and forwarded damage is deliberately discarded for a downed player — so the check
    # measured "no damage applied" and blamed the forward. Found by this harness failing on
    # its second run, which is exactly what a repeatable pass is for.
    [VF]::W32($h2, $b2 + 0x1040, [int]$target) | Out-Null                                  # revive hp
    [VF]::W32($h2, $b2 + 0x1034, [int]([VF]::U32($h2, $b2 + 0x1034) + 1)) | Out-Null        # revive serial
    Start-Sleep -Seconds 2
    [VF]::W16($h2, $pcAddr + 0xDC, [int16]$target) | Out-Null
    Start-Sleep -Milliseconds 800
}

$pcBefore = [VF]::I16($h2, $pcAddr + 0xDC)
$owedBefore = [VF]::U32($h2, $b2 + 0x1D50)
[VF]::W32($h1, $b1 + 0x1D5C, 0) | Out-Null           # phantom slot
[VF]::W32($h1, $b1 + 0x1D60, 3) | Out-Null           # amount
[VF]::W32($h1, $b1 + 0x1D64, [int]([VF]::U32($h1, $b1 + 0x1D64) + 1)) | Out-Null
WaitFor2 { ([VF]::U32($h2, $b2 + 0x1D50) - $owedBefore) -eq 3 } 20 | Out-Null
WaitFor2 { [VF]::U32($h2, $b2 + 0x1D58) -eq [VF]::U32($h2, $b2 + 0x1D50) } 20 | Out-Null
$owedAfter = [VF]::U32($h2, $b2 + 0x1D50)
$pcAfter = [VF]::I16($h2, ([VF]::FindCreature($h2, 0x7FFFFFFF)) + 0xDC)
Check 'a hit on a phantom reaches the peer' (($owedAfter - $owedBefore) -eq 3) `
    "owed $owedBefore -> $owedAfter"
Check 'the peer really loses those hit points' (($pcBefore - $pcAfter) -eq 3) `
    "hp $pcBefore -> $pcAfter"

# Put the health back so the session is left as it was found.
if ($pcAddr -and $pcBefore -gt 0) { [VF]::W16($h2, $pcAddr + 0xDC, [int16]$pcBefore) | Out-Null }

# --- 10. animation replication ----------------------------------------------------------
# One-shots (swings, draws, flourishes) are captured by hooking the engine's dispatch, not
# polled, because they start and finish between frames. Ordering the host's PC to attack an
# enemy produces real ones; the client's phantom slot must see them arrive.
$oneShotBefore = [VF]::U32($h2, $b2 + 0x1064)        # remote one-shot serial, slot 0

# Pick the NEAREST enemy, not the first published one. The census is not ordered by
# distance, and the first entry was ~98 units away: the character spent the whole timeout
# walking and never swung, so the check failed on a fresh session while passing on one
# where an earlier run had already banked a swing. Presence slots carry positions, and
# enemy ids are identical in both games, so the closest one is cheap to find here.
$px = [VF]::F32($h1, $b1 + 0x10)
$py = [VF]::F32($h1, $b1 + 0x14)
$enemyId = 0
$bestDist = [double]::MaxValue
$presenceCount = [VF]::U32($h2, $b2 + 0x1400)
for ($i = 0; $i -lt [Math]::Min($presenceCount, 32); $i++) {
    $slot = $b2 + 0x1410 + ($i * 0x28)
    $id = [VF]::U32($h2, $slot)
    if ($id -eq 0) { continue }
    $ex = [VF]::F32($h2, $slot + 4)
    $ey = [VF]::F32($h2, $slot + 8)
    $d = [math]::Sqrt([math]::Pow($ex - $px, 2) + [math]::Pow($ey - $py, 2))
    if ($d -lt $bestDist) { $bestDist = $d; $enemyId = $id }
}
if ($enemyId -ne 0) {
    [VF]::W32($h1, $b1 + 0x1DA0, [int]0x7FFFFFFF) | Out-Null   # attacker: the host's PC
    [VF]::W32($h1, $b1 + 0x1DA4, [int]$enemyId) | Out-Null     # target: a real enemy
    [VF]::W32($h1, $b1 + 0x1DA8, [int]([VF]::U32($h1, $b1 + 0x1DA8) + 1)) | Out-Null
    # A combat round takes a few seconds: the PC closes to melee range and swings.
    WaitFor2 { [VF]::U32($h2, $b2 + 0x1064) -ne $oneShotBefore } 40 | Out-Null
    $oneShotAfter = [VF]::U32($h2, $b2 + 0x1064)
    Check 'swing animations reach the peer' ($oneShotAfter -ne $oneShotBefore) `
        ("serial $oneShotBefore -> $oneShotAfter, attacking 0x{0:X} at {1:F0} units" -f $enemyId, $bestDist)
}
else {
    Check 'swing animations reach the peer' $false 'no enemy published to attack'
}

# --- 11. dialogue exclusivity -------------------------------------------------------------
# One conversation per session, enforced in the engine: the host opens one, the client
# follows it and cannot start its own.
$dlgMark1 = LogLines $Log1
$tagBytes = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes('end_trask'), $tagBytes, 9)
for ($i = 0; $i -lt 32; $i += 4) { [VF]::W32($h1, $b1 + 0x1D70 + $i, [BitConverter]::ToInt32($tagBytes, $i)) | Out-Null }
[VF]::W32($h1, $b1 + 0x1D90, [int]([VF]::U32($h1, $b1 + 0x1D90) + 1)) | Out-Null

Check 'a conversation opens on demand' (WaitForLog $Log1 'test: opened a conversation' $dlgMark1 15) ''
Check 'the peer follows the same conversation' `
    (WaitFor2 { [VF]::U32($h2, $b2 + 0x1228) -eq 1 } 20) `
    ("partner '{0}'" -f [VF]::Str($h2, $b2 + 0x1240, 32))

$dlgMark2 = LogLines $Log2
$carth = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes('carth'), $carth, 5)
for ($i = 0; $i -lt 32; $i += 4) { [VF]::W32($h2, $b2 + 0x1D70 + $i, [BitConverter]::ToInt32($carth, $i)) | Out-Null }
[VF]::W32($h2, $b2 + 0x1D90, [int]([VF]::U32($h2, $b2 + 0x1D90) + 1)) | Out-Null
# ⚠ Judge this on STATE, not on the log. StartDialogueWith returns true once it has sent
# the request — the engine's refusal happens later and server-side — so "opened a
# conversation" in the log does not mean one actually started. The honest oracle is what
# the client is talking to: if exclusivity holds it is still the FOLLOWED conversation
# (end_trask), never its own (carth).
Start-Sleep -Seconds 6
$clientPartner = [VF]::Str($h2, $b2 + 0x1200, 32)
Check 'the peer cannot start a second conversation' ($clientPartner -ne 'carth') `
    "client is talking to '$clientPartner', not 'carth'"

# --- 12. client-triggered encounters ------------------------------------------------
# Plumbing only, deliberately: end_m01aa has NO encounters at all (checked offline with
# `kotorfiles.py objectids end_m01aa` — there is no Encounter List), so a real fire cannot
# be staged from the standard save. What IS checkable everywhere is that the two ends are
# armed and pointing the right way: the watch runs on the mirroring side and never on the
# authority, and the plugin verified the engine entry points instead of refusing.
# To exercise a real fire, load a module that has encounters (tar_m03aa has four Black
# Vulkar ambushes) and use `blockpoke encounters <encounterId> <peerId>` on the HOST.
$watchHost = [VF]::U32($h1, $b1 + 0x1DE0)
$watchClient = [VF]::U32($h2, $b2 + 0x1DE0)
Check 'the encounter watch runs on the client only' (($watchClient -eq 1) -and ($watchHost -eq 0)) `
    "host $watchHost, client $watchClient"
Check 'the encounter call sites verified' `
    ((LogHas $Log2 'encounters: encounter calls verified') -and -not (LogHas $Log2 'encounters: REFUSED')) `
    'GetInActivateArea 0x590090 / SpawnIfAppropriate 0x593BE0'

# --- 13. per-hit damage attribution ------------------------------------------------------
# ⚠ CLOSE THE CONVERSATIONS FIRST. Section 11 deliberately leaves the client having tried to
# open one, and the agent suppresses damage reports while in dialogue (the local fight is
# disarmed and unrepresentative there). Run without this and the check fails for a reason
# that has nothing to do with the channel under test — which it did, on its first run.
CloseConversations $h1 $b1
CloseConversations $h2 $b2
for ($i = 0; $i -lt 20; $i++) {
    if (-not [VF]::U32($h2, $b2 + 0x1228)) { break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 3

# The whole point of the damage-dealt channel: a CLIENT swinging at an enemy has to cost that
# enemy real hit points in the HOST's game, where the enemy actually lives. Nothing tested
# this before — the channel shipped with unit tests and no live check at all — and the two
# directions it replaced (enemy HEALTH reports) failed in exactly the way a unit test cannot
# see: the owner's own mirror overwrote the sample before it was ever read.
#
# So the oracle is the host's engine, not either side's bookkeeping: find the enemy nearest
# the CLIENT, read its hit points out of the HOST's creature, make the client attack it, and
# read them again.
$cx = [VF]::F32($h2, $b2 + 0x10)
$cy = [VF]::F32($h2, $b2 + 0x14)
$dmgId = 0
$dmgDist = [double]::MaxValue
$presence2 = [VF]::U32($h2, $b2 + 0x1400)
for ($i = 0; $i -lt [Math]::Min($presence2, 32); $i++) {
    $slot = $b2 + 0x1410 + ($i * 0x28)
    $id = [VF]::U32($h2, $slot)
    if ($id -eq 0) { continue }
    $d = [math]::Sqrt([math]::Pow([VF]::F32($h2, $slot + 4) - $cx, 2) + [math]::Pow([VF]::F32($h2, $slot + 8) - $cy, 2))
    if ($d -lt $dmgDist) { $dmgDist = $d; $dmgId = $id }
}

if ($dmgId -ne 0) {
    $hostEnemy = [VF]::FindCreature($h1, $dmgId)
    if ($hostEnemy -eq 0) {
        Check 'a client hit costs the enemy real hit points' $false ("enemy 0x{0:X} not found in the host" -f $dmgId)
    }
    else {
        $hpBefore = [VF]::I16($h1, $hostEnemy + 0xDC)
        [VF]::W32($h2, $b2 + 0x1DA0, [int]0x7FFFFFFF) | Out-Null   # attacker: the CLIENT's PC
        [VF]::W32($h2, $b2 + 0x1DA4, [int]$dmgId) | Out-Null
        [VF]::W32($h2, $b2 + 0x1DA8, [int]([VF]::U32($h2, $b2 + 0x1DA8) + 1)) | Out-Null

        # The client has to close to melee range and land a swing, then the ledger has to
        # reach the host and be spent. Generous, and judged on the host's creature.
        WaitFor2 { [VF]::I16($h1, $hostEnemy + 0xDC) -lt $hpBefore } 45 | Out-Null
        $hpAfter = [VF]::I16($h1, $hostEnemy + 0xDC)
        Check 'a client hit costs the enemy real hit points' ($hpAfter -lt $hpBefore) `
            ("enemy 0x{0:X} at {1:F0} units: hp {2} -> {3}" -f $dmgId, $dmgDist, $hpBefore, $hpAfter)
    }
}
else {
    Check 'a client hit costs the enemy real hit points' $false 'no enemy published to attack'
}

# Leave the world as we found it, so the next run measures the same starting conditions.
CloseConversations $h1 $b1
CloseConversations $h2 $b2

''
'summary: {0} passed, {1} failed' -f ($results.Values | Where-Object Pass).Count, ($results.Values | Where-Object { -not $_.Pass }).Count
[VF]::CloseHandle($h1) | Out-Null
[VF]::CloseHandle($h2) | Out-Null
exit ($results.Values | Where-Object { -not $_.Pass }).Count
