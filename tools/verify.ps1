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
    [int]$SessionPort = 27140,

    # The CLIENT agent's drop file (demo.ps1 starts it with --drop-file). This is the only
    # lever that makes a real, session-owned ground item exist: see section 15 for why the
    # shared block cannot be used to stage one.
    [string]$ClientDropFile = "$env:TEMP\kotorcoop-demo\drop2.txt"
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
function NewLogLines($path, [int]$since) {
    if (-not (Test-Path $path)) { return @() }
    $lines = @(Get-Content $path)
    if ($lines.Count -le $since) { return @() }
    return $lines[$since..($lines.Count - 1)]
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
# The counterpart to CloseConversations: a creature left in combat by an earlier check cannot
# start a conversation, and the dialogue checks run after the fight the animation check stages.
# (attacker set, target zero) is the plugin's "break off" order.
function BreakOffCombat($h, $b) {
    [VF]::W32($h, $b + 0x1DA0, [int]0x7FFFFFFF) | Out-Null   # this instance's own PC
    [VF]::W32($h, $b + 0x1DA4, 0) | Out-Null                 # no target = stop
    [VF]::W32($h, $b + 0x1DA8, [int]([VF]::U32($h, $b + 0x1DA8) + 1)) | Out-Null
}

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

# ⚠ Put the fight down before talking. Section 10 orders the host's PC to attack and leaves it
# in combat; a creature in combat cannot open a conversation, so "the peer follows the same
# conversation" failed on any session that had already fought — which read as a spectate
# regression more than once and was never a dialogue defect at all.
BreakOffCombat $h1 $b1
BreakOffCombat $h2 $b2
Start-Sleep -Seconds 4

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

# --- 11a. the interaction lock reaches the object's OWNER ---------------------------------
# Section 11 leaves the HOST owning a conversation with end_trask and the CLIENT following it.
# The client's own claim loses that race (the host got there first), so the lock the CLIENT
# should be seeing is the host's — one object held by a peer.
#
# This is the channel that fixes "Trask keeps walking around on the other screen": ownership
# is one-directional, so without a lock the owner's AI is never told anything is happening.
$clientLocks = [VF]::U32($h2, $b2 + 0x1358)
Check 'an interaction lock reaches the other instance' ($clientLocks -ge 1) `
    "client sees $clientLocks object(s) held by a peer"

# --- 11b. NPC barks reach the other player ------------------------------------------------
# The one-line bubble an NPC throws up without ever entering the dialogue system — the utility
# droid's beep. It reaches NO other channel, so every check above can pass while one player
# hears a droid and the other gets silence, which is exactly how this shipped broken twice.
#
# ⚠ Close the conversation first: BarkController skips replay while a conversation owns the
# screen, and section 11 deliberately leaves one open. Without this the check fails for a
# reason that has nothing to do with barks — the same trap section 11 itself carries.
CloseConversations $h1 $b1
CloseConversations $h2 $b2
Start-Sleep -Seconds 2

$barkMark = LogLines $Log2
$weld = [byte[]]::new(32)
[Array]::Copy([Text.Encoding]::ASCII.GetBytes('end_weld'), $weld, 8)
for ($i = 0; $i -lt 32; $i += 4) { [VF]::W32($h1, $b1 + 0x1DB0 + $i, [BitConverter]::ToInt32($weld, $i)) | Out-Null }
[VF]::W32($h1, $b1 + 0x1DD0, [int]([VF]::U32($h1, $b1 + 0x1DD0) + 1)) | Out-Null

# The oracle is the CLIENT saying it spoke the line. The host capturing something was never
# in doubt — it captured `this` instead of the speaker's object id and asked the far side for
# a creature numbered 0x011680D0.
Check 'an NPC bark reaches the other player' (WaitForLog $Log2 "bark: replayed a peer's line" $barkMark 12) ''

# And exactly ONCE. The remote ring is written to a fresh slot on every delivery, so a
# per-slot high-water mark let a single bark play seven times — invisible for as long as
# every replay was failing anyway.
Start-Sleep -Seconds 3
$barkPlays = @(NewLogLines $Log2 $barkMark | Where-Object { $_ -match "bark: replayed a peer's line" }).Count
Check 'that bark is spoken once, not once per delivery' ($barkPlays -eq 1) "replayed $barkPlays time(s)"

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
    # ⚠ SKIP AN ENEMY THAT IS ALREADY DOWN TO 1. The check asks whether a client's hit costs
    # the enemy hit points, and a creature sitting at 1 cannot answer it — an earlier run of
    # this same battery leaves bodies behind, and picking one gave "hp 1 -> 1", which looks
    # like a broken channel and is nothing of the kind. Judge on the HOST's creature, which
    # is the authority.
    $cand = [VF]::FindCreature($h1, $id)
    if ($cand -eq 0 -or [VF]::I16($h1, $cand + 0xDC) -le 1) { continue }

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

# --- 14. shared progression (party gold and PC experience) -------------------------------
# Host-authoritative, and — unlike almost everything else in this battery — carried entirely
# OUTSIDE the shared block: the agent reads the host's wallet and experience straight out of
# the host's engine and writes them into the client's (CoopAgent.PumpProgression ->
# KotorGameBridge.TryReadProgression / TryWriteProgression). There is no plugin half at all.
#
# So the only honest oracle is the CLIENT'S OWN ENGINE FIELDS: change the number in the
# host's game and wait for the client's game to be holding it too. Nothing here reads either
# agent's bookkeeping, and neither side's "I published it" would prove anything — publishing
# is not arriving.
#
# Pointer chains, both from docs/research/progression-sync.md and both VERIFIED there:
#   appMgr 0x7A39FC -> serverApp +8 -> internal +4 -> partyTable +0x1B770, gold at +0xFC
#   PC (object id 0x7FFFFFFF)      -> stats +0xA74                      -> experience +0x68
# The first three hops are the same ones PauseMask already walks in section 4.
function PartyTable($h) {
    $am = [VF]::U32($h, 0x7A39FC)
    if ($am -eq 0) { return 0 }
    $srv = [VF]::U32($h, $am + 8)
    if ($srv -eq 0) { return 0 }
    $srvInt = [VF]::U32($h, $srv + 4)
    if ($srvInt -eq 0) { return 0 }
    return $srvInt + 0x1B770
}

$partyTable1 = PartyTable $h1
$partyTable2 = PartyTable $h2
$pcHost = [VF]::FindCreature($h1, 0x7FFFFFFF)
$pcPeer = [VF]::FindCreature($h2, 0x7FFFFFFF)
$statsHost = if ($pcHost) { [VF]::U32($h1, $pcHost + 0xA74) } else { 0 }
$statsPeer = if ($pcPeer) { [VF]::U32($h2, $pcPeer + 0xA74) } else { 0 }

if ($partyTable1 -and $partyTable2 -and $statsHost -and $statsPeer) {
    $goldBefore = [VF]::U32($h1, $partyTable1 + 0xFC)
    $xpBefore = [VF]::U32($h1, $statsHost + 0x68)

    # Move both by a small offset from what the host already has, rather than to a fixed
    # marker number. Two reasons: the session can be restored exactly afterwards, and the
    # target can never coincide with the value the client is already sitting on — the mirror
    # means the client starts EQUAL to the host, so "it now reads the new number" is only
    # meaningful if the new number is one nothing was holding a moment ago.
    #
    # ⚠ GOLD AND EXPERIENCE NO LONGER TRAVEL THE SAME WAY, and this section used to assume
    # they did. Gold is a purse the host owns; experience MERGES UPWARD BY MAX in both
    # directions, because both engines already grant kill XP independently (a mirrored enemy
    # dies through the real damage pipeline, so ApplyDeathExperience runs on the mirror too)
    # and an absolute overwrite was erasing a client's plot XP within a tick.
    #
    # So the old check — push the host's XP DOWN and wait for the client to follow — now
    # asserts exactly the bug that was fixed. It moves UP, and the downward case becomes its
    # own check: the client must REFUSE to be pulled backwards.
    $goldTarget = $goldBefore + 137
    $xpTarget = $xpBefore + 5   # small: crossing a level threshold lights a modal prompt

    [VF]::W32($h1, $partyTable1 + 0xFC, [int]$goldTarget) | Out-Null
    [VF]::W32($h1, $statsHost + 0x68, [int]$xpTarget) | Out-Null

    # One heavy pump a second on each side plus a wire hop; 25 s is many times over, and
    # both fields travel in the same snapshot so one wait covers them.
    WaitFor2 {
        ([VF]::U32($h2, $partyTable2 + 0xFC) -eq $goldTarget) -and
        ([VF]::U32($h2, $statsPeer + 0x68) -eq $xpTarget)
    } 25 | Out-Null

    $goldSeen = [VF]::U32($h2, $partyTable2 + 0xFC)
    $xpSeen = [VF]::U32($h2, $statsPeer + 0x68)

    Check 'party gold reaches the other instance' ($goldSeen -eq $goldTarget) `
        "host $goldBefore -> $goldTarget, client's engine reads $goldSeen"
    Check 'PC experience rises on the other instance' ($xpSeen -eq $xpTarget) `
        "host $xpBefore -> $xpTarget, client's engine reads $xpSeen"

    # The guard, and the whole reason the channel changed: a client that has earned XP the
    # host has not — plot XP from a conversation only it is having — must not have it stamped
    # back down. Drop the host well below and give it longer than a merge would need.
    [VF]::W32($h1, $statsHost + 0x68, [int]($xpBefore - 20)) | Out-Null
    Start-Sleep -Seconds 6
    $xpAfterDrop = [VF]::U32($h2, $statsPeer + 0x68)
    $xpHostHealed = [VF]::U32($h1, $statsHost + 0x68)

    Check "a client's experience cannot be pulled backwards" ($xpAfterDrop -eq $xpTarget) `
        "host dropped to $($xpBefore - 20), client's engine still reads $xpAfterDrop"

    # ...and the merge runs the other way too: the client keeps reporting its absolute, so
    # the host climbs back to it rather than the pair settling at the lower number.
    Check 'the host adopts the higher experience back' ($xpHostHealed -eq $xpTarget) `
        "host wrote $($xpBefore - 20), reads back $xpHostHealed"

    # Put the numbers back. ⚠ BOTH SIDES, BY HAND: a max-merged value cannot be restored
    # downward through the channel — writing only the host would have the client's higher
    # figure pull it straight back up, which is the behaviour two checks above just proved.
    [VF]::W32($h1, $partyTable1 + 0xFC, [int]$goldBefore) | Out-Null
    [VF]::W32($h1, $statsHost + 0x68, [int]$xpBefore) | Out-Null
    [VF]::W32($h2, $statsPeer + 0x68, [int]$xpBefore) | Out-Null
    WaitFor2 { [VF]::U32($h2, $partyTable2 + 0xFC) -eq $goldBefore } 20 | Out-Null
}
else {
    # Emit BOTH checks even when the pointers do not resolve. A check that is skipped
    # silently is counted as neither pass nor fail and simply disappears from the summary,
    # which reads exactly like it passed.
    $progressionWhy = "partyTable host 0x{0:X} client 0x{1:X}, stats host 0x{2:X} client 0x{3:X}" -f `
        $partyTable1, $partyTable2, $statsHost, $statsPeer
    Check 'party gold reaches the other instance' $false $progressionWhy
    Check 'PC experience rises on the other instance' $false $progressionWhy
    Check "a client's experience cannot be pulled backwards" $false $progressionWhy
    Check 'the host adopts the higher experience back' $false $progressionWhy
}

