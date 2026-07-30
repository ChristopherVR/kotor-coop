# Detects KOTOR's hover tooltip in a screenshot.
#
# When the cursor is over a usable object the game draws the object's name above a bright
# cyan underline that is far longer than any run of that colour occurring naturally in the
# scene. Finding that run is much cheaper and more reliable than trying to spot a small
# item model on the floor by eye, and it gives the object's screen position directly.
param(
    [Parameter(Mandatory = $true)][string]$In,
    [int]$MinRun = 90
)

Add-Type -AssemblyName System.Drawing

$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $In))
$w = $bmp.Width; $h = $bmp.Height

$rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
$data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bytes = New-Object byte[] ($data.Stride * $h)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
$bmp.UnlockBits($data)
$bmp.Dispose()

$best = 0; $bestY = -1; $bestX = -1

for ($y = 0; $y -lt $h; $y++) {
    $row = $y * $data.Stride
    $run = 0; $start = 0
    for ($x = 0; $x -lt $w; $x++) {
        $i = $row + ($x * 4)
        $b = $bytes[$i]; $g = $bytes[$i + 1]; $r = $bytes[$i + 2]
        # The underline is saturated cyan: strong blue and green, weak red.
        if ($b -gt 190 -and $g -gt 140 -and $r -lt 130) {
            if ($run -eq 0) { $start = $x }
            $run++
            if ($run -gt $best) { $best = $run; $bestY = $y; $bestX = $start }
        }
        else { $run = 0 }
    }
}

if ($best -ge $MinRun) {
    "TOOLTIP run=$best at x=$bestX..$($bestX + $best) y=$bestY  centre=($([int]($bestX + $best / 2)),$bestY)"
}
else {
    "none (longest cyan run $best)"
}
