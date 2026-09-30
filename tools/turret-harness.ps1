# Bounded calibration or real turret shooting. Stage K_RANDOM_MINI_GAME=true -> m12ab.
# Writes only the existing plugin input-cheat mailbox, never native aim/HP or OS input.
# Native quaternion order is W,X,Y,Z (Quaternion constructor 0x4AC960); the model's
# bullet forward axis is +Y (native CSWMGBehaviorBullet::Control 0x6DB0ED).
param(
    [Parameter(Mandatory)][int]$ProcId,
    [Parameter(Mandatory)][long]$PluginBlock,
    [ValidateRange(1,65535)][uint32]$NativeVersion=85,
    [ValidateRange(20,100)][int]$SettleMs = 60,
    [switch]$Keys, [switch]$Shoot, [int]$ParkedProcId=0, [long]$ParkedBlock=0,
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if (-not ('TurretHarnessMemory' -as [type])) {
Add-Type @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public sealed class TurretHarnessMemory : IDisposable {
 [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a,bool inherit,uint pid);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h,IntPtr a,byte[] b,int n,out int count);
 [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h,IntPtr a,byte[] b,int n,out int count);
 readonly IntPtr h; readonly long block; readonly uint version; readonly Stopwatch lifetime=Stopwatch.StartNew();
 public TurretHarnessMemory(int pid,long pluginBlock,uint expectedVersion=85) { version=expectedVersion; h=OpenProcess(0x438,false,(uint)pid);block=pluginBlock;if(h==IntPtr.Zero)throw new Exception("OpenProcess failed");try{Validate();}catch{Dispose();throw;} }
 byte[] R(long a,int n) {var b=new byte[n];int count;if(a==0||!ReadProcessMemory(h,new IntPtr(a),b,n,out count)||count!=n)throw new Exception("Unreachable telemetry address 0x"+a.ToString("X"));return b;}
 uint U(long a)=>BitConverter.ToUInt32(R(a,4),0);
 float F(long a)=>BitConverter.ToSingle(R(a,4),0);
 float[] V(long a,int n){var v=new float[n];for(int i=0;i<n;i++){v[i]=F(a+i*4);if(!float.IsFinite(v[i]))throw new Exception("Non-finite native transform");}return v;}
 void W(int offset,byte[] bytes){if(offset<0x3328||offset+bytes.Length>0x33C0)throw new Exception("Write outside input mailbox refused");int count;if(!WriteProcessMemory(h,new IntPtr(block+offset),bytes,bytes.Length,out count)||count!=bytes.Length)throw new Exception("Input mailbox write failed");}
 void W32(int offset,uint v)=>W(offset,BitConverter.GetBytes(v));
 string Text(int offset,int n){var b=R(block+offset,n);int end=Array.IndexOf(b,(byte)0);return Encoding.ASCII.GetString(b,0,end<0?n:end);}
 void Validate(){if(Encoding.ASCII.GetString(R(block,8))!="KOCOOP01"||U(block+8)!=version||U(block+12)!=0xC0FFEE01)throw new Exception("Expected exact requested plugin header/guard");}
 public string Input(string arguments){
  Validate();if(arguments.Length>=32)throw new Exception("Input payload too long");
  uint serial=U(block+0x3328);if(U(block+0x3364)!=serial)throw new Exception("Input mailbox busy; refusing overwrite");
  var payload=new byte[32];Encoding.ASCII.GetBytes(arguments).CopyTo(payload,0);
  W(0x3330,new byte[20]);W(0x3344,payload);W32(0x332C,103);W32(0x3368,0);W(0x336C,new byte[64]);
  uint next=unchecked(serial+1);W32(0x3328,next);
  var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<700){
   if(U(block+0x3328)!=next)throw new Exception("Another producer replaced the input request");
   if(U(block+0x3364)==next){if(U(block+0x332C)!=103||U(block+0x3368)!=1)throw new Exception("Input ACK did not match kind103/status1");string answer=Text(0x336C,64);if((arguments.StartsWith("key ")&&!answer.StartsWith("input: key "))||(arguments.StartsWith("delta ")&&!answer.StartsWith("input: queued relative mouse delta ")))throw new Exception("Input controller refused: "+answer);return answer;}
   Thread.Sleep(2);
  }throw new Exception("Input ACK timeout; no further motion sent");
 }
 public sealed class Model {public uint Address;public float[] Position,PreviousPosition,RawQuaternionWXYZ,XDirection,YDirection,ZDirection;}
 Model ModelAt(uint p){
  if(p==0)return null;uint vt=U(p);if(U(vt+0x64)!=0x449980)return null;
  float[] q=V(p+0x84,4);float w=q[0],x=q[1],y=q[2],z=q[3];
  // Retail Quaternion::xdir44F3D0 / ydir44F420 / zdir43E250, no guessed order.
  return new Model{Address=p,Position=V(p+0x78,3),PreviousPosition=V(p+0x94,3),RawQuaternionWXYZ=q,
   XDirection=new[]{1-2*(y*y+z*z),2*(x*y+w*z),2*(x*z-w*y)},
   YDirection=new[]{2*(x*y-w*z),1-2*(x*x+z*z),2*(y*z+w*x)},
   ZDirection=new[]{2*(x*z+w*y),2*(y*z-w*x),1-2*(x*x+y*y)}};
 }
 Model FollowerModel(uint body){if(U(body+0x6C)==0)return null;uint models=U(body+0x68);return models==0?null:ModelAt(U(models));}
 public sealed class Fighter {public int Slot,Hp,MaxHp;public bool Dead;public uint Address;public float TrackSpeed;public Model Model;}
 public sealed class Sample {public long ElapsedMs;public int PlayerHp,PlayerMaxHp;public float BulletSpeed;public int[] MouseAxes;public float MovementPerSecond;public float[] AxisVelocity,RawControlTransform;public Model PlayerModel;public Model[] GunModels;public Fighter[] Fighters;}
 public Sample Snapshot(){
  Validate();uint app=U(0x7A39FC),client=U(app+4),ci=U(client+4),module=U(ci+0x18),area=U(module+0x48),mini=U(area+0x264);
  if(U(mini)!=0x752914||U(mini+0x80)!=2)throw new Exception("Expected native turret minigame type2");
  uint player=U(mini+0x24);if(U(player)!=0x7525F0||U(player+0x60)!=mini)throw new Exception("Invalid native turret player");
  var guns=new List<Model>();float bulletSpeed=0;uint banks=U(player+0x74),count=U(player+0x78);if(count>16)throw new Exception("Invalid gun count");
  for(int i=0;i<count;i++){uint bank=U(banks+i*4);uint vt=U(bank);if(vt!=0x752AE4&&vt!=0x752AF0)continue;if(bulletSpeed==0)bulletSpeed=F(bank+0x44);uint models=U(bank+0x34),nc=U(bank+0x38);if(nc>16)throw new Exception("Invalid gun model count");for(int j=0;j<nc;j++){Model model=ModelAt(U(models+j*4));if(model!=null)guns.Add(model);}}
  var fighters=new List<Fighter>();uint array=U(ci);for(int i=0;i<255;i++){uint body=U(array+4+i*4);if(body==0||U(body)!=0x7528A8||U(body+0x60)!=mini)continue;fighters.Add(new Fighter{Slot=i,Address=body,Hp=unchecked((int)U(body+0x8C)),MaxHp=unchecked((int)U(body+0x90)),Dead=(U(body+0x88)&2)!=0,TrackSpeed=F(body+0x98),Model=FollowerModel(body)});}
  var sample=new Sample{BulletSpeed=bulletSpeed,ElapsedMs=lifetime.ElapsedMilliseconds,PlayerHp=unchecked((int)U(player+0x8C)),PlayerMaxHp=unchecked((int)U(player+0x90)),PlayerModel=FollowerModel(player),GunModels=guns.ToArray(),Fighters=fighters.ToArray(),MouseAxes=new[]{unchecked((int)U(mini+0x84)),unchecked((int)U(mini+0x88))},MovementPerSecond=F(mini+0x74),AxisVelocity=V(mini+0xE4,3),RawControlTransform=V(mini+0xC0,6)};
  if(U(area+0x264)!=mini)throw new Exception("Minigame ended while sampling");if(sample.PlayerMaxHp<=0||sample.PlayerHp<sample.PlayerMaxHp/4)throw new Exception("Player below25% HP; stopping");return sample;
 }
 public sealed class WorldSample {public string Module;public uint Player,Id,MinigameLock;public float[] Position;}
 string NativeModule(){uint app=U(0x7A39FC),server=U(app+8),si=U(server+4);if(U(si+0x100B4)!=0||BitConverter.ToUInt16(R(si+0x10008,2),0)!=2)throw new Exception("Module transitioning");uint info=U(si+0x10004),ptr=U(info+0xFC),len=U(info+0x100);if(len==0||len>32)throw new Exception("Invalid module string");return Encoding.ASCII.GetString(R(ptr,(int)len)).TrimEnd('\0');}
 public WorldSample World(){Validate();uint p=U(block+0x2260);if(p==0||U(p)!=0x7470C8||U(p+4)!=U(block+0x2264))throw new Exception("Invalid current own player");return new WorldSample{Module=NativeModule(),MinigameLock=U(block+0x4704),Player=p,Id=U(p+4),Position=V(p+0x90,3)};}
 public void Dispose(){if(h!=IntPtr.Zero)CloseHandle(h);}
}
'@
}
$memory = [TurretHarnessMemory]::new($ProcId, $PluginBlock,$NativeVersion)
$samples = [Collections.Generic.List[object]]::new()
$parked=if($ParkedProcId){[TurretHarnessMemory]::new($ParkedProcId,$ParkedBlock,$NativeVersion)}else{$null}
try {
    # No held input or shots during calibration. Each command must be ACKed before sampling.
    $null = $memory.Input('key 0x39 up')
    if($Shoot) {
      $held=@{}; $targetSlot=-1; $previous=@{}; $watch=[Diagnostics.Stopwatch]::StartNew();$lastLog=-1000; $lastFire=-1000; $initialFighters=@(); $deadFighters=[Collections.Generic.HashSet[uint32]]::new()
      if($parked){$parkedBefore=$parked.World();$null=$parked.Input('key 0x11 down');$parkedReleased=$false}
      $null=$memory.Input('key 0x39 down')
      while($watch.ElapsedMilliseconds -lt 30000) {
        try {$state=$memory.Snapshot()} catch { $samples.Add([pscustomobject]@{End=$_.Exception.Message;ElapsedMs=$watch.ElapsedMilliseconds});break }
        if($watch.ElapsedMilliseconds-$lastFire -ge 150){$null=$memory.Input('key 0x39 up');$null=$memory.Input('key 0x39 down');$lastFire=$watch.ElapsedMilliseconds}
        if($parked -and !$parkedReleased -and $watch.ElapsedMilliseconds -ge 2000){$null=$parked.Input('key 0x11 up');$samples.Add([pscustomobject]@{ParkedBefore=$parkedBefore;ParkedAfter=$parked.World()});$parkedReleased=$true}
        if(!$initialFighters.Count){$initialFighters=@($state.Fighters | ForEach-Object { $_.Address });if(!$initialFighters.Count){throw 'Initial native fighter census is empty; refusing victory claim'}}
        foreach($fighter in $state.Fighters){if($fighter.Dead -or $fighter.Hp -le 0){$null=$deadFighters.Add($fighter.Address)}}
        $gun=$state.GunModels[0];$live=@($state.Fighters|Where-Object { !$_.Dead -and $_.Hp -gt 0 -and $_.Model })
        if(!$live.Count){if(@($initialFighters|Where-Object {!$deadFighters.Contains([uint32]$_)}).Count){throw 'A native fighter disappeared without a sampled death; victory unproven'};$samples.Add([pscustomobject]@{Won=$true;InitialFighterAddresses=$initialFighters;DeadFighterAddresses=@($deadFighters);State=$state});break}
        $target=$live|Where-Object Slot -eq $targetSlot|Select-Object -First 1
        if(!$target){$target=$live|Sort-Object { $dx=$_.Model.Position[0]-$gun.Position[0];$dy=$_.Model.Position[1]-$gun.Position[1];$dz=$_.Model.Position[2]-$gun.Position[2];$d=[Math]::Sqrt($dx*$dx+$dy*$dy+$dz*$dz); [Math]::Acos([Math]::Clamp(($dx*$gun.YDirection[0]+$dy*$gun.YDirection[1]+$dz*$gun.YDirection[2])/$d,-1.0,1.0))+($d/300.0) }|Select-Object -First 1;$targetSlot=$target.Slot}
        $position=@($target.Model.Position);$now=$watch.ElapsedMilliseconds; $distance=[Math]::Sqrt([Math]::Pow($position[0]-$gun.Position[0],2)+[Math]::Pow($position[1]-$gun.Position[1],2)+[Math]::Pow($position[2]-$gun.Position[2],2));$lead=if($state.BulletSpeed -gt 1 -and $state.BulletSpeed -lt 100000){[Math]::Min(2.0,$distance/$state.BulletSpeed)}else{0.08}
        if($previous.ContainsKey($targetSlot)) { $prev=$previous[$targetSlot];$dt=($now-$prev.Time)/1000.0;if($dt -gt 0 -and $dt -lt 0.5){
          $velocity=@(0.0,0.0,0.0);$r=@(0.0,0.0,0.0);$aa=-$state.BulletSpeed*$state.BulletSpeed;$bb=0.0;$cc=0.0
          for($axis=0;$axis -lt 3;$axis++){$velocity[$axis]=($target.Model.Position[$axis]-$prev.Position[$axis])/$dt;$r[$axis]=$position[$axis]-$gun.Position[$axis];$aa+=$velocity[$axis]*$velocity[$axis];$bb+=2*$r[$axis]*$velocity[$axis];$cc+=$r[$axis]*$r[$axis]}
          $disc=$bb*$bb-4*$aa*$cc
          if($disc -ge 0 -and [Math]::Abs($aa) -gt 0.001){$t1=(-$bb+[Math]::Sqrt($disc))/(2*$aa);$t2=(-$bb-[Math]::Sqrt($disc))/(2*$aa);$times=@(@($t1,$t2)|Where-Object {$_ -gt 0});if($times.Count){$lead=[Math]::Min(2.0,($times|Measure-Object -Minimum).Minimum)}}
          for($axis=0;$axis -lt 3;$axis++){$position[$axis]+=$velocity[$axis]*$lead}
        } }
        $previous[$targetSlot]=@{Time=$now;Position=@($target.Model.Position)}
        $dx=$position[0]-$gun.Position[0];$dy=$position[1]-$gun.Position[1];$dz=$position[2]-$gun.Position[2]
        $yaw=[Math]::Atan2($dx,$dy)-[Math]::Atan2($gun.YDirection[0],$gun.YDirection[1]);while($yaw -gt [Math]::PI){$yaw-=2*[Math]::PI};while($yaw -lt -[Math]::PI){$yaw+=2*[Math]::PI}
        $pitch=[Math]::Atan2($dz,[Math]::Sqrt($dx*$dx+$dy*$dy))-[Math]::Asin($gun.YDirection[2])
        $wanted=@{'0x1E'=($yaw -lt -0.015);'0x20'=($yaw -gt 0.015);'0x11'=($pitch -gt 0.015);'0x1F'=($pitch -lt -0.015)}
        foreach($key in $wanted.Keys){if([bool]$held[$key] -ne $wanted[$key]){$null=$memory.Input("key $key $(if($wanted[$key]){'down'}else{'up'})");$held[$key]=$wanted[$key]}}
        if($now-$lastLog -ge 100){$samples.Add([pscustomobject]@{ElapsedMs=$now;Target=$targetSlot;BulletSpeed=$state.BulletSpeed;LeadSeconds=$lead;Yaw=$yaw;Pitch=$pitch;PlayerHp=$state.PlayerHp;Fighters=@($state.Fighters|Select-Object Slot,Hp,Dead)});$lastLog=$now}
        Start-Sleep -Milliseconds 20
      }
      foreach($key in @('0x39','0x11','0x1E','0x1F','0x20','0xC8','0xCB','0xD0','0xCD')){try{$null=$memory.Input("key $key up")}catch{Write-Warning $_}}
      if($samples.Count -and $samples[$samples.Count-1].Won) {
        $deadline=[DateTime]::UtcNow.AddSeconds(60)
        while([DateTime]::UtcNow -lt $deadline){try{$world=$memory.World();if($world.Module -eq 'ebo_m12aa'){$samples.Add([pscustomobject]@{ScriptedReturn=$world});break}}catch{};Start-Sleep -Milliseconds 100}
        if($parked){Start-Sleep -Seconds 2;$before=$parked.World();$null=$parked.Input('key 0x11 down');Start-Sleep -Seconds 2;$null=$parked.Input('key 0x11 up');$samples.Add([pscustomobject]@{ReturnBefore=$before;ReturnAfter=$parked.World()})}
      }
    } elseif($Keys) {
      foreach($key in @('0x1E','0x20','0x11','0x1F','0xCB','0xCD','0xC8','0xD0')) {
        $before=$memory.Snapshot();$ack=$memory.Input("key $key down")
        Start-Sleep -Milliseconds 250
        $null=$memory.Input("key $key up");$after=$memory.Snapshot()
        $samples.Add([pscustomobject]@{Key=$key;Ack=$ack;Before=$before;After=$after})
      }
    } else {
    foreach ($delta in @(@(20,0),@(-20,0),@(0,20),@(0,-20))) {
        $before = $memory.Snapshot()
        if ($before.ElapsedMs -gt 10000) { throw 'Calibration deadline reached' }
        $ack = $memory.Input(('delta {0} {1}' -f $delta[0], $delta[1]))
        Start-Sleep -Milliseconds $SettleMs
        $after = $memory.Snapshot()
        $samples.Add([pscustomobject]@{ Delta = $delta; Ack = $ack; Before = $before; After = $after })
    }
    }
} finally {
    # Release held aim/fire keys on every completion or abort.
    foreach ($key in @('0x39','0x11','0x1E','0x1F','0x20','0xC8','0xCB','0xD0','0xCD')) {
        try { $null = $memory.Input("key $key up") } catch { Write-Warning $_ }
    }
    if($parked){try{$null=$parked.Input('key 0x11 up')}catch{};$parked.Dispose()}
    $memory.Dispose()
    $json = $samples | ConvertTo-Json -Depth 9
    if ($OutputPath) { $json | Set-Content -LiteralPath $OutputPath }
    $json
}
