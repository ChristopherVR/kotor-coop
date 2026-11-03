# Native x86 regression for issue #1. No game needed. Optionally exercise an actual
# Ultimate ASI Loader binary; CI uses a basename-sensitive stand-in with the same route.
param(
    [string]$WorkDirectory = (Join-Path $PSScriptRoot '../artifacts/loader-asi-test'),
    [string]$AsiLoader
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'devenv.ps1') -Arch x86 -Quiet
$work = [IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Force $work > $null
@'
#include <windows.h>
static HMODULE self;
HRESULT WINAPI Create(HINSTANCE a,DWORD b,REFIID c,LPVOID *d,LPVOID e) {
 WCHAR path[MAX_PATH],*name; HMODULE next; FARPROC proc;
 GetModuleFileNameW(self,path,MAX_PATH);
 name=path; {WCHAR *p=path;for(;*p;p++)if(*p==L'\\')name=p+1;}
 if(lstrcmpiW(name,L"dinput8.dll")) ExitProcess(11);
 next=LoadLibraryW(L"dinput8Hooked.dll");
 if(!next||!LoadLibraryW(L"issue1.asi")) ExitProcess(12);
 proc=GetProcAddress(next,"DirectInput8Create");
 return proc?((HRESULT (WINAPI*)(HINSTANCE,DWORD,REFIID,LPVOID*,LPVOID))proc)(a,b,c,d,e):E_FAIL;
}
BOOL WINAPI DllMain(HINSTANCE a,DWORD b,LPVOID c){(void)c;if(b==DLL_PROCESS_ATTACH)self=a;return TRUE;}
'@ | Set-Content (Join-Path $work 'host.c')
@'
#include <windows.h>
HRESULT WINAPI Create(HINSTANCE a,DWORD b,REFIID c,LPVOID *d,LPVOID e){(void)a;(void)b;(void)c;(void)d;(void)e;return 117;}
BOOL WINAPI DllMain(HINSTANCE a,DWORD b,LPVOID c){(void)a;(void)b;(void)c;return TRUE;}
'@ | Set-Content (Join-Path $work 'primary.c')
@'
#include <windows.h>
BOOL WINAPI DllMain(HINSTANCE a,DWORD b,LPVOID c){(void)a;(void)c;if(b==DLL_PROCESS_ATTACH)SetEnvironmentVariableW(L"ISSUE1_ASI_LOADED",L"yes");return TRUE;}
'@ | Set-Content (Join-Path $work 'asi.c')
@'
EXPORTS
 DirectInput8Create=Create
'@ | Set-Content (Join-Path $work 'input.def')
@'
#include <windows.h>
typedef HRESULT (WINAPI *Create)(HINSTANCE,DWORD,REFIID,LPVOID*,LPVOID);
void entry(void){
 WCHAR value[8]; HMODULE m; Create create;
 SetEnvironmentVariableW(L"ISSUE1_ASI_LOADED",NULL);
 m=LoadLibraryW(L"dinput8.dll");
 if(!m)ExitProcess(1);
 create=(Create)GetProcAddress(m,"DirectInput8Create");
 if(!create||create(NULL,0,NULL,NULL,NULL)!=117)ExitProcess(2);
 if(!GetEnvironmentVariableW(L"ISSUE1_ASI_LOADED",value,8))ExitProcess(3);
 ExitProcess(0);
}
'@ | Set-Content (Join-Path $work 'probe.c')
Push-Location $work
try {
    & cl.exe /nologo /W4 /WX /O1 /GS- /Zl /c host.c primary.c asi.c probe.c
    if ($LASTEXITCODE) { throw 'fixture compilation failed' }
    foreach ($name in @('host','primary','asi')) {
        $def = @(); if ($name -ne 'asi') { $def = @('/DEF:input.def') }
        & link.exe /nologo /DLL /NODEFAULTLIB /ENTRY:DllMain /MACHINE:X86 /SAFESEH:NO @def "/OUT:$name.dll" "$name.obj" kernel32.lib
        if ($LASTEXITCODE) { throw "fixture link failed: $name" }
    }
    & link.exe /nologo /NODEFAULTLIB /ENTRY:entry /SUBSYSTEM:CONSOLE /MACHINE:X86 /SAFESEH:NO /OUT:probe.exe probe.obj kernel32.lib
    if ($LASTEXITCODE) { throw 'probe link failed' }
    $dir = Join-Path $work ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $dir > $null
    Copy-Item probe.exe $dir
    Copy-Item $(if ($AsiLoader) { $AsiLoader } else { 'host.dll' }) (Join-Path $dir 'dinput8.dll')
    Copy-Item primary.dll (Join-Path $dir 'KotorCoop.Plugin.dll')
    Copy-Item asi.dll (Join-Path $dir 'issue1.asi')
    Copy-Item (Join-Path $PSScriptRoot '../src/KotorCoop.Loader/bin/dinput8.dll') (Join-Path $dir 'dinput8Hooked.dll')
    'KotorCoop.Plugin.dll' | Set-Content (Join-Path $dir 'dinput8.chain')
    $p = Start-Process (Join-Path $dir 'probe.exe') -WorkingDirectory $dir -WindowStyle Hidden -PassThru
    if (!$p.WaitForExit(15000)) { Stop-Process -Id $p.Id; throw 'ASI host test timed out' }
    if ($p.ExitCode -ne 0) { throw "ASI host test failed: exit $($p.ExitCode)" }
    'PASS: ASI host -> dinput8Hooked loader -> primary proxy; root ASI loaded'
} finally { Pop-Location }
