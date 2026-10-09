# Reads and writes named fields of the plugin's shared block in a running KOTOR.
#
# Exists for scripted verification: the interesting inputs (a travel request, a journal
# request, the remote-pause flag) are agent->plugin fields, so writing them directly
# exercises the plugin's half of a feature without needing a peer, and reading the
# plugin->agent halves confirms the result. Offsets mirror the current SharedBlock.cs layout.
#
#   pwsh tools\blockpoke.ps1 -ProcId 1234 state
#   pwsh tools\blockpoke.ps1 -ProcId 1234 travel end_m01aa
#   pwsh tools\blockpoke.ps1 -ProcId 1234 journal k_swg_bastila 20
#   pwsh tools\blockpoke.ps1 -ProcId 1234 pause 1
#   pwsh tools\blockpoke.ps1 -ProcId 1234 cheat god 1
#   pwsh tools\blockpoke.ps1 -ProcId 1234 cheat slay 42   # decimal object id
#   pwsh tools\blockpoke.ps1 -ProcId 1234 cheat           # read the answer + detail
#   pwsh tools\blockpoke.ps1 -ProcId 1234 console 'netsim blackout 10'
#   pwsh tools\blockpoke.ps1 -ProcId 1234 console         # read the latest agent reply
#   pwsh tools\blockpoke.ps1 -ProcId 1234 item            # the cheat channel's item fields
#
# The `console` verb writes the same agent<-plugin channel the in-game ~ console writes,
# which is the ONLY way to drive a co-op verb unattended: injected keystrokes never reach
# this game (docs/live-verification.md), so without this every console-routed feature —
# netsim, the staging cheats, status — was live-testable by a human and nobody else.
#
# The item-identity harness rides that same channel, which is why it has no verb of its own
# here (its arguments are named, and this script's Arg1/Arg2 pair cannot carry them):
#
#   console 'items'                              list live items with ids and identities
#   console 'setitem 0x131 stack=20 charges=3'   make one worth sending
#   console 'dropitem 0x131'                     replicate it, identity and all
#   console 'item 0x131'                         read one back (run this on the RECEIVER)
#
# `item` below is the raw view of what the last setitem put on the wire, for when the answer
# and the request need to be compared byte for byte.
param(
    [string]$ProcessName = 'swkotor',
    [int]$ProcId = 0,
    [Parameter(Position = 0)][ValidateSet('state', 'travel', 'announce', 'journal', 'pause', 'browser', 'death', 'hitphantom', 'combat', 'ledger', 'threat', 'talk', 'walk', 'dialogue', 'attack', 'encounters', 'triggers', 'cheat', 'console', 'item', 'chargen', 'menuload', 'doors')]
    [string]$Command = 'state',
    [Parameter(Position = 1)][string]$Arg1 = '',
    [Parameter(Position = 2)][int]$Arg2 = 0
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class BP {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern bool WriteProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr a, out MBI m, int l);
  [StructLayout(LayoutKind.Sequential)] public struct MBI {
    public IntPtr BaseAddress, AllocationBase; public uint AllocationProtect, __a1;
    public UIntPtr RegionSize; public uint State, Protect, Type, __a2; }
  public static IntPtr Open(int pid){ return OpenProcess(0x0438, false, (uint)pid); }
  public static byte[] Read(IntPtr h, long a, int n){
    byte[] b = new byte[n]; int r;
    if(!ReadProcessMemory(h, new IntPtr(a), b, n, out r) || r != n) return null;
    return b; }
  public static bool Write(IntPtr h, long a, byte[] b){
    int r; return WriteProcessMemory(h, new IntPtr(a), b, b.Length, out r) && r == b.Length; }
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

$proc = if ($ProcId -ne 0) { Get-Process -Id $ProcId -ErrorAction SilentlyContinue }
        else { Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1 }
if (-not $proc) { Write-Error 'target process not running'; exit 1 }

$h = [BP]::Open($proc.Id)
$sig = [byte[]](0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31, 0,0,0,0, 0x01,0xEE,0xFF,0xC0)
$block = [BP]::FindBlock($h, $sig)
if ($block -eq 0) { Write-Error 'plugin shared block not found'; exit 1 }

function U32($off) {
    $b = [BP]::Read($h, $block + $off, 4)
    if ($null -eq $b) { return [uint32]0xFFFFFFFF } else { [BitConverter]::ToUInt32($b, 0) }
}
function WU32($off, [uint32]$value) { [BP]::Write($h, $block + $off, [BitConverter]::GetBytes($value)) | Out-Null }
function WF32($off, [float]$value) { [BP]::Write($h, $block + $off, [BitConverter]::GetBytes($value)) | Out-Null }
function WAscii($off, [string]$text, [int]$cap) {
    $bytes = [byte[]]::new($cap)
    $ascii = [Text.Encoding]::ASCII.GetBytes($text)
    [Array]::Copy($ascii, $bytes, [Math]::Min($ascii.Length, $cap - 1))
    [BP]::Write($h, $block + $off, $bytes) | Out-Null
}
function Ascii($off, [int]$cap) {
    $b = [BP]::Read($h, $block + $off, $cap)
    if ($null -eq $b) { return '?' }
    $n = [Array]::IndexOf($b, [byte]0); if ($n -lt 0) { $n = $cap }
    [Text.Encoding]::ASCII.GetString($b, 0, $n)
}

$version = U32 0x08
"pid $($proc.Id)  block 0x{0:X}  version {1}" -f $block, $version

switch ($Command) {
    'state' {
        "  frames {0:N0}  phantoms {1}  remotes {2}  command {3}" -f (U32 0x250), (U32 0x25C), (U32 0x48), (U32 0x254)
        "  travel:  serial {0}  applied {1}  status {2}  module '{3}'" -f (U32 0x920), (U32 0x964), (U32 0x968), (Ascii 0x924 32)
        "  pause:   desired {0}  applied {1}" -f (U32 0x96C), (U32 0x970)
        "  journal: serial {0}  applied {1}  status {2}  tag '{3}' state {4}" -f `
            (U32 0x974), (U32 0x99C), (U32 0x9A0), (Ascii 0x978 32), (U32 0x998)
    }
    'doors' {
        # The agent's desired door table for this module (SharedBlock.World: count 0x690,
        # 32 slots of 8 bytes at 0x698: id, open state, unlocked, apply sequence).
        $count = U32 0x690
        "  desired doors: $count"
        for ($i = 0; $i -lt [Math]::Min($count, 32); $i++) {
            $b = [BP]::Read($h, $block + 0x698 + $i * 8, 8)
            "  slot {0,2}: door 0x{1:X}  open {2}  unlocked {3}  applied seq {4}" -f $i, [BitConverter]::ToUInt32($b, 0), $b[4], $b[5], $b[7]
        }
    }
    'menuload' {
        if ($Arg1 -ne 'read') {
            WU32 0x3F24 1
            # Raw probes model automatic requests; never inherit an earlier typed load.
            WU32 0x8634 0
            if ($version -ge 95) {
                $intent = U32 0xC400
                if (($intent -band 1) -ne 0) { throw 'New Game intent is being published; request not filed.' }
                WU32 0xC418 $intent
                if ((U32 0xC400) -ne $intent) { throw 'New Game intent changed; request not filed.' }
            }
            WU32 0x9A4 ((U32 0x9A4) + 1)
            "mid-session load request filed: serial $(U32 0x9A4)"
        }
        "  menuload: serial {0}  applied {1}  status {2}" -f (U32 0x9A4), (U32 0x9A8), (U32 0x9AC)
    }
    'announce' {
        # Isolated metadata probe: announces a destination without moving this game.
        if ($Arg1.Length -eq 0) { Write-Error 'announce needs a module name'; exit 1 }
        WAscii 0x38E8 $Arg1 32
        WAscii 0x3908 '' 32
        WU32 0x38E4 ((U32 0x38E4) + 1)
        "transition metadata announced: module '$Arg1' serial $(U32 0x38E4)"
    }
    'travel' {
        if ($Arg1.Length -eq 0) { Write-Error 'travel needs a module name'; exit 1 }
        WAscii 0x924 $Arg1 32
        WAscii 0x944 '' 32
        WU32 0x8630 0
        WU32 0x920 ((U32 0x920) + 1)
        "travel request queued: module '$Arg1' serial $(U32 0x920)"
    }
    'journal' {
        if ($Arg1.Length -eq 0) { Write-Error 'journal needs a quest tag'; exit 1 }
        WAscii 0x978 $Arg1 32
        WU32 0x998 ([uint32]$Arg2)
        WU32 0x974 ((U32 0x974) + 1)
        "journal request queued: '$Arg1' -> $Arg2 serial $(U32 0x974)"
    }
    'pause' {
        WU32 0x96C ([uint32]$Arg2)
        if ($Arg1 -match '^\d+$') { WU32 0x96C ([uint32]$Arg1) }
        "remote-pause desired = $(U32 0x96C)"
    }
    'death' {
        # `death` prints the co-op death state; `death revive` asks the plugin to stand the
        # local PC back up (the same request a peer's rescue sends).
        if ($Arg1 -eq 'revive') {
            WU32 0x1040 ([uint32]$Arg2)
            WU32 0x1034 ((U32 0x1034) + 1)
            "revive requested (serial $(U32 0x1034), hp $(U32 0x1040))"
        }
        "  downed {0} (serial {1})  gameOverSerial {2}" -f (U32 0x1020), (U32 0x1024), (U32 0x1028)
        "  suppress: gameOver {0}  swap {1}" -f (U32 0x102C), (U32 0x1030)
        "  revive:   serial {0}  applied {1}  status {2}  hp {3}" -f `
            (U32 0x1034), (U32 0x1038), (U32 0x103C), (U32 0x1040)
    }
    'console' {
        # Bump the serial LAST: the agent polls it and reads the text behind it, so a
        # half-written command must never be visible under a serial that says "ready".
        if ($Arg1.Length -gt 0) {
            $before = U32 0xA58
            WAscii 0x9F8 $Arg1 96
            WU32 0x9F4 ((U32 0x9F4) + 1)

            # Wait for a REPLY SERIAL CHANGE rather than for non-empty text: the reply field
            # holds the previous answer until the next one lands, so reading text alone
            # cheerfully reports the last command's result as if it were this one's.
            $deadline = (Get-Date).AddSeconds(6)
            while ((Get-Date) -lt $deadline -and (U32 0xA58) -eq $before) { Start-Sleep -Milliseconds 100 }

            if ((U32 0xA58) -eq $before) {
                "  console: sent '{0}' (serial {1}) - NO REPLY within 6s" -f $Arg1, (U32 0x9F4)
            } else {
                "  console: sent '{0}' (serial {1})" -f $Arg1, (U32 0x9F4)
            }
        }

        "  console: cmd serial {0}  reply serial {1}" -f (U32 0x9F4), (U32 0xA58)
        "  last command: '{0}'" -f (Ascii 0x9F8 96)
        "  reply:        '{0}'" -f (Ascii 0xA5C 96)
    }
    'chargen' {
        # The character-creation drive verbs, poked straight into the plugin's own request
        # region (SharedBlock 0x4790..0x47A0) rather than through the agent's ~console channel.
        #
        # Why direct: every one of these is only meaningful AT THE MAIN MENU, and the agent's
        # console reply channel does not answer there on this rig - verified with `status`, a
        # long-standing verb, which is equally silent at the menu. The plugin services this
        # region from MenuFrame, which does run there. Same "kind before serial" ordering the
        # agent uses.
        #
        #   blockpoke -ProcId N chargen newgame
        #   blockpoke -ProcId N chargen quick 2      (class index 0-5)
        #   blockpoke -ProcId N chargen accept
        #   blockpoke -ProcId N chargen              (read the state back)
        $kinds = @{ 'newgame' = 1; 'quick' = 2; 'accept' = 3 }

        if ($Arg1.Length -gt 0) {
            if (-not $kinds.ContainsKey($Arg1)) {
                "  chargen: unknown verb '{0}' (newgame|quick|accept)" -f $Arg1
            }
            else {
                WU32 0x4794 ([uint32]($kinds[$Arg1] -bor ($Arg2 -shl 16)))
                $serial = (U32 0x4790) + 1
                WU32 0x4790 $serial

                $deadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $deadline -and (U32 0x4798) -ne $serial) { Start-Sleep -Milliseconds 100 }

                if ((U32 0x4798) -ne $serial) {
                    "  chargen: sent '{0}' (serial {1}) - the plugin did not answer within 10s" -f $Arg1, $serial
                } else {
                    $status = U32 0x479C
                    if ($status -eq 0) { "  chargen: '{0}' done" -f $Arg1 }
                    else { "  chargen: '{0}' refused (failure {1}; the reason is in kotorcoop-plugin.log)" -f $Arg1, $status }
                }
            }
        }

        "  chargen drive: request {0}  applied {1}  status {2}" -f (U32 0x4790), (U32 0x4798), (U32 0x479C)
        "  newchar capture: request {0}  applied {1}  state {2} (0 idle/1 armed/2 captured/3 failed)  bytes {3}" -f (U32 0x4780), (U32 0x4784), (U32 0x4788), (U32 0x478C)
    }
    'item' {
        # The raw item-identity half of the cheat channel (SharedBlock version 41,
        # 0x33B0..0x33C0). 4294967295 in a field means "the request left this one alone" —
        # which is not the same as zero, and confusing the two is how a poked stack would
        # silently empty a medpac's charges.
        #
        # Also prints the plugin's answer, because for setitem the answer IS the measurement:
        # it reports what the item reads AFTER the write, not what the write asked for.
        $status = U32 0x3368
        if ($status -gt 0x7FFFFFFF) { $status = [int64]$status - 4294967296 }

        "  request: kind {0}  target 0x{1:X}  stack {2}" -f (U32 0x332C), (U32 0x3330), (U32 0x3334)
        "  fields:  charges {0}  maxCharges {1}  upgrades 0x{2:X}  flags 0x{3:X}" -f `
            (U32 0x33B0), (U32 0x33B4), (U32 0x33B8), (U32 0x33BC)
        "  answer:  serial {0}  applied {1}  status {2}" -f (U32 0x3328), (U32 0x3364), $status
        "  detail:  '{0}'" -f (Ascii 0x336C 64)
    }
    'cheat' {
        # The staging cheats, without needing the console or a keyboard — which is the whole
        # point, since injected input does not reach this game (see docs/live-verification.md).
        # Bare `cheat` prints the channel; the plugin's DETAIL string is the answer worth
        # reading, because a refusal names the rule and the object id it saw.
        #   cheat god 1 | cheat god 0 | cheat topup [hp] | cheat standup [hp] | cheat slay <id>
        $kinds = @{ 'god' = 1; 'topup' = 2; 'standup' = 3; 'slay' = 4 }
        if ($Arg1.Length -gt 0) {
            if (-not $kinds.ContainsKey($Arg1)) {
                Write-Error "cheat kinds: $($kinds.Keys -join ', ')  (tp/dropitem/setitem need the console)"
                exit 1
            }

            $kind = $kinds[$Arg1]
            $target = 0
            $amount = $Arg2
            if ($Arg1 -eq 'slay') { $target = $Arg2; $amount = 0 }

            WU32 0x332C ([uint32]$kind)
            WU32 0x3330 ([uint32]$target)
            WU32 0x3334 ([uint32]$amount)
            WAscii 0x3344 '' 32

            # The setitem fields go back to "unchanged" so a leftover value from an earlier
            # poke cannot ride along with an unrelated kind.
            # ([uint32]::MaxValue) rather than the bare 0xFFFFFFFF literal: PowerShell parses
            # that literal as a signed value wider than Int32, and passing it straight into a
            # [uint32] parameter throws "Cannot convert value ... System.UInt32" on this PS
            # version. Found live 2026-08-23 -- every 'cheat' call was printing four of these
            # errors (harmlessly; the writes still landed) before this fix.
            WU32 0x33B0 ([uint32]::MaxValue)
            WU32 0x33B4 ([uint32]::MaxValue)
            WU32 0x33B8 ([uint32]::MaxValue)
            WU32 0x33BC ([uint32]::MaxValue)

            WU32 0x3368 0
            WAscii 0x336C '' 64
            WU32 0x3328 ((U32 0x3328) + 1)
            "cheat '$Arg1' queued (serial $(U32 0x3328)); re-run the bare 'cheat' to read the answer"
        }

        # Status is a SIGNED int in the block and every refusal is negative, so it has to be
        # sign-extended back out of the unsigned read or -3 prints as 4294967293.
        $status = U32 0x3368
        if ($status -gt 0x7FFFFFFF) { $status = [int64]$status - 4294967296 }

        "  cheat: serial {0}  applied {1}  status {2}" -f (U32 0x3328), (U32 0x3364), $status
        "  detail: '{0}'" -f (Ascii 0x336C 64)
    }
    'attack' {
        # Orders a creature to attack another through the engine's own ActionAttack, so a
        # real fight (swings, rolls, damage, AI follow-up) can be staged on a save whose
        # enemies never aggro by themselves. Arg1 = attacker id, Arg2 = target id.
        if ($Arg1.Length -eq 0) { Write-Error 'attack needs <attackerId> <targetId>'; exit 1 }
        WU32 0x1DA0 ([uint32]$Arg1)
        WU32 0x1DA4 ([uint32]$Arg2)
        WU32 0x1DA8 ((U32 0x1DA8) + 1)
        "attack ordered: 0x{0:X} -> 0x{1:X} (serial {2})" -f [uint32]$Arg1, [uint32]$Arg2, (U32 0x1DA8)
    }
    'talk' {
        # Opens a conversation with an NPC by tag — exactly what clicking it does, so the
        # exclusivity gate and the peer's spectate both run for real.
        if ($Arg1.Length -eq 0) { Write-Error 'talk needs an NPC tag (e.g. end_trask)'; exit 1 }
        WAscii 0x1D70 $Arg1 32
        WU32 0x1D90 ((U32 0x1D90) + 1)
        "talk requested: '$Arg1' (serial $(U32 0x1D90))"
    }
    'walk' {
        # Walks the local player to a point with the engine's own move action.
        $x = [float]$Arg1; $y = [float]$Arg2
        WF32 0x1D94 $x
        WF32 0x1D98 $y
        WU32 0x1D9C ((U32 0x1D9C) + 1)
        "walk requested: ({0:F2}, {1:F2}) serial {2}" -f $x, $y, (U32 0x1D9C)
    }
    'dialogue' {
        "  local:  serial {0}  speaker '{1}'" -f (U32 0xCE8), (Ascii 0xC80 32)
        "  local:  partner '{0}'  replySerial {1} index {2}" -f (Ascii 0x1200 32), (U32 0x1220), (U32 0x1224)
        "  remote: active {0}  partner '{1}'  replySerial {2} index {3}" -f `
            (U32 0x1228), (Ascii 0x1240 32), (U32 0x122C), (U32 0x1230)
        "  line:   '{0}'" -f (Ascii 0x1100 96)
    }
    'hitphantom' {
        # Lands a REAL engine damage effect on a phantom, so the combat-participation
        # chain (OnApplyDamage -> watch -> wire -> the peer's own character) can be
        # exercised without an armed encounter. Arg1 = slot (default 0), Arg2 = amount.
        $slot = if ($Arg1 -match '^\d+$') { [uint32]$Arg1 } else { 0 }
        $amount = if ($Arg2 -gt 0) { [uint32]$Arg2 } else { 5 }
        WU32 0x1D5C $slot
        WU32 0x1D60 $amount
        WU32 0x1D64 ((U32 0x1D64) + 1)
        "test hit queued: phantom slot $slot for $amount damage (serial $(U32 0x1D64))"
    }
    'combat' {
        # The combat-participation state: what each phantom here has taken, and what this
        # player owes for hits taken on their phantom elsewhere.
        "  incoming: owed {0}  applied {1}  lastAttacker 0x{2:X}" -f (U32 0x1D50), (U32 0x1D58), (U32 0x1D54)
        for ($i = 0; $i -lt 8; $i++) {
            $base = 0x1CD0 + ($i * 0x10)
            $id = U32 $base
            if ($id -ne 0) {
                "  phantom slot {0}: id 0x{1:X}  taken {2}  lastAttacker 0x{3:X}  hits {4}" -f `
                    $i, $id, (U32 ($base + 4)), (U32 ($base + 8)), (U32 ($base + 0xC))
            }
        }
    }
    'ledger' {
        "  local player: 0x{0:X}" -f (U32 0x2264)
        for ($slot = 0; $slot -lt 8; $slot++) {
            $remote = 0x50 + ($slot * 0x40)
            if ((U32 $remote) -ne 0) {
                "  remote slot {0}: peer {1} phantom 0x{2:X} hp {3}" -f $slot, (U32 ($remote + 4)), (U32 (0x1F80 + ($slot * 4))), (U32 ($remote + 0x18))
            }
        }
        if ((U32 8) -ge 83) {
            $snapshot = $null
            for ($attempt = 0; $attempt -lt 4; $attempt++) {
                $sequence = U32 0x8700
                if (($sequence -band 1) -ne 0) { continue }
                $candidate = [BP]::Read($h, ($block + 0x8700), 0x3010)
                if ($candidate -and (U32 0x8700) -eq $sequence) { $snapshot = $candidate; break }
            }
            if ($snapshot) {
                $count = [Math]::Min([BitConverter]::ToUInt32($snapshot, 8), 1024)
                "  attributed: count {0} epoch 0x{1:X8} world serial {2}" -f $count, [BitConverter]::ToUInt32($snapshot, 4), [BitConverter]::ToUInt32($snapshot, 12)
                for ($i = 0; $i -lt [Math]::Min($count, 16); $i++) {
                    $row = 16 + ($i * 12)
                    "    attributed[{0}]: peer {1} enemy 0x{2:X} total {3}" -f $i, [BitConverter]::ToUInt32($snapshot, $row), [BitConverter]::ToUInt32($snapshot, $row + 4), [BitConverter]::ToUInt32($snapshot, $row + 8)
                }
            } else { '  attributed: publication changing; retry' }
        }
        # The peer-damage ledger, both directions (SharedBlock.Combat.cs): what THIS instance
        # has dealt to other people's enemies (plugin -> agent, cumulative per enemy id) and
        # what the peers owe this instance's own enemies (agent -> plugin). Cumulative totals,
        # so a row that jumps by more than any weapon can deal is a corrupt read, not a hit.
        "  dealt: count {0}  dropped {1}  generation {2}  enabled {3}" -f (U32 0x2000), (U32 0x2004), (U32 0x2008), (U32 0x2118)
        $n = [Math]::Min([int](U32 0x2000), 128)
        for ($i = 0; $i -lt $n; $i++) {
            $base = 0x7E00 + ($i * 8)
            "    dealt[{0}]: id 0x{1:X}  total {2}" -f $i, (U32 $base), (U32 ($base + 4))
        }
        "  owed: count {0}  epoch 0x{1:X8}  applied {2}" -f (U32 0x2010), (U32 0x200C), (U32 0x2014)
        $n = [Math]::Min([int](U32 0x2010), 128)
        for ($i = 0; $i -lt $n; $i++) {
            $base = 0x8200 + ($i * 8)
            "    owed[{0}]: id 0x{1:X}  total {2}" -f $i, (U32 $base), (U32 ($base + 4))
        }
    }
    'threat' {
        # THREAT_RETARGET's own diagnostic snapshot (coop.ini threat_retarget, SharedBlock
        # version 66, docs/research/peer-damage-ledger.md §9): what PeerDamageController's
        # ThreatTable believed as of the last owed-loop pass that ran with the key on. Empty
        # (count 0) either means the key is off, nothing is currently owed against any enemy,
        # or every tracked attacker's threat has fully decayed — `combat`/`ledger` above show
        # whether the underlying damage channel itself is alive.
        "  threat: count {0}  generation {1}" -f (U32 0x4B80), (U32 0x4B84)
        $n = [Math]::Min([int](U32 0x4B80), 6)
        for ($i = 0; $i -lt $n; $i++) {
            $base = 0x4B88 + ($i * 0x14)
            $topThreat = (U32 ($base + 0xC)) / 100.0
            $curThreat = (U32 ($base + 0x10)) / 100.0
            "    threat[{0}]: creature 0x{1:X}  currentTarget 0x{2:X} (threat {3:F2})  topAttacker 0x{4:X} (threat {5:F2})" -f `
                $i, (U32 $base), (U32 ($base + 4)), $curThreat, (U32 ($base + 8)), $topThreat
        }
    }
    'encounters' {
        # Client-triggered encounters, both halves. On a CLIENT: watch + inside says which
        # activate areas the player is standing in. On the HOST: requests says what clients
        # have asked for and fired/refused says what its engine did with them.
        # `encounters <id> <peer>` writes a request by hand, which is how the fire path can
        # be exercised without a second player walking anywhere.
        if ($Arg1 -match '^(0x)?[0-9A-Fa-f]+$' -and $Arg1 -ne '') {
            $id = [Convert]::ToUInt32($Arg1.Replace('0x', ''), 16)
            $peer = if ($Arg2 -gt 0) { [uint32]$Arg2 } else { 1 }
            WU32 0x1E04 $id
            WU32 0x1E08 $peer
            WU32 0x1DFC 1
            WU32 0x1E00 ((U32 0x1E00) + 1)
            "fire request queued: encounter 0x{0:X} on behalf of peer {1} (serial {2})" -f $id, $peer, (U32 0x1E00)
        }
        "  watch:    enabled {0}  inside {1} (serial {2})" -f (U32 0x1DE0), (U32 0x1DE4), (U32 0x1DE8)
        $inside = [Math]::Min([int](U32 0x1DE4), 4)
        for ($i = 0; $i -lt $inside; $i++) { "    inside[{0}] 0x{1:X}" -f $i, (U32 (0x1DEC + ($i * 4))) }
        "  requests: count {0} (serial {1})" -f (U32 0x1DFC), (U32 0x1E00)
        $reqs = [Math]::Min([int](U32 0x1DFC), 8)
        for ($i = 0; $i -lt $reqs; $i++) {
            "    request[{0}] encounter 0x{1:X}  peer {2}" -f $i, (U32 (0x1E04 + ($i * 8))), (U32 (0x1E08 + ($i * 8)))
        }
        "  result:   fired {0}  refused {1}  lastStatus {2}" -f (U32 0x1E44), (U32 0x1E48), (U32 0x1E4C)
    }
    'triggers' {
        # Generic-trigger relay (TriggerRelay), both halves, mirroring 'encounters' exactly
        # but at the tail-slack region 0x3B88..0x3BD8. On a CLIENT: watch + inside says which
        # relay-eligible polygons the player is standing in. On the HOST: requests says what
        # clients have asked for and fired/refused says what its engine did with them.
        # `triggers <id> <peer>` writes a request by hand, which is how the fire path can be
        # exercised without a second player walking anywhere — see docs/research/trigger-relay.md.
        if ($Arg1 -match '^(0x)?[0-9A-Fa-f]+$' -and $Arg1 -ne '') {
            $id = [Convert]::ToUInt32($Arg1.Replace('0x', ''), 16)
            $peer = if ($Arg2 -gt 0) { [uint32]$Arg2 } else { 1 }
            WU32 0x3BAC $id
            WU32 0x3BB0 $peer
            WU32 0x3BA4 1
            WU32 0x3BA8 ((U32 0x3BA8) + 1)
            "fire request queued: trigger 0x{0:X} on behalf of peer {1} (serial {2})" -f $id, $peer, (U32 0x3BA8)
        }
        "  watch:    enabled {0}  inside {1} (serial {2})" -f (U32 0x3B88), (U32 0x3B8C), (U32 0x3B90)
        $inside = [Math]::Min([int](U32 0x3B8C), 4)
        for ($i = 0; $i -lt $inside; $i++) { "    inside[{0}] 0x{1:X}" -f $i, (U32 (0x3B94 + ($i * 4))) }
        "  requests: count {0} (serial {1})" -f (U32 0x3BA4), (U32 0x3BA8)
        $reqs = [Math]::Min([int](U32 0x3BA4), 4)
        for ($i = 0; $i -lt $reqs; $i++) {
            "    request[{0}] trigger 0x{1:X}  peer {2}" -f $i, (U32 (0x3BAC + ($i * 8))), (U32 (0x3BB0 + ($i * 8)))
        }
        "  result:   fired {0}  refused {1}  lastStatus {2}" -f (U32 0x3BCC), (U32 0x3BD0), (U32 0x3BD4)
    }
    'browser' {
        # `browser` prints the panel state; `browser 1` / `browser 0` opens / closes the
        # native panel (the plugin honors the block as an external open/close request).
        if ($Arg1 -match '^\d+$') { WU32 0xE00 ([uint32]$Arg1) }
        "  browser: open {0}  selection {1}  count {2}  connectSerial {3}  listSerial {4}" -f `
            (U32 0xE00), (U32 0xE04), (U32 0xE08), (U32 0xE0C), (U32 0xE10)
        $count = [Math]::Min([int](U32 0xE08), 8)
        for ($i = 0; $i -lt $count; $i++) {
            $base = 0xE20 + ($i * 0x40)
            "    {0}. {1}  {2}  ({3}/{4})" -f ($i + 1), (Ascii $base 32), (Ascii ($base + 0x20) 24), `
                ([BP]::Read($h, $block + $base + 0x38, 1)[0]), ([BP]::Read($h, $block + $base + 0x39, 1)[0])
        }
    }
}

[BP]::CloseHandle($h) | Out-Null
