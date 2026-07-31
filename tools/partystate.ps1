# Reads a KOTOR instance's party table: solo-mode flag, active NPC slots, and the object
# id + live position of each spawned companion.
#
# Exists to verify party reconciliation without a full playthrough. The sync is
# host-authoritative — the host's engine AI drives the companions and publishes where they
# are; each client sets SOLO MODE (so its own follow-AI stops fighting the writes) and
# mirrors the host's positions by roster slot. Both halves are checkable here: run it
# against both instances and compare, and confirm the client (and only the client) has
# solo mode held.
#
#   pwsh tools\partystate.ps1 -ProcId 1234
param(
    [int]$ProcId,
    [string]$ProcessName = 'swkotor'
)

Add-Type @'
using System; using System.Runtime.InteropServices;
public class PS2 {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  public static IntPtr Open(int pid){ return OpenProcess(0x0410, false, (uint)pid); }
  public static byte[] Read(IntPtr h, long a, int n){
    byte[] b = new byte[n]; int r;
    if(!ReadProcessMemory(h, new IntPtr(a), b, n, out r) || r != n) return null;
    return b; }
  public static int I32(IntPtr h, long a){ byte[] b = Read(h, a, 4); return b == null ? 0 : BitConverter.ToInt32(b, 0); }
  public static byte U8(IntPtr h, long a){ byte[] b = Read(h, a, 1); return b == null ? (byte)0xFF : b[0]; }
  public static float F32(IntPtr h, long a){ byte[] b = Read(h, a, 4); return b == null ? 0f : BitConverter.ToSingle(b, 0); }
}
'@

if ($ProcId -eq 0) {
    $p = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { Write-Error 'no game process'; exit 1 }
    $ProcId = $p.Id
}

$h = [PS2]::Open($ProcId)

# CAppManager -> CServerExoApp -> CServerExoAppInternal -> embedded CSWPartyTable.
$app = [PS2]::I32($h, 0x7A39FC)
if ($app -eq 0) { Write-Error 'no app manager'; exit 1 }
$server = [PS2]::I32($h, $app + 8)
if ($server -eq 0) { Write-Error 'no server (still at the main menu?)'; exit 1 }
$serverInternal = [PS2]::I32($h, $server + 4)
$party = $serverInternal + 0x1B770

$count = [PS2]::I32($h, $party + 0x00)
$solo = [PS2]::U8($h, $party + 0x190)

"pid $ProcId  partyTable 0x{0:X}" -f $party
"  companions {0}   soloMode {1}" -f $count, $solo

# int[2] active NPC slot indices at +0x04, int[9] spawned object id per slot at +0x0C.
for ($i = 0; $i -lt 2; $i++) {
    $slot = [PS2]::I32($h, $party + 0x04 + ($i * 4))
    if ($slot -lt 0 -or $slot -ge 9) { continue }

    $objectId = [PS2]::I32($h, $party + 0x0C + ($slot * 4))
    if ($objectId -eq 0 -or $objectId -eq 0x7F000000) {
        "  member {0}: slot {1}  (not spawned)" -f $i, $slot
        continue
    }

    # Find the creature by object id through the same vtable scan creatures.ps1 uses is
    # overkill here; the agent already resolves it. Report the id and let the caller cross
    # it against creatures.ps1 for a position.
    "  member {0}: slot {1}  objectId 0x{2:X}" -f $i, $slot, $objectId
}

[PS2]::CloseHandle($h) | Out-Null