# --- 15. shared ground items (an item taken here vanishes there) -------------------------
# ⚠ Put the fight and any conversation down first. The pickup below is a real ENGINE ACTION
# queued on the local player, and a character in combat or mid-conversation will not walk
# over and take anything — the same trap sections 11, 11b and 13 each carry.
BreakOffCombat $h1 $b1
BreakOffCombat $h2 $b2
CloseConversations $h1 $b1
CloseConversations $h2 $b2
Start-Sleep -Seconds 4

# ⚠ THE DROP CANNOT BE STAGED FROM THE SHARED BLOCK, and it is worth saying why, because it
# is the obvious thing to try. The item region at 0x290 is the AGENT's desired-state area:
# an item written there by hand never enters the SESSION's item store, so it can never reach
# the peer, and a check built on it would only be watching one plugin talk to itself — a
# check that passes for the wrong reason.
#
# The one lever that produces a genuinely shared item is the agent's own drop file
# (demo.ps1 gives each agent --drop-file), which runs exactly the code the dashboard's D key
# runs: Session.DropItem -> the reliable item op -> both agents' desired sets -> both
# plugins spawn a body.
#
# Dropped from the CLIENT, deliberately. The item lands 1.6 units in front of the DROPPING
# player, so the client has one step to walk when it is asked to pick it back up. Drop from
# the host instead and the item lands wherever the host's PC finished section 10's fight,
# which can be tens of units from the client — the same distance mistake that made the
# animation check fail on a fresh session.
$dropResref = 'g_i_medeqpmnt01'   # a medpac: stackable, so a run that succeeds adds no new
                                  # inventory line to the client's character

