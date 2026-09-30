# Shared, read-only startup gate for live rigs. Cache each shared-block address once;
# installation audit / carried-save adoption can take much longer than a fixed sleep.
function Wait-CoopRigReady {
    param([Parameter(Mandatory)][int[]]$ProcessIds, [int]$TimeoutSeconds = 120)
    if (-not ('CoopRigReader' -as [type])) {
        Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CoopRigReader {
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
 public static long FindBlock(IntPtr h){
  byte[] signature={0x4B,0x4F,0x43,0x4F,0x4F,0x50,0x30,0x31};
  for(long address=0;address<0x7FFFFFFF;){
   MBI m;if(VirtualQueryEx(h,new IntPtr(address),out m,Marshal.SizeOf(typeof(MBI)))==0)break;
   long size=(long)m.RegionSize;if(size<=0)break;
   if(m.State==0x1000&&m.Type==0x20000&&(m.Protect&0x101)==0&&size<=64L*1024*1024){
    var b=Read(h,(long)m.BaseAddress,(int)size);
    if(b!=null)for(int i=0;i+16<=b.Length;i+=4){
     bool hit=true;for(int j=0;j<8;j++)if(b[i+j]!=signature[j]){hit=false;break;}
     if(hit&&BitConverter.ToUInt32(b,i+12)==0xC0FFEE01)return (long)m.BaseAddress+i;
    }
   }
   long next=(long)m.BaseAddress+size;if(next<=address)break;address=next;
  }return 0;
 }
}
'@
    }
    $expected = $ProcessIds.Count - 1
    $targets = @()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $settledPasses = 0
    try {
        foreach ($processId in $ProcessIds) {
            $handle = [CoopRigReader]::OpenProcess(0x410, $false, $processId)
            if ($handle -eq [IntPtr]::Zero) { throw "Cannot read rig process $processId" }
            $targets += @{ Pid = $processId; Handle = $handle; Block = 0; Heartbeat = 0 }
        }
        while ([DateTime]::UtcNow -lt $deadline) {
            $allReady = $true
            $detail = @()
            foreach ($target in $targets) {
                if ($target.Block -eq 0) { $target.Block = [CoopRigReader]::FindBlock($target.Handle) }
                $remoteCount = if ($target.Block) { [CoopRigReader]::U32($target.Handle, $target.Block + 0x48) } else { 0 }
                $phantomCount = if ($target.Block) { [CoopRigReader]::U32($target.Handle, $target.Block + 0x25C) } else { 0 }
                $player = if ($target.Block) { [CoopRigReader]::U32($target.Handle, $target.Block + 0x2260) } else { 0 }
                $playerStats = if ($player) { [CoopRigReader]::U32($target.Handle, $player + 0xA74) } else { 0 }
                $heartbeat = if ($target.Block) { [CoopRigReader]::U32($target.Handle, $target.Block + 0x4C) } else { 0 }
                if ($heartbeat -eq $target.Heartbeat) { $allReady = $false }
                $target.Heartbeat = $heartbeat
                if ($remoteCount -lt $expected -or $phantomCount -lt $expected -or $playerStats -eq 0) { $allReady = $false }
                for ($peer = 0; $peer -lt $expected; $peer++) {
                    $id = if ($target.Block) { [CoopRigReader]::U32($target.Handle, $target.Block + 0x1F80 + 4 * $peer) } else { 0 }
                    $body = if ($id) { [CoopRigReader]::ResolveObject($target.Handle, $id) } else { 0 }
                    if ($body -eq 0 -or [CoopRigReader]::U32($target.Handle, $body) -ne 0x007470C8 `
                        -or [CoopRigReader]::U32($target.Handle, $body + 4) -ne $id `
                        -or [CoopRigReader]::U32($target.Handle, $body + 0xA74) -eq 0) { $allReady = $false }
                }
                $detail += "pid $($target.Pid) remotes=$remoteCount phantoms=$phantomCount playerStats=0x$($playerStats.ToString('X'))"
            }
            $settledPasses = if ($allReady) { $settledPasses + 1 } else { 0 }
            if ($settledPasses -ge 4) { return ($detail -join '; ') }
            Start-Sleep -Milliseconds 500
        }
        throw "Rig did not become ready in ${TimeoutSeconds}s: $($detail -join '; ')"
    } finally {
        foreach ($target in $targets) { [CoopRigReader]::CloseHandle($target.Handle) > $null }
    }
}
