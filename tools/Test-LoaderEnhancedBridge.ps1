# Native x86 regression: optional Enhanced exports must reach a secondary chain DLL,
# while DirectInput still goes to the primary proxy. No game or third-party assets needed.
param([string]$WorkDirectory = (Join-Path $PSScriptRoot '../artifacts/loader-enhanced-test'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'devenv.ps1') -Arch x86 -Quiet
$work = [IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Path $work -Force > $null
@'
#include <windows.h>
DWORD WINAPI Count(void) { return 42; }
DWORD WINAPI Serial(void) { return 73; }
HRESULT WINAPI Create(HINSTANCE a,DWORD b,REFIID c,LPVOID *d,LPVOID e) { (void)a;(void)b;(void)c;(void)d;(void)e;return 117; }
BOOL WINAPI DllMain(HINSTANCE a,DWORD b,LPVOID c) { (void)a;(void)b;(void)c;return TRUE; }
'@ | Set-Content (Join-Path $work 'fixture.c')
@'
EXPORTS
 DirectInput8Create=Create
'@ | Set-Content (Join-Path $work 'primary.def')
@'
EXPORTS
 KotorEnhancedInputDeviceCount=Count
 KotorEnhancedPhysicalMouseSerial=Serial
'@ | Set-Content (Join-Path $work 'enhanced.def')
@'
#include <windows.h>
typedef DWORD (WINAPI *Counter)(void);
typedef HRESULT (WINAPI *Create)(HINSTANCE,DWORD,REFIID,LPVOID*,LPVOID);
void entry(void) {
 HMODULE m=LoadLibraryW(L"dinput8.dll");
 Counter count,serial; Create create; DWORD expected;
 if(!m) ExitProcess(1);
 count=(Counter)GetProcAddress(m,"KotorEnhancedInputDeviceCount");
 serial=(Counter)GetProcAddress(m,"KotorEnhancedPhysicalMouseSerial");
 create=(Create)GetProcAddress(m,"DirectInput8Create");
 if(!count||!serial||!create) ExitProcess(2);
 expected=GetFileAttributesW(L"enhanced.dll")!=INVALID_FILE_ATTRIBUTES?42:0;
 /* Resolve through an optional export first: its provider follows the primary proxy. */
 if(count()!=expected||serial()!=(expected?73u:0u)) ExitProcess(3);
 if(create(NULL,0,NULL,NULL,NULL)!=117) ExitProcess(4);
 if(count()!=expected) ExitProcess(5);
 ExitProcess(0);
}
'@ | Set-Content (Join-Path $work 'probe.c')
Push-Location $work
try {
 & cl.exe /nologo /W4 /WX /O1 /GS- /Zl /c fixture.c probe.c
 if($LASTEXITCODE){throw 'fixture compilation failed'}
 foreach($name in @('primary','enhanced')) {
  & link.exe /nologo /DLL /NODEFAULTLIB /ENTRY:DllMain /MACHINE:X86 /SAFESEH:NO "/DEF:$name.def" "/OUT:$name.dll" fixture.obj kernel32.lib
  if($LASTEXITCODE){throw 'fixture DLL link failed'}
 }
 & link.exe /nologo /NODEFAULTLIB /ENTRY:entry /SUBSYSTEM:CONSOLE /MACHINE:X86 /SAFESEH:NO /OUT:probe.exe probe.obj kernel32.lib
 if($LASTEXITCODE){throw 'probe link failed'}
 foreach($case in @('present','absent')) {
  $dir=Join-Path $work ($case + '-' + [Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory $dir > $null
  Copy-Item probe.exe,primary.dll $dir
  Copy-Item (Join-Path $PSScriptRoot '../src/KotorCoop.Loader/bin/dinput8.dll') $dir
  $chain=@('primary.dll')
  if($case -eq 'present'){Copy-Item enhanced.dll $dir;$chain+='enhanced.dll'}
  $chain | Set-Content (Join-Path $dir 'dinput8.chain')
  $process=Start-Process (Join-Path $dir 'probe.exe') -WorkingDirectory $dir -WindowStyle Hidden -PassThru
  if(!$process.WaitForExit(10000)){Stop-Process -Id $process.Id;throw "Loader test timed out: $case"}
  if($process.ExitCode -ne 0){throw "Loader test failed: $case, exit $($process.ExitCode)"}
  "PASS: Enhanced counters $case; DirectInput still forwarded to first proxy"
 }
} finally {Pop-Location}
