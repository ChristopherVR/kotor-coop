# Offline proof checker. It never opens a game process or issues a game command.
param(
    [Parameter(Mandatory)][string]$Before,
    [Parameter(Mandatory)][string]$After,
    [Parameter(Mandatory)][uint32]$EnemyId,
    [Parameter(Mandatory)][uint32[]]$PeerIds,
    [ValidateSet('Hits','Quiet')][string]$Mode='Hits',
    [string]$OutputPath=''
)
$ErrorActionPreference='Stop'
if ($PeerIds.Count -ne 7 -or @($PeerIds | Sort-Object -Unique).Count -ne 7 -or $PeerIds -contains 1) {
    throw 'Supply the seven distinct joiner peer identities, excluding host peer1.'
}
$a=Get-Content -LiteralPath $Before -Raw | ConvertFrom-Json
$b=Get-Content -LiteralPath $After -Raw | ConvertFrom-Json
if (($null -ne $a.AttributedAvailable -and !$a.AttributedAvailable) -or
    ($null -ne $b.AttributedAvailable -and !$b.AttributedAvailable)) {
    throw 'NativeOnly snapshots cannot prove attributed damage or quiet replay.'
}
if ($a.Version -ge 88 -or $b.Version -ge 88) {
    foreach($snapshot in @($a,$b)) {
        if($snapshot.AttributedAvailable -ne $true -or $null -eq $snapshot.NativeLivenessEpoch -or
            $snapshot.NativeLivenessEpoch -eq 0 -or $null -eq $snapshot.AttributedNativeWorldEpoch -or
            $snapshot.AttributedNativeWorldEpoch -ne $snapshot.NativeLivenessEpoch) {
            throw 'Native88 proof requires a present, nonzero native epoch and its matching attributed bank binding.'
        }
    }
    if($a.NativeLivenessEpoch -ne $b.NativeLivenessEpoch) { throw 'Changed native world epoch.' }
}
foreach($name in @('Pid','ProcessStartTicks','Version','Epoch','WorldLoadSerial','NativeDamageIncarnation')) {
    if ($null -eq $a.$name -or $a.$name -ne $b.$name) { throw "Changed or absent host namespace field: $name" }
}
foreach($name in @('Module','ModuleEpoch','ObjectArray','ObjectTree')) {
    if ($a.NativeNamespace.$name -ne $b.NativeNamespace.$name) { throw "Changed native namespace: $name" }
}
if ($a.Version -lt 86 -or $a.DamageDealtEnabled -ne 0 -or $b.DamageDealtEnabled -ne 0) {
    throw 'Need attributed native86+ host with its own damage forwarding disabled.'
}
$ea=@($a.Enemies | Where-Object Id -eq $EnemyId)
$eb=@($b.Enemies | Where-Object Id -eq $EnemyId)
if($ea.Count -ne 1 -or $eb.Count -ne 1 -or !$ea[0].Resolved -or !$eb[0].Resolved -or
    $ea[0].Address -ne $eb[0].Address -or $ea[0].BaseHp -le 0 -or $eb[0].BaseHp -le 0) {
    throw 'Target must remain the same resolved, living host-owned native body.'
}
foreach($snapshot in @($a,$b)) {
    if (@($snapshot.Peers | Where-Object Phantom -eq $EnemyId).Count -or $snapshot.LocalPlayer.Id -eq $EnemyId) {
        throw 'Target is a player or mapped phantom, not the selected host enemy.'
    }
}
$rows=@()
foreach($peer in $PeerIds) {
    $old=@($a.Accepted | Where-Object { $_.Peer -eq $peer -and $_.Enemy -eq $EnemyId })
    $new=@($b.Accepted | Where-Object { $_.Peer -eq $peer -and $_.Enemy -eq $EnemyId })
    if($old.Count -gt 1 -or $new.Count -gt 1) { throw 'Duplicate attributed peer/enemy row.' }
    $oldTotal=if($old.Count){[long]$old[0].AcceptedCumulative}else{0L}
    $newTotal=if($new.Count){[long]$new[0].AcceptedCumulative}else{0L}
    $delta=$newTotal-$oldTotal
    if($delta -lt 0) { throw "Peer$peer history reset; cannot compare this baseline." }
    $mapped=@($b.Peers | Where-Object Peer -eq $peer)
    if($mapped.Count -ne 1 -or !$mapped[0].Native.Resolved -or !$mapped[0].Phantom) {
        throw "Peer$peer has no unique resolved host phantom at the final snapshot."
    }
    $rows+=[pscustomobject]@{Peer=$peer;Phantom=$mapped[0].Phantom;Before=$oldTotal;After=$newTotal;Delta=$delta}
}
foreach($unexpected in @($b.Accepted | Where-Object { $_.Enemy -eq $EnemyId -and $_.Peer -notin $PeerIds })) {
    $prior=@($a.Accepted | Where-Object { $_.Peer -eq $unexpected.Peer -and $_.Enemy -eq $EnemyId })
    if(!$prior.Count -or $prior[0].AcceptedCumulative -ne $unexpected.AcceptedCumulative) {
        throw 'Unexpected contributor changed; health cannot be attributed solely to the seven joiners.'
    }
}
$healthBefore=[long]$ea[0].BaseHp+[long]$ea[0].TemporaryHp
$healthAfter=[long]$eb[0].BaseHp+[long]$eb[0].TemporaryHp
$accepted=[long](($rows | Measure-Object Delta -Sum).Sum)
$loss=$healthBefore-$healthAfter
$calls=[long]$b.AppliedEffects-[long]$a.AppliedEffects
$pass=if($Mode -eq 'Hits') {
    @($rows | Where-Object Delta -le 0).Count -eq 0 -and $accepted -eq $loss -and $calls -gt 0
} else { $accepted -eq 0 -and $loss -eq 0 -and $calls -eq 0 }
$result=[pscustomobject]@{Passed=$pass;Mode=$Mode;Enemy=$EnemyId;HealthBefore=$healthBefore;
    HealthAfter=$healthAfter;NativeHealthLoss=$loss;AcceptedDamageDelta=$accepted;AppliedCallDelta=$calls;
    Peers=$rows;Note='Exact HP equality assumes no damage reduction, immunity, regeneration, host/companion attacks or unrelated effects. Calls are not HP.'}
$json=$result | ConvertTo-Json -Depth 5
if($OutputPath){[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),$json)}
$json
if(!$pass){throw 'Eight-player combat proof did not meet the requested Hits/Quiet assertion.'}
