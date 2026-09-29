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
  // Same technique as FindCreature, different vtable (CSWSDoor 0x007498B0 — see
  // src/KotorCoop.Plugin/CreatureFinder.cs's own DoorVtable). Door open-state is a byte at
  // door+0x2CC (Odyssey.GetDoorOpenState / DoorOpenState).
  public static long FindDoor(IntPtr h, uint objId){
    long addr=0; int sz=Marshal.SizeOf(typeof(MBI));
    while(addr<0x7FFFFFFF){ MBI m; if(VirtualQueryEx(h,new IntPtr(addr),out m,sz)==0) break;
      long size=(long)m.RegionSize; if(size<=0) break;
      if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
        var buf=R(h,(long)m.BaseAddress,(int)size);
        if(buf!=null) for(int i=0;i+8<=buf.Length;i+=4)
          if(BitConverter.ToUInt32(buf,i)==0x007498B0 && BitConverter.ToUInt32(buf,i+4)==objId)
            return (long)m.BaseAddress+i; }
      long next=(long)m.BaseAddress+size; if(next<=addr) break; addr=next; }
    return 0; }
  // Same technique again, CSWSPlaceable vtable (0x007494D0 —
  // src/KotorCoop.Plugin/CreatureFinder.cs's own PlaceableVtable). The container-sync
  // live-detector field is an int-wide "GUI open now" flag at placeable+0x338
  // (docs/research/container-sync.md §1.2, VERIFIED live 2026-08-22).
  public static long FindPlaceable(IntPtr h, uint objId){
    long addr=0; int sz=Marshal.SizeOf(typeof(MBI));
    while(addr<0x7FFFFFFF){ MBI m; if(VirtualQueryEx(h,new IntPtr(addr),out m,sz)==0) break;
      long size=(long)m.RegionSize; if(size<=0) break;
      if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
        var buf=R(h,(long)m.BaseAddress,(int)size);
        if(buf!=null) for(int i=0;i+8<=buf.Length;i+=4)
          if(BitConverter.ToUInt32(buf,i)==0x007494D0 && BitConverter.ToUInt32(buf,i+4)==objId)
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

# Drives the SAME agent<-plugin console channel blockpoke.ps1's own 'console' verb uses
# (0x9F8 command text, 0x9F4 command serial, 0xA58 reply serial) — the only way to run a
# co-op console verb (warp, opendoor, ...) unattended, since injected keystrokes never reach
# this game. Waits for the REPLY SERIAL to move, not for non-empty text, for the same reason
# blockpoke.ps1's own 'console' verb does: the reply field holds the previous answer until
# the next one lands.
function SendConsole($h, $b, [string]$cmdText) {
    $before = [VF]::U32($h, $b + 0xA58)
    $bytes = [byte[]]::new(96)
    $ascii = [Text.Encoding]::ASCII.GetBytes($cmdText)
    [Array]::Copy($ascii, $bytes, [Math]::Min($ascii.Length, 95))
    for ($i = 0; $i -lt 96; $i += 4) { [VF]::W32($h, $b + 0x9F8 + $i, [BitConverter]::ToInt32($bytes, $i)) | Out-Null }
    [VF]::W32($h, $b + 0x9F4, [int]([VF]::U32($h, $b + 0x9F4) + 1)) | Out-Null
    return WaitFor2 { ([VF]::U32($h, $b + 0xA58)) -ne $before } 10
}

# Groups every log line since a mark by (whole second, message text) and returns the worst
# (key, count) pair — the check for "no log line repeating >10x/s" this round's own task asked
# for. A real per-frame spam bug logs the exact SAME text every frame with nothing that varies
# (no serial, no counter), which is exactly what grouping on the literal message text catches;
# a line that legitimately differs each time it fires (an embedded counter, a changing id)
# never collides with itself here.
function WorstSpam($path, [int]$since) {
    $lines = NewLogLines $path $since
    $buckets = @{}
    foreach ($line in $lines) {
        if ($line -match '^\[(\d\d:\d\d:\d\d)\.\d+\]\s?(.*)$') {
            $key = "$($matches[1]) | $($matches[2])"
            if ($buckets.ContainsKey($key)) { $buckets[$key]++ } else { $buckets[$key] = 1 }
        }
    }
    if ($buckets.Count -eq 0) { return $null }
    return ($buckets.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1)
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

# The plugin log files are NOT rotated per run — they accumulate across every past demo
# session on this machine. A "did this ever happen" check must scope to what THIS run
# produced, or it reads history as if it were live evidence (this file's own header rule,
# applied here to the two standing-state checks below that scan for a line rather than
# waiting on one specific action).
$battery1 = LogLines $Log1
$battery2 = LogLines $Log2

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
    # The attack bit (mask 4) is cleared at spawn (MarkAsRemotePlayer) but re-permitted for the
    # combat-mirror swing visual while the peer is fighting (Odyssey.Combat PermitAttacking), so it
    # is reported, not required: it reads set whenever the baseline has the peer in combat.
    Check 'phantom damageable, not a sink' (($plot -eq 0) -and ($min1 -eq 1)) `
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

# --- 5b. journal DECREASE applies (protocol 31) --------------------------------------
# The whole point of the fix in docs/research/quest-sync.md: a target BELOW what the
# engine already holds used to be silently refused forever (bOverrideHigher hardcoded 0).
# Same tag, same slot, now asking for 10 after the check above left it at 20.
$decreaseMark = LogLines $Log2
$beforeDecrease = [VF]::U32($h2, $b2 + 0x99C)
[VF]::W32($h2, $b2 + 0x998, 10) | Out-Null
[VF]::W32($h2, $b2 + 0x974, [int]([VF]::U32($h2, $b2 + 0x974) + 1)) | Out-Null
Start-Sleep -Seconds 3
$afterDecrease = [VF]::U32($h2, $b2 + 0x99C)
$statusDecrease = [VF]::U32($h2, $b2 + 0x9A0)
Check 'journal DECREASE request serviced' (($afterDecrease -ne $beforeDecrease) -and ($statusDecrease -eq 0)) `
    "applied serial $beforeDecrease -> $afterDecrease, status $statusDecrease"
Check 'journal DECREASE logged by the plugin' (LogHasSince $Log2 'journal:' $decreaseMark) `
    ((Select-String -Path $Log2 -Pattern 'journal:' | Select-Object -Last 1).Line ?? 'no journal line')

# --- 5c. journal removal reaches the live engine call (journal_remove_sync=1, default) ----
# ⚠ ROUND 59: this section used to assert the OPPOSITE of what ships. The RemoveJournalQuestEntry
# native call (Odyssey.RemoveJournalEntry / CSWSJournal::Destroy) was "confirmed live 2026-08-22"
# (Odyssey.cs's own remarks) EARLIER THE SAME DAY as this round and promoted to
# `PluginConfig.ReadBool("journal_remove_sync", true)` — DEFAULT ON — in `JournalController.cs`,
# and `profiles/coop.ini.example` ships `journal_remove_sync=1` to match. This check still
# expected the OLD acknowledge-only default and failed for that reason alone, not a regression.
# Fixed to test what the current default actually does: the request is serviced AND reaches the
# live removal call. Set journal_remove_sync=0 in coop.ini to exercise the acknowledge-only path
# this check used to assert instead.
$removeMark = LogLines $Log2
$beforeRemove = [VF]::U32($h2, $b2 + 0x99C)
[VF]::W32($h2, $b2 + 0x998, [int]::MinValue) | Out-Null
[VF]::W32($h2, $b2 + 0x974, [int]([VF]::U32($h2, $b2 + 0x974) + 1)) | Out-Null
Start-Sleep -Seconds 3
$afterRemove = [VF]::U32($h2, $b2 + 0x99C)
$statusRemove = [VF]::U32($h2, $b2 + 0x9A0)
Check 'journal removal request serviced (default: reaches the live call)' `
    (($afterRemove -ne $beforeRemove) -and ($statusRemove -eq 0)) `
    "applied serial $beforeRemove -> $afterRemove, status $statusRemove"
Check 'journal removal logged as reaching the live engine call by default' `
    (LogHasSince $Log2 "removed on the session's authority \(journal_remove_sync\)" $removeMark) `
    ((Select-String -Path $Log2 -Pattern 'journal_remove_sync' | Select-Object -Last 1).Line ?? 'no removal line')

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
    # PLAYER (0) while it is a live target; a non-PLAYER faction is the deliberate Neutral hold
    # (local_combat + damage-dealt, stealth, menu guard - PhantomController.ApplyTargetable), which
    # the stock config runs, so any of them is the designed state; unkillable (min1hp) is the invariant.
    Check 'phantom is a targetable decoy' (($faction -in 0, 2, 5) -and ([VF]::U32($h1, $c1 + 0x200) -eq 1)) `
        "faction $faction (0 player, 2/5 neutral hold), min1hp 1"
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
# At LEAST the forwarded 3: enemy damage is scaled by player count (115% for two) and the same
# fight can land other hits in the wait, and the tutorial Min1HP pin floors the PC at 1, so an
# exact -eq 3 flaked between 3, 5 and 0 lost. The exact forwarded amount is asserted one check up.
Check 'the peer really loses those hit points' ((($pcBefore - $pcAfter) -ge 3) -or ($pcAfter -le 1)) `
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
    $slot = $b2 + 0x6800 + ($i * 0x28)
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
    $slot = $b2 + 0x6800 + ($i * 0x28)
    $id = [VF]::U32($h2, $slot)
    if ($id -eq 0) { continue }
    # ⚠ SKIP AN ENEMY THAT IS ALREADY DOWN TO 1. The check asks whether a client's hit costs
    # the enemy hit points, and a creature sitting at 1 cannot answer it — an earlier run of
    # this same battery leaves bodies behind, and picking one gave "hp 1 -> 1", which looks
    # like a broken channel and is nothing of the kind. Judge on the HOST's creature, which
    # is the authority.
    $cand = [VF]::FindCreature($h1, $id)
    if ($cand -eq 0 -or [VF]::I16($h1, $cand + 0xDC) -le 1) { continue }

    # ⚠ ROUND 58: the census slot position ($h2, $slot+4/+8) is a mirrored SNAPSHOT and can go
    # stale — measured picking a target the census placed 57 units away while its real, live
    # engine position was 128 units away on the far side of the map, so the attack gave up
    # immediately (no walkable path in the window) and the check failed for a reason that had
    # nothing to do with the damage channel under test. $cand is already the HOST's own live
    # creature (resolved above), so read its position straight out of the host's engine
    # (Odyssey.ObjPosition = +0x90) instead — the same "judge distance from what the engine
    # says NOW, not a snapshot" discipline section 10 uses for the attacker's own position.
    $ex = [VF]::F32($h1, $cand + 0x90)
    $ey = [VF]::F32($h1, $cand + 0x94)
    $d = [math]::Sqrt([math]::Pow($ex - $cx, 2) + [math]::Pow($ey - $cy, 2))
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
    # Experience arithmetic is done as UNSIGNED 32-bit and written back through this: a fresh
    # Endar Spire PC can read 0xFFFFFFEC (-20), and `[int]4294967281` throws instead of wrapping,
    # which made every XP write below a silent no-op.
    function AsI32([uint64]$v) { [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]($v -band 0xFFFFFFFF)), 0) }
    $goldTarget = $goldBefore + 137
    $xpTarget = ([uint64]$xpBefore + 5) -band 0xFFFFFFFF   # small: crossing a level threshold lights a modal prompt

    [VF]::W32($h1, $partyTable1 + 0xFC, [int]$goldTarget) | Out-Null
    [VF]::W32($h1, $statsHost + 0x68, (AsI32 $xpTarget)) | Out-Null

    # One heavy pump a second on each side plus a wire hop; 25 s is many times over, and
    # both fields travel in the same snapshot so one wait covers them.
    WaitFor2 {
        ([VF]::U32($h2, $statsPeer + 0x68) -eq $xpTarget)
    } 25 | Out-Null

    $goldSeen = [VF]::U32($h2, $partyTable2 + 0xFC)
    $xpSeen = [VF]::U32($h2, $statsPeer + 0x68)

    # credits_mode defaults to PERSONAL (each player keeps their own purse, CoopConfig.CreditsPersonal),
    # so on a stock install the honest expectation is that the client's gold does NOT follow. Read the
    # mode from the client's coop.ini (credits_mode, else inventory_mode, else personal).
    $clientIni = Join-Path (Split-Path (Get-Process -Id $Client2).Path -Parent) 'coop.ini'
    $iniText = if (Test-Path $clientIni) { Get-Content $clientIni -Raw } else { '' }
    $creditsShared = ($iniText -match '(?m)^\s*credits_mode\s*=\s*shared') -or
        (($iniText -notmatch '(?m)^\s*credits_mode\s*=') -and ($iniText -match '(?m)^\s*inventory_mode\s*=\s*shared'))
    if ($creditsShared) {
        Check 'party gold reaches the other instance' ($goldSeen -eq $goldTarget) `
            "host $goldBefore -> $goldTarget, client's engine reads $goldSeen"
    }
    else {
        Check "party gold stays personal (credits_mode default)" ($goldSeen -ne $goldTarget) `
            "host $goldBefore -> $goldTarget, client's engine reads $goldSeen"
    }
    Check 'PC experience rises on the other instance' ($xpSeen -eq $xpTarget) `
        "host $xpBefore -> $xpTarget, client's engine reads $xpSeen"

    # The guard, and the whole reason the channel changed: a client that has earned XP the
    # host has not — plot XP from a conversation only it is having — must not have it stamped
    # back down. Drop the host well below and give it longer than a merge would need.
    [VF]::W32($h1, $statsHost + 0x68, (AsI32 ($xpBefore + 0xFFFFFFEC))) | Out-Null
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
    [VF]::W32($h1, $statsHost + 0x68, (AsI32 $xpBefore)) | Out-Null
    [VF]::W32($h2, $statsPeer + 0x68, (AsI32 $xpBefore)) | Out-Null
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

# --- 19. the host still controls its companions --------------------------------------------
# Round 58 (shared-state-audit.md): CSWPartyTable counts COMPANIONS only (Trask -> 1); CSWParty
# (the struct a real door's IsPartyTogether actually reads) counts the PC too (PC + Trask -> 2).
# An earlier build clamped CSWParty down to the table's own count on EVERY instance, which on
# the HOST dropped its own Trask out of the struct the control/portrait/follow logic reads —
# "the host loses control of its own companions". party_together_fix is now client-only
# (gated on TryGetHostPartyRoster, true only for a client); this check is the regression test
# that class of bug would have caught: the host's own table/CSWParty counts must differ by
# exactly one (the PC), the companion (if any) must be commandable, and the client-only clamp
# line must never have fired against the HOST's own log.
function SwPartyCount($h) {
    $am = [VF]::U32($h, 0x7A39FC)
    if ($am -eq 0) { return -1 }
    $cli = [VF]::U32($h, $am + 4)
    if ($cli -eq 0) { return -1 }
    $cliInt = [VF]::U32($h, $cli + 4)
    if ($cliInt -eq 0) { return -1 }
    $party = [VF]::U32($h, $cliInt + 0x270)
    if ($party -eq 0) { return -1 }
    return [int]([VF]::U32($h, $party))
}

$hostTableCount = if ($partyTable1) { [int]([VF]::U32($h1, $partyTable1 + 0x00)) } else { -1 }
$hostSwCount = SwPartyCount $h1
$companionOk = $true
$companionDetail = 'no companion in the host party'
if ($hostTableCount -gt 0) {
    $npcSlot = [VF]::U32($h1, $partyTable1 + 0x04)                       # first active companion's NPC slot index
    $companionObjId = [VF]::U32($h1, $partyTable1 + 0x0C + ($npcSlot * 4))
    $companionAddr = if ($companionObjId -ne 0) { [VF]::FindCreature($h1, $companionObjId) } else { 0 }
    if ($companionAddr -ne 0) {
        $commandable = [VF]::U32($h1, $companionAddr + 0xE8)             # ObjCommandable
        $companionOk = ($commandable -eq 1)
        $companionDetail = "companion 0x{0:X} (npc slot {1}) commandable={2}" -f $companionObjId, $npcSlot, $commandable
    }
    else {
        $companionOk = $false
        $companionDetail = "companion object 0x{0:X} (npc slot {1}) not found in the host's engine" -f $companionObjId, $npcSlot
    }
}
# ⚠ Scoped to THIS run ($battery1, taken right after the blocks were found), NOT the whole
# file. The plugin log is never rotated between runs — it accumulates every past session on
# this machine — so a whole-file scan (check 6's crash scan does this deliberately, because a
# crash is never expected at all) reads yesterday's already-fixed bug as if it were live
# evidence. Measured doing exactly that this round: a clean rig still failed on clamp lines
# timestamped hours before this battery ran.
$hostClamped = LogHasSince $Log1 'party together:.*clamping' $battery1
Check 'the host still controls its companions' `
    (($hostTableCount -ge 0) -and ($hostSwCount -eq ($hostTableCount + 1)) -and $companionOk -and -not $hostClamped) `
    ("table {0} companion(s), CSWParty {1} (PC+companions), {2}, host clamped this run: {3}" -f `
        $hostTableCount, $hostSwCount, $companionDetail, $hostClamped)

# --- 20. a door opened on the client opens on the host --------------------------------------
# The symmetric-doors channel (symmetry.md: "either peer's own engine-native open/unlock/swing
# is detected locally... applied by DoorController.Apply on every instance"), driven end to end
# through the same 'opendoor' verb shared-state-audit.md round 57 built for exactly this ("one
# usedoor refuses by design"): blank picks the nearest door, so no tag needs to be known ahead
# of time. The oracle is the HOST's own live door-open-state byte (door+0x2CC), read off the
# host's engine directly, not either side's bookkeeping.
$doorMark = LogLines $Log2
$doorReplied = SendConsole $h2 $b2 'opendoor'
Start-Sleep -Seconds 1
$doorLine = @(NewLogLines $Log2 $doorMark | Where-Object { $_ -match 'door: opened 0x([0-9A-Fa-f]+) \(state (\d+) -> (\d+)\)' } | Select-Object -First 1)
$doorId = 0
$doorOpenedOnClient = $false
if ($doorLine.Count -gt 0 -and $doorLine[0] -match 'door: opened 0x([0-9A-Fa-f]+) \(state (\d+) -> (\d+)\)') {
    $doorId = [Convert]::ToUInt32($matches[1], 16)
    $doorOpenedOnClient = ([int]$matches[3] -ne [int]$matches[2])
}
$hostDoorAddr = 0
$hostDoorState = 0
if ($doorId -ne 0) {
    # Poll the STATE itself, not just the address: round 57 measured ~1.8s mirror latency for a
    # nearby door, so a single read taken the instant the address resolves can catch state=0
    # before DoorController.Apply's mirror write lands. 20s is generous over that.
    WaitFor2 {
        if ($script:hostDoorAddr -eq 0) { $script:hostDoorAddr = [VF]::FindDoor($h1, $doorId) }
        if ($script:hostDoorAddr -ne 0) { $script:hostDoorState = [VF]::U8($h1, $script:hostDoorAddr + 0x2CC) }
        $script:hostDoorState -gt 0
    } 20 | Out-Null
}
# Distance diagnostic: SetDoorOpenState's engine call is DEFERRED (bDeferred=1) — it commits
# once the door's own animation runs, which round 59 measured NOT happening for a door far
# outside the reading instance's own active radius (host 114 units from a door the CLIENT'S
# "nearest door" sweep picked at 26 units from itself — current AND pending both stayed 0 for
# 7+ minutes, well past DoorController's own 25-pass commit-fallback). Reported here so a
# future FAIL reads as "known distance-dependent gap" rather than "check is broken".
$hostDoorDist = -1.0
if ($hostDoorAddr -ne 0) {
    $hx = [VF]::F32($h1, $hostDoorAddr + 0x90); $hy = [VF]::F32($h1, $hostDoorAddr + 0x94)
    $hpx = [VF]::F32($h1, $b1 + 0x10); $hpy = [VF]::F32($h1, $b1 + 0x14)
    $hostDoorDist = [math]::Sqrt([math]::Pow($hx - $hpx, 2) + [math]::Pow($hy - $hpy, 2))
}
Check 'a door opened on the client opens on the host' `
    (($doorId -ne 0) -and $doorOpenedOnClient -and ($hostDoorAddr -ne 0) -and ($hostDoorState -gt 0)) `
    $(if (-not $doorReplied) { 'the opendoor console command got no reply within 10s' }
      elseif ($doorId -eq 0) { "no 'door: opened' line seen on the client (sweep may have found no door nearby)" }
      elseif (-not $doorOpenedOnClient) { "door 0x{0:X} did not change state on the client" -f $doorId }
      elseif ($hostDoorAddr -eq 0) { "door 0x{0:X} not found in the host's engine" -f $doorId }
      elseif ($hostDoorState -eq 0) { "door 0x{0:X}: client opened it, host STILL reads open-state 0 after 20s (host is {1:F0} units from the door — see this check's own comment on the deferred-commit/distance gap)" -f $doorId, $hostDoorDist }
      else { "door 0x{0:X}: client opened it, host reads open-state {1} ({2:F0} units from the door)" -f $doorId, $hostDoorState, $hostDoorDist })

# --- 20b. a container opened on the client is held and shown open on the host ---------------
# docs/research/container-sync.md §5 (live-verified 2026-08-22, protocol 37): the same
# 'useplaceable' staging verb the door check above already relies on, for a placeable instead.
# The oracle is engine state on BOTH sides, not either instance's own bookkeeping — the CLIENT's
# own placeable+0x338 (Odyssey.IsPlaceableOpen's field) confirms a REAL local open happened, and
# the HOST's mirrored copy of the SAME field plus its own interaction-lock slot (kind=2,
# Container) confirm the mirror/lock reached the other side, exactly the way check 20 confirms a
# door's open-state crossed rather than trusting a log line alone.
#
# Deliberately the CLIENT opening and the HOST observing, the opposite direction from check 20's
# door (client opens, host observes) — this pass's own live testing found the mechanism
# symmetric, but the two checks together cover both roles across the whole battery.
#
# Placed here (20b), not after the later checks: check 21 below self-warps the client to another
# module and back, which repositions the player somewhere inside END_M01AA on reload — measured
# live to land 116 units from 0xC6, well past PlaceableUseRange (15). Running before that warp is
# what lets this check use the save's own natural starting proximity rather than needing its own
# `tp`.
#
# ⚠ NAMES THE PLACEABLE EXPLICITLY (0xC6), the same way check 11 hardcodes 'end_trask' —
# demo.ps1's own starting save, not a general module. NOT the bare 'useplaceable' (nearest
# container): this pass's own live testing found the bare form's has_inventory (+0x334) filter
# reads 0 for every real container it tried, 0xC6 included — a live-confirmed gap, documented
# in container-sync.md §5.7, not fixed there. Naming the id directly bypasses that filter
# entirely (CheatController.UsePlaceable resolves an explicit id without consulting
# HasInventory), which is why this check can pass reliably while that gap stands.
#
# ⚠ TELEPORTS THE CLIENT ONTO 0xC6 FIRST, unlike the door check above. A door's state toggles
# through a non-walking primitive, so that check's own proximity only affects how fast the
# MIRROR is observed (its own documented distance/latency gap). A container's real
# `AIActionUseObject`/`OpenInventory` path has no such shortcut — `PlaceableUseRange` (15 units)
# hard-refuses a distant attempt before it ever reaches the engine, and by this point in the
# battery real combat earlier in the run (checks 9/13) has already dragged the party well away
# from the spawn point (measured live: 116-142 units from 0xC6 with no `tp`, comfortably outside
# range) — so without this, the check would fail on distance almost every time, not on anything
# this pass is meant to verify. 0xC6's own position (10.8, 21.87) was read live off the placeable
# itself (+0x90 Vec3) and does not move.
SendConsole $h2 $b2 'tp 10.8 21.87' | Out-Null
Start-Sleep -Seconds 1
$cUseMark = LogLines $Log2
$cUseReplied = SendConsole $h2 $b2 'useplaceable 0xC6'
Start-Sleep -Seconds 1
$cUseLine = @(NewLogLines $Log2 $cUseMark | Where-Object { $_ -match 'placeable: use-object sent for 0x([0-9A-Fa-f]+)' } | Select-Object -First 1)
$containerId = 0
if ($cUseLine.Count -gt 0 -and $cUseLine[0] -match 'placeable: use-object sent for 0x([0-9A-Fa-f]+)') {
    $containerId = [Convert]::ToUInt32($matches[1], 16)
}

$clientPlaceableAddr = 0
$clientOpened = $false
if ($containerId -ne 0) {
    # A real open queues an AI walk-and-use action first (check 20's own door-distance comment
    # notes the same kind of delay for doors); this pass's own live testing measured up to ~15s
    # for a ~9m walk, so this is generous over that, not arbitrary.
    $clientOpened = WaitFor2 {
        if ($script:clientPlaceableAddr -eq 0) { $script:clientPlaceableAddr = [VF]::FindPlaceable($h2, $containerId) }
        ($script:clientPlaceableAddr -ne 0) -and (([VF]::U32($h2, $script:clientPlaceableAddr + 0x338)) -ne 0)
    } 20
    $clientPlaceableAddr = $script:clientPlaceableAddr
}

$hostPlaceableAddr = 0
$hostMirroredOpen = $false
$hostLockCount = 0
$hostHoldsIt = $false
if ($clientOpened) {
    $hostMirroredOpen = WaitFor2 {
        if ($script:hostPlaceableAddr -eq 0) { $script:hostPlaceableAddr = [VF]::FindPlaceable($h1, $containerId) }
        ($script:hostPlaceableAddr -ne 0) -and (([VF]::U32($h1, $script:hostPlaceableAddr + 0x338)) -ne 0)
    } 10
    $hostPlaceableAddr = $script:hostPlaceableAddr
    $hostLockCount = [VF]::U32($h1, $b1 + 0x1358)
    for ($i = 0; $i -lt $hostLockCount -and $i -lt 4; $i++) {
        $lockKind = [VF]::U32($h1, $b1 + 0x1360 + ($i * 0x28) + 4)
        $lockTargetId = [VF]::U32($h1, $b1 + 0x3F0C + ($i * 0x04))
        if ($lockKind -eq 2 -and $lockTargetId -eq $containerId) { $hostHoldsIt = $true }
    }
}

Check 'a container opened on the client is held and shown open on the host' `
    ($clientOpened -and $hostMirroredOpen -and $hostHoldsIt) `
    $(if (-not $cUseReplied) { "the useplaceable console command got no reply within 10s" }
      elseif ($containerId -eq 0) { "no 'placeable: use-object sent' line seen on the client for 0xC6 (wrong save/module, or too far — 0xC6 is demo.ps1's own starting footlocker, END_M01AA)" }
      elseif (-not $clientOpened) { "container 0x{0:X}: client's own +0x338 never flipped to 1 within 20s (too far to walk to, or not a real container)" -f $containerId }
      elseif (-not $hostMirroredOpen) { "container 0x{0:X}: client opened it, host's own copy +0x338 never mirrored to 1 within 10s" -f $containerId }
      elseif (-not $hostHoldsIt) { "container 0x{0:X}: host's copy mirrored open but its interaction-lock list (count={1}) has no Container-kind entry naming it" -f $containerId, $hostLockCount }
      else { "container 0x{0:X}: client opened it for real, host mirrors it open and holds the lock (peer-held)" -f $containerId })

# Leave the world closed rather than open, so the next run does not inherit a held container —
# closeplaceable is idempotent (a no-op, cleanly refused, on an already-closed target).
if ($containerId -ne 0) {
    SendConsole $h2 $b2 ("closeplaceable 0x{0:X}" -f $containerId) | Out-Null
}

# --- 21. the client's party roster survives a transition ------------------------------------
# party-reconciliation's own host-roster valid bit (SharedBlock.PartyRosterValid, 0x8000_0000
# of OffsetHostPartyRoster/0x3304) is the client's only signal that it has something to
# reconcile against; ReleaseSession already proved it can be left set incorrectly (round 56's
# "a disconnected agent... nothing lowered it on teardown"). This checks the OTHER direction: a
# real module teardown/rebuild must not silently drop it. Round-trips through a self-warp (the
# module the client is already in) so the check restores exactly where it found the session.
$hostRosterBefore = [VF]::U32($h2, $b2 + 0x3304)
$validBefore = ($hostRosterBefore -band 0x80000000) -ne 0
$moduleBefore = [VF]::Str($h2, $b2 + 0x20, 32)
if ($validBefore -and $moduleBefore.Length -gt 0) {
    $warpReplied = SendConsole $h2 $b2 "warp $moduleBefore"
    Start-Sleep -Seconds 2
    $reloaded = WaitFor2 { ([VF]::Str($h2, $b2 + 0x20, 32)) -eq $moduleBefore } 40
    Start-Sleep -Seconds 5
    $hostRosterAfter = [VF]::U32($h2, $b2 + 0x3304)
    $validAfter = ($hostRosterAfter -band 0x80000000) -ne 0
    Check 'the client party roster survives a transition' $validAfter `
        ("module '{0}', warp replied {1}, reloaded {2}, roster before 0x{3:X8} (valid={4}) after 0x{5:X8} (valid={6})" `
            -f $moduleBefore, $warpReplied, $reloaded, $hostRosterBefore, $validBefore, $hostRosterAfter, $validAfter)
}
else {
    Check 'the client party roster survives a transition' $false `
        ("no valid host roster to begin with (0x{0:X8}), module '{1}' — nothing to round-trip" -f $hostRosterBefore, $moduleBefore)
}

# --- 22. no per-frame spam -------------------------------------------------------------------
# What this week's regressions would have caught: a bug that logs the SAME line every frame
# (no serial, no counter, nothing that varies) rather than once on a state CHANGE — the pattern
# every LEVEL-shaped channel in this codebase (doors, roster, pause, ...) is supposed to avoid
# by design (see PartyRosterController's own "A LEVEL, NOT EDGES" comment). Scoped to the whole
# session log, same as check 6's crash scan: this is a standing-quality check, not tied to one
# action this run triggers.
# The plugin log appends across sessions, so scope to the CURRENT process: from its last
# "plugin loaded into pid" banner, not every burst any earlier run ever logged.
function LastSessionStart($path) {
    $n = 0; $at = 0
    foreach ($l in [IO.File]::ReadLines($path)) { if ($l -like '*=== KotorCoop plugin loaded into pid*') { $at = $n }; $n++ }
    return $at
}
$worst1 = WorstSpam $Log1 (LastSessionStart $Log1)
$worst2 = WorstSpam $Log2 (LastSessionStart $Log2)
$spamOk1 = ($null -eq $worst1) -or ($worst1.Value -le 10)
$spamOk2 = ($null -eq $worst2) -or ($worst2.Value -le 10)
Check 'no per-frame spam' ($spamOk1 -and $spamOk2) `
    ("host worst: {0}x in one second ({1}); client worst: {2}x in one second ({3})" -f `
        $(if ($worst1) { $worst1.Value } else { 0 }), $(if ($worst1) { $worst1.Key } else { 'n/a' }),
        $(if ($worst2) { $worst2.Value } else { 0 }), $(if ($worst2) { $worst2.Key } else { 'n/a' }))

# Leave the world as we found it, so the next run measures the same starting conditions.
CloseConversations $h1 $b1
CloseConversations $h2 $b2

''
'summary: {0} passed, {1} failed' -f ($results.Values | Where-Object Pass).Count, ($results.Values | Where-Object { -not $_.Pass }).Count
[VF]::CloseHandle($h1) | Out-Null
[VF]::CloseHandle($h2) | Out-Null
exit ($results.Values | Where-Object { -not $_.Pass }).Count
