param([Parameter(Mandatory = $true)][string]$Path)

$b = [System.IO.File]::ReadAllBytes($Path)
$pe = [BitConverter]::ToInt32($b, 0x3C)
$opt = $pe + 24
$magic = [BitConverter]::ToUInt16($b, $opt)
$isPe32 = $magic -eq 0x10B

# Data directory 0 is the export table. Its offset differs between PE32 and PE32+.
$dirOffset = if ($isPe32) { $opt + 96 } else { $opt + 112 }
$exportRva = [BitConverter]::ToUInt32($b, $dirOffset)

$numSections = [BitConverter]::ToUInt16($b, $pe + 6)
$sectionStart = $opt + [BitConverter]::ToUInt16($b, $pe + 20)

function RvaToOffset($rva) {
    for ($i = 0; $i -lt $numSections; $i++) {
        $s = $sectionStart + ($i * 40)
        $va = [BitConverter]::ToUInt32($b, $s + 12)
        $vsize = [BitConverter]::ToUInt32($b, $s + 8)
        $raw = [BitConverter]::ToUInt32($b, $s + 20)
        $rawSize = [BitConverter]::ToUInt32($b, $s + 16)
        $size = [Math]::Max($vsize, $rawSize)
        if ($rva -ge $va -and $rva -lt ($va + $size)) { return $raw + ($rva - $va) }
    }
    return 0
}

$e = RvaToOffset $exportRva
if ($e -eq 0) { Write-Error 'no export directory'; exit 1 }

$nameCount = [BitConverter]::ToUInt32($b, $e + 24)
$namesRva = [BitConverter]::ToUInt32($b, $e + 32)
$ordinalsRva = [BitConverter]::ToUInt32($b, $e + 36)
$ordinalBase = [BitConverter]::ToUInt32($b, $e + 16)

$namesOff = RvaToOffset $namesRva
$ordOff = RvaToOffset $ordinalsRva

for ($i = 0; $i -lt $nameCount; $i++) {
    $nameRva = [BitConverter]::ToUInt32($b, $namesOff + ($i * 4))
    $nameOff = RvaToOffset $nameRva
    $end = $nameOff
    while ($b[$end] -ne 0) { $end++ }
    $name = [System.Text.Encoding]::ASCII.GetString($b, $nameOff, $end - $nameOff)
    $ord = [BitConverter]::ToUInt16($b, $ordOff + ($i * 2)) + $ordinalBase
    "$name|$ord"
}