# Item status is the PLUGIN's report, one 0x10-byte entry per slot: itemId, the engine
# object it created, state (1 alive, 2 vanished), error. Judging on THIS and not on the
# desired slots at 0x290 is the whole point — the desired region only says the agent asked
# for an item, while a non-zero objectId here means the peer's engine really created a body,
# and the plugin's aliveness probe re-asks the engine every 20 frames, so a slot that STAYS
# alive is a body the engine still says is lying on the floor.
function ItemSlotOf($h, $b, $itemId) {
    for ($k = 0; $k -lt 8; $k++) {
        if ([VF]::U32($h, $b + 0x410 + ($k * 0x10)) -eq $itemId) { return $k }
    }
    return -1
}
function ItemState($h, $b, $slot) { [VF]::U32($h, $b + 0x410 + ($slot * 0x10) + 8) }
function ItemBody($h, $b, $slot) { [VF]::U32($h, $b + 0x410 + ($slot * 0x10) + 4) }

# Remember which items the HOST already has bodies for. A previous run whose pickup did not
# complete leaves an item lying there, and mistaking it for this run's drop would report a
# pass from stale evidence — the same lesson the log helpers at the top of this file learned.
$knownItems = @{}
for ($i = 0; $i -lt 8; $i++) {
    $id = [VF]::U32($h1, $b1 + 0x410 + ($i * 0x10))
    if ($id -ne 0) { $knownItems["$id"] = $true }
}

