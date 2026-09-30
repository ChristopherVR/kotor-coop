# Real attacks only. Writes are restricted to the existing console command mailbox.
# Run only after reviewing the staged, isolated, non-commandable enemy fixture.
param(
    [Parameter(Mandatory)][int[]]$ProcessIds,
    [Parameter(Mandatory)][uint32]$HostEnemyId,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$BaselinePath='',
    [ValidateRange(1,15)][int]$AttackSeconds=15,
    [ValidateRange(7,30)][int]$QuietSeconds=7
)
$ErrorActionPreference='Stop'
if ($ProcessIds.Count -ne 8 -or @($ProcessIds | Sort-Object -Unique).Count -ne 8) { throw 'Supply host first, then seven distinct joiner PIDs.' }
# Retain an external baseline before writing entry-host.json, including when both
# paths name the same file in the output directory.
$suppliedBaseline=if($BaselinePath){Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json}else{$null}
$out=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($out) > $null
$probe=Join-Path $PSScriptRoot 'peer-combat-probe.ps1'
$checker=Join-Path $PSScriptRoot 'eight-player-combat-check.ps1'
function Snapshot([int]$processId,[uint32]$enemy,[string]$name) {
    $path=if($name){Join-Path $out ($name+'.json')}else{''}
    & $probe -ProcId $processId -EnemyIds @($enemy) -OutputPath $path -NativeOnly:($processId -ne $ProcessIds[0]) | ConvertFrom-Json
}
function Save-Json($value,[string]$name) { [IO.File]::WriteAllText((Join-Path $out ($name+'.json')),($value | ConvertTo-Json -Depth 12)) }
function Accepted($snapshot,[uint32]$peer) {
    $rows=@($snapshot.Accepted | Where-Object { $_.Peer -eq $peer -and $_.Enemy -eq $HostEnemyId })
    if($rows.Count -gt 1){throw 'Duplicate accepted row'}
    if($rows.Count){return [long]$rows[0].AcceptedCumulative}; return 0L
}
function Stable-Key($snapshot) {
    $e=@($snapshot.Enemies | Where-Object Id -eq $HostEnemyId)[0]
    ($snapshot.Accepted | Sort-Object Peer,Enemy | ConvertTo-Json -Compress)+':'+$e.BaseHp+':'+$e.TemporaryHp+':'+$snapshot.AppliedEffects
}
function Validate-World($snapshot,$reference,[uint32]$enemy) {
    foreach($key in @('Pid','ProcessStartTicks','Version','Epoch','WorldLoadSerial','NativeDamageIncarnation')) {
        if($snapshot.$key -ne $reference.$key){throw "Changed process/world field $key"}
    }
    if($null -ne $reference.NativeLivenessEpoch -and $snapshot.NativeLivenessEpoch -ne $reference.NativeLivenessEpoch){throw 'Changed native liveness epoch'}
    foreach($key in @('Module','ModuleEpoch','ObjectArray','ObjectTree')) {
        if($snapshot.NativeNamespace.$key -ne $reference.NativeNamespace.$key){throw "Changed native namespace $key"}
    }
    $e=@($snapshot.Enemies | Where-Object Id -eq $enemy)
    $r=@($reference.Enemies | Where-Object Id -eq $enemy)
    if($e.Count -ne 1 -or !$e[0].Resolved -or $e[0].BaseHp -le 0 -or $e[0].Address -ne $r[0].Address){throw 'Selected living creature changed or disappeared'}
    if($snapshot.LocalPlayer.Id -eq $enemy -or @($snapshot.Peers | Where-Object Phantom -eq $enemy).Count){throw 'Selected target is a player or phantom'}
}
# Load the read-only reader type without opening any write handle yet.
$hostNow=Snapshot $ProcessIds[0] $HostEnemyId 'entry-host'
if($hostNow.Version -lt 87 -or $hostNow.DamageDealtEnabled -ne 0){throw 'Requires native87+ rig with host damage forwarding disabled'}
$fixture=@($hostNow.Enemies | Where-Object Id -eq $HostEnemyId)
if($fixture.Count -ne 1 -or !$fixture[0].Resolved -or $fixture[0].BaseHp -le 0 -or $fixture[0].Commandable -ne 0 -or $fixture[0].Plot -ne 0){throw 'Need the staged living, non-commandable, non-plot host creature'}
$before=if($null -ne $suppliedBaseline){$suppliedBaseline}else{$hostNow}
Validate-World $hostNow $before $HostEnemyId
Save-Json $before 'before'
if(-not ('EightCombatMailbox' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class EightCombatMailbox {
 [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a,bool i,uint p);
 [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h,IntPtr a,byte[] b,int n,out int written);
 public static IntPtr Open(int pid){return OpenProcess(0x438,false,(uint)pid);}
 public static void Write(IntPtr h,long block,int offset,byte[] bytes){
  if(!((offset==0x9F4 && bytes.Length==4)||(offset==0x9F8 && bytes.Length==96))) throw new Exception("Not a console mailbox write");
  int written; if(!WriteProcessMemory(h,new IntPtr(block+offset),bytes,bytes.Length,out written)||written!=bytes.Length)throw new Exception("Mailbox write failed");
 }
}
'@
}
function Read-U32($entry,[int]$offset){
    $bytes=[PeerCombatReader]::Read($entry.Handle,$entry.Block+$offset,4)
    if(!$bytes){throw 'Unreadable shared block'}; [BitConverter]::ToUInt32($bytes,0)
}
function Attack-Command($entry,[uint32]$target){
    $fresh=Snapshot $entry.Pid $entry.Enemy ''
    Validate-World $fresh $entry.Reference $entry.Enemy
    if((Read-U32 $entry 8) -ne $entry.Reference.Version -or (Read-U32 $entry 12) -ne 0xC0FFEE01L){throw 'Shared block signature changed'}
    if((Read-U32 $entry 0x3328) -ne (Read-U32 $entry 0x3364)){throw 'Native command mailbox busy; refusing to overwrite'}
    $old=Read-U32 $entry 0x3328
    $cmd=if($target -eq 0){'attack stop'}else{'attack 0x{0:X}' -f $target}
    $buffer=[byte[]]::new(96); $ascii=[Text.Encoding]::ASCII.GetBytes($cmd); [Array]::Copy($ascii,$buffer,$ascii.Length)
    [EightCombatMailbox]::Write($entry.Handle,$entry.Block,0x9F8,$buffer)
    $serial=[uint32](([long](Read-U32 $entry 0x9F4)+1) -band 0xFFFFFFFFL)
    [EightCombatMailbox]::Write($entry.Handle,$entry.Block,0x9F4,[BitConverter]::GetBytes($serial))
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.Elapsed.TotalSeconds -lt 10){
        $native=Read-U32 $entry 0x3328
        if($native -ne $old -and (Read-U32 $entry 0x332C) -eq 98 -and (Read-U32 $entry 0x3364) -eq $native){
            $status=Read-U32 $entry 0x3368
            if($status -ne 1){throw "Native attack request refused (status $status)"}
            return
        }; Start-Sleep -Milliseconds 25
    }; throw 'Exact native attack ACK timed out'
}
$entries=@(); $touched=@(); $success=$false
try {
    $hostPeers=@($hostNow.Peers.Peer | Sort-Object -Unique)
    if($hostPeers.Count -ne 7 -or $hostPeers -contains 1){throw 'Host roster must contain seven joiners'}
    foreach($processId in $ProcessIds){
        $process=Get-Process -Id $processId
        $path=[IO.Path]::GetFullPath($process.Path)
        $privateRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/live-installs'))
        if($path -notmatch ('^'+[regex]::Escape($privateRoot)+'\\game[1-8]\\swkotor\.exe$')){throw "Not an owned private game: $path"}
    }
    foreach($processId in $ProcessIds[1..7]){
        $initial=Snapshot $processId $HostEnemyId ''
        if($initial.Version -ne $hostNow.Version){throw 'Mixed native versions in the rig'}
        $peer=@($hostPeers | Where-Object { $initial.Peers.Peer -notcontains $_ })
        if($peer.Count -ne 1 -or $initial.Peers.Count -ne 7 -or $initial.Peers.Peer -notcontains 1){throw 'Could not prove local peer identity from complete rosters'}
        $gameDirectory=Split-Path (Get-Process -Id $processId).Path
        $text=[IO.File]::ReadAllText((Join-Path $gameDirectory 'kotorcoop-plugin.log'))
        $pattern='materialise: owner 0x'+$HostEnemyId.ToString('X')+' -> local 0x([0-9A-F]+)\b'
        $matches=[regex]::Matches($text,$pattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if(!$matches.Count){throw "No owner-to-local materialisation proof for PID $processId"}
        $last=$matches[$matches.Count-1]
        $epoch=$text.LastIndexOf('epoch:',[StringComparison]::OrdinalIgnoreCase)
        if($epoch -gt $last.Index){throw 'Materialisation mapping predates latest world epoch'}
        $local=[Convert]::ToUInt32($last.Groups[1].Value,16)
        $reference=Snapshot $processId $local ('mapping-'+$peer[0])
        Validate-World $reference $reference $local
        if(!$reference.LocalPlayer.Resolved -or $reference.LocalPlayer.BaseHp -le 0){throw 'Joiner player is unavailable or downed'}
        $handle=[EightCombatMailbox]::Open($processId)
        if($handle -eq [IntPtr]::Zero){throw 'Cannot open reviewed console mailbox'}
        $signature=[byte[]](0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31,0,0,0,0,0x01,0xEE,0xFF,0xC0)
        $block=[PeerCombatReader]::FindBlock($handle,$signature)
        if(!$block){[PeerCombatReader]::CloseHandle($handle)>$null;throw 'Validated shared block missing'}
        $entries+=@{Pid=$processId;Peer=[uint32]$peer[0];Enemy=$local;Reference=$reference;Handle=$handle;Block=$block}
    }
    if(@($entries.Peer | Sort-Object -Unique).Count -ne 7){throw 'Duplicate peer identity'}
    Save-Json @($entries | ForEach-Object { @{Pid=$_.Pid;Peer=$_.Peer;Owner=$HostEnemyId;Local=$_.Enemy} }) 'mappings'
    foreach($entry in $entries){
        for($attempt=1;$attempt -le 2;$attempt++){
            $current=Snapshot $ProcessIds[0] $HostEnemyId ''
            Validate-World $current $before $HostEnemyId
            if((Accepted $current $entry.Peer) -gt (Accepted $before $entry.Peer)){break}
            Write-Host "Real attack: PID $($entry.Pid), peer $($entry.Peer), local $($entry.Enemy), attempt $attempt"
            $touched+=$entry
            try {
                Attack-Command $entry $entry.Enemy
                $watch=[Diagnostics.Stopwatch]::StartNew()
                while($watch.Elapsed.TotalSeconds -lt $AttackSeconds){
                    $current=Snapshot $ProcessIds[0] $HostEnemyId ('peer-'+$entry.Peer+'-latest')
                    Validate-World $current $before $HostEnemyId
                    if($current.DamageDealtEnabled -ne 0){throw 'Host damage forwarding was enabled'}
                    $client=Snapshot $entry.Pid $entry.Enemy ''
                    Validate-World $client $entry.Reference $entry.Enemy
                    if(($client.LocalPlayer.BaseHp+$client.LocalPlayer.TemporaryHp) -lt ($entry.Reference.LocalPlayer.BaseHp+$entry.Reference.LocalPlayer.TemporaryHp)){throw 'Unexpected incoming player damage'}
                    if((Accepted $current $entry.Peer) -gt (Accepted $before $entry.Peer)){break}
                    Start-Sleep -Milliseconds 200
                }
            } finally { Attack-Command $entry 0 }
        }
        if((Accepted $current $entry.Peer) -le (Accepted $before $entry.Peer)){throw "Peer $($entry.Peer) produced no real accepted hit in two bounded windows"}
    }
    # Allow outstanding acknowledged debt to settle, then demand a full quiet period.
    $watch=[Diagnostics.Stopwatch]::StartNew(); $previous=''; $equal=0
    while($watch.Elapsed.TotalSeconds -lt 5){
        $settled=Snapshot $ProcessIds[0] $HostEnemyId 'after'
        Validate-World $settled $before $HostEnemyId
        $key=Stable-Key $settled
        $equal=if($key -eq $previous){$equal+1}else{0}; $previous=$key
        if($equal -ge 2){break}; Start-Sleep -Milliseconds 500
    }
    if($equal -lt 2){throw 'Stop failed to settle combat counters/HP; need native CancelCombat stop fix'}
    & $checker -Before (Join-Path $out 'before.json') -After (Join-Path $out 'after.json') -EnemyId $HostEnemyId -PeerIds $entries.Peer -Mode Hits -OutputPath (Join-Path $out 'hits-proof.json') | Out-Null
    $watch=[Diagnostics.Stopwatch]::StartNew(); $sample=0
    while($watch.Elapsed.TotalSeconds -lt $QuietSeconds){
        Start-Sleep -Milliseconds 500; $sample++
        $null=Snapshot $ProcessIds[0] $HostEnemyId 'quiet-latest'
        & $checker -Before (Join-Path $out 'after.json') -After (Join-Path $out 'quiet-latest.json') -EnemyId $HostEnemyId -PeerIds $entries.Peer -Mode Quiet -OutputPath (Join-Path $out 'quiet-proof.json') | Out-Null
    }
    $success=$true
    Save-Json @{Passed=$true;Peers=$entries.Peer;QuietSeconds=$watch.Elapsed.TotalSeconds;QuietSamples=$sample;Enemy=$HostEnemyId} 'result'
    Write-Host 'PASS: seven real contributors, exact native HP accounting, and bounded quiet check.'
} catch {
    Save-Json @{Passed=$false;Error=$_.Exception.Message;Enemy=$HostEnemyId} 'result'
    throw
} finally {
    if(!$success){foreach($entry in @($touched | Sort-Object Pid -Unique)){try{Attack-Command $entry 0}catch{Write-Warning "STOP FAILED for PID $($entry.Pid): $_"}}}
    foreach($entry in $entries){[PeerCombatReader]::CloseHandle($entry.Handle)>$null}
}
