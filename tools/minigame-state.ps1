# Read-only native minigame telemetry; no shared-block staging or engine writes.
# Compare snapshots around real `input key` / `input click` commands.
# Retail start recipe: `setglobal K_RANDOM_MINI_GAME true`, then `warp m12ab`.
# Position provenance: CSWTrackFollower::GetPosition 0x66D5D0 selects models[0];
# Gob::GetPosition 0x449980 reads +0x78. Orientation +0x84 and previous position
# +0x94 are confirmed by the adjacent getters 0x4499A0/0x4499D0. No engine calls.
param([Parameter(Mandatory)][int]$ProcId)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MiniGameProbe {
 [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
 [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h,IntPtr a,byte[] b,int n,out int read);
 public static byte[] Read(IntPtr h,long a,int n){var b=new byte[n];int r;return a!=0&&ReadProcessMemory(h,new IntPtr(a),b,n,out r)&&r==n?b:null;}
 public static uint U32(IntPtr h,long a){var b=Read(h,a,4);return b==null?0:BitConverter.ToUInt32(b,0);}
 public static int I32(IntPtr h,long a){var b=Read(h,a,4);return b==null?0:BitConverter.ToInt32(b,0);}
 public static float F32(IntPtr h,long a){var b=Read(h,a,4);return b==null?float.NaN:BitConverter.ToSingle(b,0);}
}
'@
$handle = [MiniGameProbe]::OpenProcess(0x10, $false, $ProcId)
if ($handle -eq [IntPtr]::Zero) { throw "Cannot read process $ProcId" }
function Read-Model([long]$Model) {
    if ($Model -eq 0) { return $null }
    $vtable = [MiniGameProbe]::U32($handle, $Model)
    # Refuse unrelated objects rather than reading plausible transform bytes.
    if (-not $vtable -or [MiniGameProbe]::U32($handle, $vtable + 0x64) -ne 0x00449980) { return $null }
    [pscustomobject]@{
        Address = '0x{0:X}' -f $Model
        Position = @(0x78,0x7C,0x80 | ForEach-Object { [MiniGameProbe]::F32($handle, $Model + $_) })
        Quaternion = @(0x84,0x88,0x8C,0x90 | ForEach-Object { [MiniGameProbe]::F32($handle, $Model + $_) })
        PreviousPosition = @(0x94,0x98,0x9C | ForEach-Object { [MiniGameProbe]::F32($handle, $Model + $_) })
    }
}
function Read-FollowerModel([long]$Body) {
    if ($Body -eq 0 -or [MiniGameProbe]::U32($handle, $Body + 0x6C) -eq 0) { return $null }
    $models = [MiniGameProbe]::U32($handle, $Body + 0x68)
    if (-not $models) { return $null }
    Read-Model ([MiniGameProbe]::U32($handle, $models))
}
function Read-GunBanks([long]$Body) {
    if ($Body -eq 0) { return }
    # CSWMiniPlayer::Fire 0x66DC50 walks the +0x74/+0x78 array; each bank's
    # Fire 0x674090 walks its +0x34/+0x38 Gob array before invoking gun behavior.
    $banks = [MiniGameProbe]::U32($handle, $Body + 0x74)
    $count = [MiniGameProbe]::U32($handle, $Body + 0x78)
    if (-not $banks -or $count -gt 16) { return }
    for ($i = 0; $i -lt $count; $i++) {
        $bank = [MiniGameProbe]::U32($handle, $banks + $i * 4)
        $bankVtable = [MiniGameProbe]::U32($handle, $bank)
        if ($bankVtable -notin @(0x00752AE4, 0x00752AF0)) { continue }
        $models = [MiniGameProbe]::U32($handle, $bank + 0x34)
        $modelsCount = [MiniGameProbe]::U32($handle, $bank + 0x38)
        if (-not $models -or $modelsCount -gt 16) { continue }
        $transforms = @(for ($j = 0; $j -lt $modelsCount; $j++) { Read-Model ([MiniGameProbe]::U32($handle, $models + $j * 4)) })
        [pscustomobject]@{ Index = $i; Address = '0x{0:X}' -f $bank; Models = $transforms }
    }
}
try {
    $app = [MiniGameProbe]::U32($handle, 0x007A39FC)
    $client = if ($app) { [MiniGameProbe]::U32($handle, $app + 4) } else { 0 }
    $clientInternal = if ($client) { [MiniGameProbe]::U32($handle, $client + 4) } else { 0 }
    $module = if ($clientInternal) { [MiniGameProbe]::U32($handle, $clientInternal + 0x18) } else { 0 }
    $area = if ($module) { [MiniGameProbe]::U32($handle, $module + 0x48) } else { 0 }
    $miniGame = if ($area) { [MiniGameProbe]::U32($handle, $area + 0x264) } else { 0 }
    $validMiniGame = $miniGame -ne 0 -and [MiniGameProbe]::U32($handle, $miniGame) -eq 0x00752914
    $player = if ($validMiniGame) { [MiniGameProbe]::U32($handle, $miniGame + 0x24) } else { 0 }
    if ($player -and ([MiniGameProbe]::U32($handle, $player) -ne 0x007525F0 -or [MiniGameProbe]::U32($handle, $player + 0x60) -ne $miniGame)) { $player = 0 }
    $array = if ($clientInternal) { [MiniGameProbe]::U32($handle, $clientInternal) } else { 0 }
    $fighters = @()
    if ($array -and $validMiniGame) {
        for ($slot = 0; $slot -lt 255; $slot++) {
            $body = [MiniGameProbe]::U32($handle, $array + 4 + $slot * 4)
            if ($body -eq 0 -or [MiniGameProbe]::U32($handle, $body) -ne 0x007528A8 -or [MiniGameProbe]::U32($handle, $body + 0x60) -ne $miniGame) { continue }
            $fighters += [pscustomobject]@{
                Slot = $slot
                Address = '0x{0:X}' -f $body
                Hp = [MiniGameProbe]::I32($handle, $body + 0x8C)
                MaxHp = [MiniGameProbe]::I32($handle, $body + 0x90)
                Dead = ([MiniGameProbe]::U32($handle, $body + 0x88) -band 2) -ne 0
                Model = Read-FollowerModel $body
                TrackSpeed = [MiniGameProbe]::F32($handle, $body + 0x98)
            }
        }
    }
    if ($validMiniGame -and [MiniGameProbe]::U32($handle, $area + 0x264) -ne $miniGame) {
        throw 'Minigame changed while sampling; retry after the transition.'
    }
    [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Now.ToString('o')
        ProcessId = $ProcId
        InMinigame = $validMiniGame
        Type = if ($validMiniGame) { [MiniGameProbe]::U32($handle, $miniGame + 0x80) } else { $null }
        PlayerHp = if ($player) { [MiniGameProbe]::I32($handle, $player + 0x8C) } else { $null }
        PlayerModel = Read-FollowerModel $player
        GunBanks = @(Read-GunBanks $player)
        CameraViewAngle = if ($validMiniGame) { [MiniGameProbe]::F32($handle, $miniGame + 0x70) } else { $null }
        MovementPerSecond = if ($validMiniGame) { [MiniGameProbe]::F32($handle, $miniGame + 0x74) } else { $null }
        MouseAxes = if ($validMiniGame) { @(0x84,0x88 | ForEach-Object { [MiniGameProbe]::I32($handle, $miniGame + $_) }) } else { @() }
        AxisVelocity = if ($validMiniGame) { @(0xE4,0xE8,0xEC | ForEach-Object { [MiniGameProbe]::F32($handle, $miniGame + $_) }) } else { @() }
        ControlTransform = if ($validMiniGame) { @(0xC0,0xC4,0xC8,0xCC,0xD0,0xD4 | ForEach-Object { [MiniGameProbe]::F32($handle, $miniGame + $_) }) } else { @() }
        Fighters = $fighters
        Alive = @($fighters | Where-Object { -not $_.Dead -and $_.Hp -gt 0 }).Count
    } | ConvertTo-Json -Depth 7
} finally {
    [MiniGameProbe]::CloseHandle($handle) > $null
}