$dropRequested = $false
try {
    Set-Content -Path $ClientDropFile -Value $dropResref -Encoding ASCII -ErrorAction Stop
    $dropRequested = $true
}
catch {
    $dropRequested = $false
}

# The agent DELETES the file as it takes the request, so the file going away is proof the
# request was seen at all. Without this a session started without --drop-file (or with a
# different work directory) would fail the checks below exactly as if the item channel were
# broken, which is a lie about which thing is wrong.
$dropTaken = $dropRequested -and (WaitFor2 { -not (Test-Path $ClientDropFile) } 15)

$script:newItemId = 0
if ($dropTaken) {
    WaitFor2 {
        for ($k = 0; $k -lt 8; $k++) {
            $entry = $b1 + 0x410 + ($k * 0x10)
            $id = [VF]::U32($h1, $entry)
            if ($id -eq 0 -or $knownItems.ContainsKey("$id")) { continue }
            if (([VF]::U32($h1, $entry + 8) -eq 1) -and ([VF]::U32($h1, $entry + 4) -ne 0)) {
                $script:newItemId = $id
                return $true
            }
        }
        return $false
    } 30 | Out-Null
}

$hostSlot = if ($newItemId) { ItemSlotOf $h1 $b1 $newItemId } else { -1 }
$hostBody = if ($hostSlot -ge 0) { ItemBody $h1 $b1 $hostSlot } else { 0 }

