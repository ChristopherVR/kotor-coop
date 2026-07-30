param([string]$ProcessName = 'swkotor')

Add-Type @'
using System; using System.Runtime.InteropServices;
public class R {
  [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint a, bool i, uint p);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr h, IntPtr a, byte[] b, int n, out int r);
  public static IntPtr Open(int pid){ return OpenProcess(0x0410, false, (uint)pid); }
  public static uint U32(IntPtr h, long a){ byte[] b=new byte[4]; int r;
    if(!ReadProcessMemory(h,new IntPtr(a),b,4,out r)||r!=4) return 0; return BitConverter.ToUInt32(b,0); }
}
'@

$p = Get-Process -Name $ProcessName | Select-Object -First 1
$h = [R]::Open($p.Id)

$vm = [R]::U32($h, 0x007A3A00)
$internal = [R]::U32($h, $vm)
$implementer = [R]::U32($h, $internal + 0x3D4)
$table = [R]::U32($h, $implementer + 0x0C)

"VIRTUAL_MACHINE_PTR -> vm        0x{0:X8}" -f $vm
"vm                  -> internal  0x{0:X8}" -f $internal
"internal+0x3D4      -> impl      0x{0:X8}" -f $implementer
"impl+0x0C           -> table     0x{0:X8}" -f $table
"impl vtable                      0x{0:X8}  (expect 0x007450E4)" -f ([R]::U32($h, $implementer))
""
"routine  address     matches"
foreach($n in 0,10,21,27,28,213,214,215,243,313,382,385){
  $fn = [R]::U32($h, $table + ($n*4))
  $note = switch($fn){
    0x0052F820 { 'ExecuteCommandCreateObject' }
    0x0053FE00 { 'ExecuteCommandMoveToPoint' }
    default { '' }
  }
  "  {0,4}   0x{1:X8}  {2}" -f $n, $fn, $note
}
""
"--- every routine slot that maps to ExecuteCommandMoveToPoint (0x0053FE00) ---"
$hits = @()
for($n=0; $n -lt 772; $n++){
  if([R]::U32($h, $table + ($n*4)) -eq 0x0053FE00){ $hits += $n }
}
"  routines: $($hits -join ', ')"
"--- VM idle? ---"
"  stack_pointer(+0x1AC) = $([R]::U32($h, $internal + 0x1AC))   call_stack_size(+0x1C4) = $([R]::U32($h, $internal + 0x1C4))"
"  has_caller_object(+0x04) = $([R]::U32($h, $implementer + 0x04))   caller_object_id(+0x08) = 0x$('{0:X}' -f [R]::U32($h, $implementer + 0x08))"

[R]::CloseHandle($h) | Out-Null
