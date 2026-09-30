# Read-only native census. Walks the engine's live object table, never a heap-vtable scan.
# Run only once the class/level-up GUI has closed and the refreshed world has settled.
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [string]$MemoryAssembly = (Join-Path $PSScriptRoot '..\src\KotorCoop.Memory\bin\Debug\net10.0-windows\KotorCoop.Memory.dll')
)
$ErrorActionPreference = 'Stop'
$dll = (Resolve-Path $MemoryAssembly).Path
[Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $dll) 'KotorCoop.Protocol.dll')) | Out-Null
[Reflection.Assembly]::LoadFrom($dll) | Out-Null
$memory = [KotorCoop.Memory.ProcessMemory]::Attach($ProcessId, $false)
function Read-U32([long]$Address) {
    $value = 0u
    if (-not $memory.TryReadUInt32([IntPtr]$Address, [ref]$value)) { throw "unreadable native census address $Address" }
    return $value
}
function Read-Tag([long]$Address) {
    $pointer = Read-U32 $Address
    $length = Read-U32 ($Address + 4)
    if ($pointer -eq 0 -or $length -eq 0) { return '' }
    if ($length -gt 512) { throw 'invalid native tag length' }
    $text = ''
    if (-not $memory.TryReadAsciiString([IntPtr]$pointer, [int]$length, [ref]$text)) { throw "unreadable native tag at $Address pointer=$pointer length=$length" }
    return $text
}
try {
    $array = [IntPtr]0
    if (-not [KotorCoop.Memory.EngineGlobals]::TryGetServerObjectArray($memory, [ref]$array)) { throw 'native server table unavailable' }
    $tree = Read-U32 $array
    $module = ''
    if (-not [KotorCoop.Memory.EngineGlobals]::TryReadModuleName($memory, [ref]$module)) { throw 'module unavailable' }
    $rows = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[uint32]]::new()
    for ($bucket = 0; $bucket -lt 8192; $bucket++) {
        $entry = Read-U32 ($tree + $bucket * 4)
        $walked = 0
        while ($entry -ne 0) {
            if (++$walked -gt 64) { throw 'native bucket cycle or table changed' }
            $id = Read-U32 $entry
            if ($bucket -ge 4096) { $id = $id -bor 0x80000000u }
            $address = Read-U32 ($entry + 4)
            $entry = Read-U32 ($entry + 8)
            $resolved = [IntPtr]0
            if (-not [KotorCoop.Memory.EngineGlobals]::TryResolveServerObject($memory, $array, $id, [ref]$resolved) -or $resolved -ne [IntPtr]$address) { throw 'native table changed while sampling' }
            if (-not $seen.Add($id)) { throw 'duplicate native object id' }
            $vtable = Read-U32 $address
            $kind = 'Other'
            $tag = ''
            $possessor = $null
            $deleting = $false
            if ($vtable -eq [KotorCoop.Memory.KotorGameBridge]::CreatureVtable) {
                $kind = 'Creature'; $tag = Read-Tag ($address + 0x18)
            } elseif ($vtable -eq [KotorCoop.Memory.KotorGameBridge]::PlaceableVtable) {
                $kind = 'Placeable'; $tag = Read-Tag ($address + 0x18)
            } elseif ($address -ge 16 -and (Read-U32 ($address - 16)) -eq [KotorCoop.Memory.ItemIdentityLayout]::ItemVtable) {
                $kind = 'Item'; $tag = Read-Tag ($address + 0x18)
                $possessor = Read-U32 ($address - 16 + 0x268)
                $deleting = ((Read-U32 ($address - 16 + 0x288)) -band 0x100) -ne 0
            }
            $rows.Add([pscustomobject]@{ Id = $id; Address = $address; Kind = $kind; Tag = $tag; Possessor = $possessor; Deleting = $deleting })
        }
    }
    $finalArray = [IntPtr]0; $finalModule = ''
    if (-not [KotorCoop.Memory.EngineGlobals]::TryGetServerObjectArray($memory, [ref]$finalArray) -or $finalArray -ne $array -or
        -not [KotorCoop.Memory.EngineGlobals]::TryReadModuleName($memory, [ref]$finalModule) -or $module -ne $finalModule) { throw 'world changed while sampling' }
    foreach ($row in $rows) {
        $resolved = [IntPtr]0
        if (-not [KotorCoop.Memory.EngineGlobals]::TryResolveServerObject($memory, $array, $row.Id, [ref]$resolved) -or $resolved -ne [IntPtr]$row.Address) { throw 'native object removed or replaced while sampling; retry once settled' }
    }
    $lockers = @($rows | Where-Object { $_.Kind -eq 'Placeable' -and $_.Tag -in @('end_locker01', 'FootLker') })
    $lockerItems = foreach ($locker in $lockers) {
        [pscustomobject]@{ Tag = $locker.Tag; Id = $locker.Id; Items = @($rows | Where-Object { $_.Kind -eq 'Item' -and -not $_.Deleting -and $_.Possessor -eq $locker.Id } | Sort-Object Tag | Select-Object Id,Tag) }
    }
    [pscustomobject]@{
        ProcessId = $ProcessId; Module = $module; NativeObjects = $rows.Count
        LiveItems = @($rows | Where-Object { $_.Kind -eq 'Item' -and -not $_.Deleting }).Count
        Trask = @($rows | Where-Object { $_.Tag -match 'trask|jfts' })
        Lockers = @($lockerItems)
    } | ConvertTo-Json -Depth 6
} finally { $memory.Dispose() }