# The DROPPING instance's own body can lag the peer's — the plugin spaces its engine
# mutations 15 frames apart and the two plugins are not in step — so wait for the client's
# slot rather than deciding on one read. Deciding on one read is how the pickup below would
# end up aimed at slot 0, which holds somebody else's item.
$script:clientSlot = -1
if ($newItemId) {
    WaitFor2 {
        $script:clientSlot = ItemSlotOf $h2 $b2 $newItemId
        ($script:clientSlot -ge 0) -and ((ItemState $h2 $b2 $script:clientSlot) -eq 1)
    } 20 | Out-Null
}

Check 'a dropped item is created in the peer''s game' (($newItemId -ne 0) -and ($hostBody -ne 0)) `
    $(if ($newItemId -ne 0) { "item 0x{0:X} is object 0x{1:X} in the host, slot {2}" -f $newItemId, $hostBody, $hostSlot }
      elseif (-not $dropRequested) { "could not write the drop request to $ClientDropFile" }
      elseif (-not $dropTaken) { "the client agent never took the drop request from $ClientDropFile (was this session started by demo.ps1?)" }
      else { "the request was taken but no new item body appeared in the host within 30s" })

if (($newItemId -ne 0) -and ($clientSlot -ge 0)) {
    $issuedBefore = [VF]::U32($h2, $b2 + 0x284)

    # The pickup TEST HOOK: the 1-based slot index of an item the local player should walk
    # over and take, through the engine's own ActionPickUpItem. It exists because clicking a
    # small object on the floor is not something automation can do reliably, and it is the
    # identical write KotorGameBridge.TryRequestPickUp makes — so nothing exercised here is a
    # path a player never runs.
    [VF]::W32($h2, $b2 + 0x280, [int]($clientSlot + 1)) | Out-Null

    # First the client's own plugin has to see the item leave ITS floor. That is the engine's
    # answer to "who possesses this object", asked by the aliveness probe — NOT the return
    # value of the pickup call, which only ever meant the action was queued.
    $tookIt = WaitFor2 { (ItemState $h2 $b2 $clientSlot) -eq 2 } 45
    $issuedAfter = [VF]::U32($h2, $b2 + 0x284)

    # And then the peer's copy has to go. This is the whole reason the channel exists: an
    # item taken in one game must not still be lying there in the other, or two players pick
    # up the same medpac. The host's plugin destroys its body once the session stops wanting
    # the item, so the slot either clears or stops reading Alive.
    $goneOnPeer = WaitFor2 {
        $s = ItemSlotOf $h1 $b1 $newItemId
        ($s -lt 0) -or ((ItemState $h1 $b1 $s) -ne 1)
    } 30

    # The issued counter is part of the verdict, not decoration. An item can leave the floor
    # for reasons that are nothing to do with this check (it spawned somewhere unreachable and
    # the probe reported it gone), and the host's copy would then be withdrawn too — a pass
    # for entirely the wrong reason. The plugin only bumps this once the engine has ACCEPTED
    # the pick-up action, so requiring it ties the vanish to the action we asked for.
    $finalSlot = ItemSlotOf $h1 $b1 $newItemId
    Check 'an item picked up here vanishes on the peer' `
        (($issuedAfter -ne $issuedBefore) -and $tookIt -and $goneOnPeer) `
        ("pickups issued {0} -> {1}, client slot {2} state {3}, host slot {4}" -f `
            $issuedBefore, $issuedAfter, $clientSlot, (ItemState $h2 $b2 $clientSlot),
            $(if ($finalSlot -lt 0) { 'cleared' } else { "still state $(ItemState $h1 $b1 $finalSlot)" }))

    # ⚠ Nothing can withdraw a shared item from outside the session, so a pickup that did not
    # complete leaves this item on both floors for the next run. That is why the check above
    # snapshots the host's existing items first: a leftover is ignored rather than mistaken
    # for the new drop. It does cost one of the eight item slots per failed run.
}
else {
    Check 'an item picked up here vanishes on the peer' $false `
        $(if ($newItemId -eq 0) { 'no item was dropped to pick up' } else { "item 0x{0:X} has no slot in the client" -f $newItemId })
}

# --- 16. death handling ------------------------------------------------------------------
# Three separate things, and they are checked separately because they fail separately.
#
# 1. The two byte patches exist. Both are verify-bytes-or-refuse detours (the control
#    auto-swap funnel, and the single CALL that raises game-over), so if the installed binary
#    ever stops matching, the plugin declines and says so — this is where that shows, exactly
#    as for the hooks in section 6.
# 2. They are ARMED the right way round. Both patches are inert until the agent sets their
#    flags, and the swap suppression is a CLIENT-side lock ONLY: applying it to the host too
#    left nobody able to switch character at all, which was reported from play. That is a
#    directional fact about live state, so it is checked as one.
# 3. The downed -> revive round trip actually runs.
Check 'death guards installed on both sides' `
    ((LogHas $Log1 'death handling: control-swap guard at') -and (LogHas $Log2 'death handling: control-swap guard at')) `
    'ChangeCharacter 0x5F6B60 + the game-over call site 0x4B6E99'

$suppressOverHost = [VF]::U32($h1, $b1 + 0x102C)
$suppressOverPeer = [VF]::U32($h2, $b2 + 0x102C)
$suppressSwapHost = [VF]::U32($h1, $b1 + 0x1030)
$suppressSwapPeer = [VF]::U32($h2, $b2 + 0x1030)
Check 'death suppressions armed the right way round' `
    (($suppressOverHost -ne 0) -and ($suppressOverPeer -ne 0) -and ($suppressSwapHost -eq 0) -and ($suppressSwapPeer -ne 0)) `
    "game-over host $suppressOverHost client $suppressOverPeer, swap host $suppressSwapHost client $suppressSwapPeer"

if ($pcPeer -eq 0) { $pcPeer = [VF]::FindCreature($h2, 0x7FFFFFFF) }

if ($pcPeer -eq 0) {
    Check 'a downed player is reported to the agent' $false 'client PC not found'
    Check 'the co-op revive stands the player back up' $false 'client PC not found'
}
else {
    $maxPeer = [VF]::I16($h2, $pcPeer + 0xE0)

    # ⚠ STAND THE PLAYER UP FIRST if an earlier section left him down. The oracle below is a
    # RISING edge, and a player who is already down produces no edge at all — so the check
    # would fail while reporting nothing about the channel. Section 9 learned the same thing
    # from the other side (forwarded damage is discarded for a downed player) and revives
    # before it measures; this is that lesson applied to the section that owns the state.
    if ([VF]::I16($h2, $pcPeer + 0xDC) -le 0) {
        [VF]::W32($h2, $b2 + 0x1040, [int]$(if ($maxPeer -ge 20) { 20 } else { [Math]::Max($maxPeer, 1) })) | Out-Null
        [VF]::W32($h2, $b2 + 0x1034, [int]([VF]::U32($h2, $b2 + 0x1034) + 1)) | Out-Null
        WaitFor2 { [VF]::I16($h2, $pcPeer + 0xDC) -gt 0 } 15 | Out-Null
        Start-Sleep -Seconds 2
    }

    $hpSaved = [VF]::I16($h2, $pcPeer + 0xDC)
    $tempSaved = [VF]::U32($h2, $pcPeer + 0xE4)
    $downedSerialBefore = [VF]::U32($h2, $b2 + 0x1024)
    $reviveAppliedBefore = [VF]::U32($h2, $b2 + 0x1038)
    $gameOverBefore = [VF]::U32($h2, $b2 + 0x1028)
    $downBlock = [VF]::U32($h2, $b2 + 0x1F60)

    # ⚠ THE HIT POINTS ARE ZEROED FROM OUTSIDE THE ENGINE, ON PURPOSE, and that is the only
    # way this can be staged on the test save. The Endar Spire command deck pins the PC at
    # min-1-hp for the whole deck (docs/research/min-one-hitpoint.md — it is a module rule,
    # not a co-op fault), so every route THROUGH the engine is clamped at 1 and NOBODY can be
    # downed there: not by a damage effect, not by the forwarded-damage channel section 9
    # uses, not in a normal playthrough either. A cross-process write is not clamped, and the
    # plugin's downed test is the engine's own CSWSCreature::GetDeadTemp — "current plus
    # temporary hit points <= 0" — which is exactly what this produces. The block's
    # DownBlockReason at 0x1F60 is reported below so a reader can see the floor for himself.
    #
    # What this does NOT stage is the engine's death choreography: OnApplyDeath, the control
    # auto-swap and the game-over all hang off the damage handler, not off the hit-point
    # field. Those are what the two checks above cover, and there is no way to fire them here
    # without a module that lets a PC be damaged to zero.
    [VF]::W32($h2, $pcPeer + 0xE4, 0) | Out-Null
    [VF]::W16($h2, $pcPeer + 0xDC, [int16]0) | Out-Null

    # The oracle is the EDGE counter, not the level at 0x1020. The agent revives a downed
    # player the moment a standing peer is within a few units, and the engine's own
    # TempDeathUpdate stands people up after five clear seconds, so the level can easily be
    # back to 0 before the next poll — a serial cannot be missed that way.
    $sawDown = WaitFor2 { [VF]::U32($h2, $b2 + 0x1024) -ne $downedSerialBefore } 15
    Check 'a downed player is reported to the agent' $sawDown `
        ("downed serial {0} -> {1}, game-over attempts {2} -> {3}, down-block reason {4}" -f `
            $downedSerialBefore, [VF]::U32($h2, $b2 + 0x1024), $gameOverBefore,
            [VF]::U32($h2, $b2 + 0x1028), $downBlock)

    # Drive the revive ourselves rather than waiting on the agent's proximity rule, which
    # depends on where the two players happen to be standing after section 13. Same channel
    # either way — and if the agent's own rescue fires first, that is the same path arriving
    # by the other door, so it cannot make this pass falsely.
    $reviveHp = if ($hpSaved -gt 0) { $hpSaved } elseif ($maxPeer -ge 20) { 20 } else { [Math]::Max($maxPeer, 1) }
    [VF]::W32($h2, $b2 + 0x1040, [int]$reviveHp) | Out-Null
    [VF]::W32($h2, $b2 + 0x1034, [int]([VF]::U32($h2, $b2 + 0x1034) + 1)) | Out-Null

    # ⚠ Judged on the ENGINE's hit points AND on the plugin's applied/status pair together.
    # Hit points alone would also be satisfied by the engine's five-second auto-revive, which
    # is not the channel under test; the applied serial moving with a zero status is what says
    # the co-op revive ran, and a failed revive still echoes the serial (with a non-zero
    # status), so the status has to be read too.
    WaitFor2 {
        ([VF]::U32($h2, $b2 + 0x1038) -ne $reviveAppliedBefore) -and ([VF]::I16($h2, $pcPeer + 0xDC) -gt 0)
    } 25 | Out-Null

    $reviveApplied = [VF]::U32($h2, $b2 + 0x1038)
    $reviveStatus = [VF]::U32($h2, $b2 + 0x103C)
    $hpAfterRevive = [VF]::I16($h2, $pcPeer + 0xDC)
    $downedNow = [VF]::U32($h2, $b2 + 0x1020)
    Check 'the co-op revive stands the player back up' `
        (($reviveApplied -ne $reviveAppliedBefore) -and ($reviveStatus -eq 0) -and ($hpAfterRevive -gt 0) -and ($downedNow -eq 0)) `
        ("applied {0} -> {1}, status {2}, hp {3}, downed {4}" -f `
            $reviveAppliedBefore, $reviveApplied, $reviveStatus, $hpAfterRevive, $downedNow)

    # Leave the player as we found him. The wait first is load-bearing: the plugin tops the
    # hit points up a TICK AFTER the Resurrection (a same-frame write lands while the target
    # still reads dead and the engine drops it), and the agent's own rescue may have queued a
    # second top-up of its own — restoring before those land would simply be overwritten.
    Start-Sleep -Seconds 3
    [VF]::W32($h2, $pcPeer + 0xE4, [int]$tempSaved) | Out-Null
    if ($hpSaved -gt 0) { [VF]::W16($h2, $pcPeer + 0xDC, [int16]$hpSaved) | Out-Null }
}

