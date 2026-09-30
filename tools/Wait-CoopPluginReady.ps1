# Read-only gate before a startup save load; movies may stop the plugin frame loop.
function Wait-CoopPluginReady {
 param([Parameter(Mandatory)][int]$ProcessId,[ValidateRange(1,120)][int]$TimeoutSeconds=60)
 if (-not ('CoopStartupReader' -as [type])) {
  Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CoopStartupReader {
 [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a,bool inherit,uint pid);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
 [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h,IntPtr a,byte[] b,int n,out int read);
 [DllImport("kernel32.dll")] static extern int VirtualQueryEx(IntPtr h,IntPtr a,out MBI m,int length);
 [StructLayout(LayoutKind.Sequential)] struct MBI {public IntPtr BaseAddress,AllocationBase;public uint AllocationProtect,Padding1;public UIntPtr RegionSize;public uint State,Protect,Type,Padding2;}
 public static byte[] Read(IntPtr h,long a,int n){var b=new byte[n];int r;return ReadProcessMemory(h,new IntPtr(a),b,n,out r)&&r==n?b:null;}
 public static uint U32(IntPtr h,long a){var b=Read(h,a,4);return b==null?0:BitConverter.ToUInt32(b,0);}
 public static long ResolveObject(IntPtr h,uint id){
  uint app=U32(h,0x007A39FC);if(app==0)return 0;
  uint server=U32(h,app+8);if(server==0)return 0;
  uint inside=U32(h,server+4);if(inside==0)return 0;
  uint array=U32(h,inside+0x1005C);if(array==0)return 0;
  uint tree=U32(h,array);if(tree==0)return 0;
  uint bucket=(((id>>31)&1)<<12)|(id&0xFFF);uint entry=U32(h,tree+bucket*4);
  for(int i=0;entry!=0&&i<64;i++){
   if(U32(h,entry)==(id&0x7FFFFFFF))return U32(h,entry+4);
   entry=U32(h,entry+8);
  }return 0;
 }
 public static long FindBlock(IntPtr h,uint version){
  byte[] signature={0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31};
  for(long address=0;address<0x7FFFFFFF;){
   MBI m;if(VirtualQueryEx(h,new IntPtr(address),out m,Marshal.SizeOf(typeof(MBI)))==0)break;
   long size=(long)m.RegionSize;if(size<=0)break;
   if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
    var b=Read(h,(long)m.BaseAddress,(int)size);
    if(b!=null)for(int i=0;i+16<=b.Length;i+=4){
     bool hit=true;for(int j=0;j<8;j++)if(b[i+j]!=signature[j]){hit=false;break;}
     if(hit&&BitConverter.ToUInt32(b,i+8)==version&&BitConverter.ToUInt32(b,i+12)==0xC0FFEE01)return (long)m.BaseAddress+i;
    }
   }
   long next=(long)m.BaseAddress+size;if(next<=address)break;address=next;
  }return 0;
 }
}
"@
 }
 $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/KotorCoop.Memory/KotorGameBridge.cs'))
 if ($source -notmatch 'PluginSignature = "([0-9A-F ]+)"') { throw 'Cannot determine current plugin signature.' }
 $signature = [byte[]]@($Matches[1].Split(' ') | ForEach-Object { [Convert]::ToByte($_,16) })
 $version = [BitConverter]::ToUInt32($signature,8)
 $handle = [CoopStartupReader]::OpenProcess(0x410,$false,$ProcessId)
 if ($handle -eq [IntPtr]::Zero) { throw "Cannot read startup process $ProcessId" }
 $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
 $block=0L; $heartbeat=0; $passes=0
 try {
  while ([DateTime]::UtcNow -lt $deadline) {
   if (!$block) { $block=[CoopStartupReader]::FindBlock($handle,$version) }
   $next = if($block) { [CoopStartupReader]::U32($handle,$block+0x4C) } else { 0 }
   $app=[CoopStartupReader]::U32($handle,0x7A39FC)
   $client=if($app) { [CoopStartupReader]::U32($handle,$app+4) } else { 0 }
   $inside=if($client) { [CoopStartupReader]::U32($handle,$client+4) } else { 0 }
   $menu=if($inside) { [CoopStartupReader]::U32($handle,$inside+0x280) } else { 0 }
   # BTN_WARP is embedded, its verified CSWGuiButton vtable confirms constructed menu.
   $valid = $block -and [CoopStartupReader]::U32($handle,$block+8) -eq $version -and [CoopStartupReader]::U32($handle,$block+12) -eq 0xC0FFEE01L
   $ready = $valid -and $menu -and [CoopStartupReader]::U32($handle,$menu+0x1248) -eq 0x73E658 -and $next -ne $heartbeat
   $passes=if($ready) { $passes+1 } else { 0 }; $heartbeat=$next
   if($passes -ge 3) { return "pid $ProcessId startup ready: plugin v$version block=0x$($block.ToString('X')), moving frames, constructed main menu" }
   Start-Sleep -Milliseconds 200
  }
  throw "Startup process $ProcessId did not reach a constructed main menu with moving plugin v$version frames in ${TimeoutSeconds}s (block=$block, heartbeat=$heartbeat, menu=$menu, vtable=$([CoopStartupReader]::U32($handle,$menu+0x1248)), valid=$valid)."
 } finally { [CoopStartupReader]::CloseHandle($handle) > $null }
}