# --- 17. engine call signatures ----------------------------------------------------------
# Static, but it belongs in the battery rather than in a comment nobody re-runs: a __thiscall
# callee pops its OWN arguments, so a declaration with the wrong number of them skews the
# stack four bytes at a time and surfaces as a stack-cookie fast-fail (0xC0000409) inside some
# unrelated function later. Two declarations were wrong when this check was written, and
# neither had ever produced a symptom anyone could trace back to it.
$audit = & python (Join-Path $PSScriptRoot 'callaudit.py') --bad 2>&1
$auditOk = ($LASTEXITCODE -eq 0)
Check 'every engine call signature matches the binary' $auditOk `
    (@($audit | Where-Object { $_ -match '^\s+\d+ agree|do not$|agree' }) -join '; ')

# --- 18. no crash the plugin log cannot see ----------------------------------------------
# ⚠ A FAIL-FAST LEAVES NOTHING IN THE PLUGIN LOG. A wrong __thiscall argument count skews the
# stack and surfaces as STATUS_STACK_BUFFER_OVERRUN (0xC0000409) inside some unrelated
# function, with no managed exception and no log line — this project logged exactly that twice
# without an explanation before tools/callaudit.py found the cause. The Application log is the
# only place it appears, so check it rather than trusting a quiet plugin log.
#
# Scoped to this run: an older crash in the log is history, not a regression (verify3 learned
# the same lesson from its own log checks).
$sinceRun = (Get-Date).AddMinutes(-30)
$eventCrashes = @(
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $sinceRun } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -like '*swkotor*' -or $_.Message -like '*dinput8*' })
Check 'no swkotor crash in the event log' ($eventCrashes.Count -eq 0) `
    $(if ($eventCrashes.Count -eq 0) { "clean since $($sinceRun.ToString('HH:mm'))" }
      else { ($eventCrashes | Select-Object -First 2 | ForEach-Object { ($_.Message -split "`n")[0] }) -join ' | ' })

# Leave the world as we found it, so the next run measures the same starting conditions.
CloseConversations $h1 $b1
CloseConversations $h2 $b2

''
'summary: {0} passed, {1} failed' -f ($results.Values | Where-Object Pass).Count, ($results.Values | Where-Object { -not $_.Pass }).Count
[VF]::CloseHandle($h1) | Out-Null
[VF]::CloseHandle($h2) | Out-Null
exit ($results.Values | Where-Object { -not $_.Pass }).Count
