# Exercises the early-resolution bootstrap with native x86 fixtures, without KOTOR/mod assets.
param([string]$WorkDirectory = (Join-Path $PSScriptRoot '../artifacts/loader-enhanced-startup'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'devenv.ps1') -Arch x86 -Quiet
$work=[IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Path $work -Force > $null
@'
#include <windows.h>
static DWORD calls;
BOOL WINAPI Enumerate(LPCSTR name,DWORD mode,DEVMODEA *info) {
 (void)name;(void)info; if(mode==ENUM_CURRENT_SETTINGS) calls++; return TRUE;
}
DWORD WINAPI Count(void) {return calls;}
void WINAPI Marker(int delta) {(void)delta;}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved) {
 BYTE *base; IMAGE_NT_HEADERS32 *nt; IMAGE_IMPORT_DESCRIPTOR *desc;
 (void)instance;(void)reserved;
 if(reason!=DLL_PROCESS_ATTACH)return TRUE;
 base=(BYTE*)GetModuleHandleW(NULL); nt=(IMAGE_NT_HEADERS32*)(base+((IMAGE_DOS_HEADER*)base)->e_lfanew);
 desc=(IMAGE_IMPORT_DESCRIPTOR*)(base+nt->OptionalHeader.DataDirectory[1].VirtualAddress);
 for(;desc->Name;desc++) {
  IMAGE_THUNK_DATA32 *n,*s; if(!desc->OriginalFirstThunk)continue;
  n=(IMAGE_THUNK_DATA32*)(base+desc->OriginalFirstThunk);s=(IMAGE_THUNK_DATA32*)(base+desc->FirstThunk);
  for(;n->u1.AddressOfData;n++,s++) {
   DWORD old; const char *name;
   if(IMAGE_SNAP_BY_ORDINAL32(n->u1.Ordinal))continue;
   name=(const char*)((IMAGE_IMPORT_BY_NAME*)(base+n->u1.AddressOfData))->Name;
   if(lstrcmpA(name,"EnumDisplaySettingsA"))continue;
   if(!VirtualProtect(&s->u1.Function,4,PAGE_READWRITE,&old))return FALSE;
   s->u1.Function=(DWORD)Enumerate;VirtualProtect(&s->u1.Function,4,old,&old);
  }
 }
 return TRUE;
}
'@ | Set-Content (Join-Path $work 'renderer.c')
@'
EXPORTS
 KotorEnhancedMouseWheel=Marker
 PrimeCount=Count
'@ | Set-Content (Join-Path $work 'renderer.def')
@'
#include <windows.h>
typedef DWORD (WINAPI *Count)(void);
/* Keep the native guard address inside this fixture's mapped executable. */
static volatile BYTE imagePadding[0x210000];
__declspec(noinline) LPSTR command_line(void) { return GetCommandLineA(); }
static HMODULE import_owner(const char *wanted) {
 BYTE *b=(BYTE*)GetModuleHandleW(NULL);IMAGE_NT_HEADERS32 *n=(IMAGE_NT_HEADERS32*)(b+((IMAGE_DOS_HEADER*)b)->e_lfanew);
 IMAGE_IMPORT_DESCRIPTOR *d=(IMAGE_IMPORT_DESCRIPTOR*)(b+n->OptionalHeader.DataDirectory[1].VirtualAddress);
 for(;d->Name;d++) {
  IMAGE_THUNK_DATA32 *t,*s;if(!d->OriginalFirstThunk)continue;
  t=(IMAGE_THUNK_DATA32*)(b+d->OriginalFirstThunk);s=(IMAGE_THUNK_DATA32*)(b+d->FirstThunk);
  for(;t->u1.AddressOfData;t++,s++) {
   HMODULE owner=NULL;if(IMAGE_SNAP_BY_ORDINAL32(t->u1.Ordinal))continue;
   if(lstrcmpA((const char*)((IMAGE_IMPORT_BY_NAME*)(b+t->u1.AddressOfData))->Name,wanted))continue;
   GetModuleHandleExW(6,(LPCWSTR)s->u1.Function,&owner);return owner;
  }
 }
 return NULL;
}
void entry(void) {
 const char *original=command_line(); HMODULE renderer=NULL,loader; Count count=NULL;
 BOOL present=GetFileAttributesW(L"opengl32.dll")!=INVALID_FILE_ATTRIBUTES;
 BOOL recognized=GetFileAttributesW(L"recognized")!=INVALID_FILE_ATTRIBUTES;
 BOOL late=GetFileAttributesW(L"late")!=INVALID_FILE_ATTRIBUTES;
 BOOL mismatch=GetFileAttributesW(L"mismatch")!=INVALID_FILE_ATTRIBUTES;
 BYTE *guard=(BYTE*)0x5F198A; DWORD protect; unsigned i;
 static const BYTE originalGuard[]={0x3B,0xC6,0x7E,0x04,0x3B,0xD1,0x7F,0x0F,0x33,0xD2,0x8A,0x15,0xE0,0xD1,0x78,0x00,0x6A,0x01,0x6A,0x01,0x52,0xEB,0x0F,0x8B,0x44,0x24,0x0C};
 imagePadding[0]=1;imagePadding[sizeof(imagePadding)-1]=1;
 if(!VirtualProtect(guard,sizeof(originalGuard),PAGE_READWRITE,&protect))ExitProcess(16);
 for(i=0;i<sizeof(originalGuard);i++)guard[i]=originalGuard[i];
 if(mismatch)guard[0]^=1;
 VirtualProtect(guard,sizeof(originalGuard),protect,&protect);
 if(present&&!late)renderer=LoadLibraryW(L".\\opengl32.dll");
 loader=LoadLibraryW(L".\\dinput8.dll");if(!loader)ExitProcess(1);
 if(!GetProcAddress(loader,"KotorCoopLoaderMarker"))ExitProcess(13);
 if(recognized) {
  BYTE *b=(BYTE*)GetModuleHandleW(NULL);IMAGE_NT_HEADERS32 *n=(IMAGE_NT_HEADERS32*)(b+((IMAGE_DOS_HEADER*)b)->e_lfanew);
  if(n->FileHeader.TimeDateStamp!=0x402BC2D9)ExitProcess(14);
  if(n->OptionalHeader.ImageBase!=0x400000)ExitProcess(15);
 }
 if(present&&late)renderer=LoadLibraryW(L".\\opengl32.dll");
 if(present) {if(!renderer)ExitProcess(2);count=(Count)GetProcAddress(renderer,"PrimeCount");if(!count)ExitProcess(3);}
 if(renderer && GetModuleHandleW(L"opengl32.dll")!=renderer)ExitProcess(9);
 if(renderer && !GetProcAddress(renderer,"KotorEnhancedMouseWheel"))ExitProcess(10);
 if(recognized && import_owner("GetCommandLineA")!=loader)ExitProcess(11);
 if(renderer && import_owner("EnumDisplaySettingsA")!=renderer)ExitProcess(12);
 if(original!=command_line())ExitProcess(4);
 if(*(WORD*)(guard+8)!=(recognized&&present&&!mismatch?0x0DEB:0xD233))ExitProcess(17);
 /* Every surrounding byte, including a mismatched signature, must be preserved. */
 for(i=0;i<sizeof(originalGuard);i++) {
  BYTE expected=originalGuard[i];if(i==0&&mismatch)expected^=1;
  if(i!=8&&i!=9&&guard[i]!=expected)ExitProcess(18);
 }
 if(count&&count()!=(recognized?1u:0u))ExitProcess(5);
 if(original!=command_line())ExitProcess(6);
 if(count&&count()!=(recognized?1u:0u))ExitProcess(7);
 /* Retain the import and verify Enhanced still owns it after our one-shot bootstrap. */
 if(count) {EnumDisplaySettingsA(NULL,ENUM_CURRENT_SETTINGS,NULL);if(count()!=(recognized?2u:1u))ExitProcess(8);}
 ExitProcess(0);
}
'@ | Set-Content (Join-Path $work 'probe.c')
Push-Location $work
try {
 & cl.exe /nologo /W4 /WX /O1 /GS- /Zl /c renderer.c probe.c
 if($LASTEXITCODE){throw 'Fixture compilation failed'}
 & link.exe /nologo /DLL /NODEFAULTLIB /ENTRY:DllMain /MACHINE:X86 /SAFESEH:NO /DEF:renderer.def /OUT:opengl32.dll renderer.obj kernel32.lib
 if($LASTEXITCODE){throw 'Renderer fixture link failed'}
 & link.exe /nologo /NODEFAULTLIB /ENTRY:entry /SUBSYSTEM:CONSOLE /MACHINE:X86 /BASE:0x400000 /DYNAMICBASE:NO /SAFESEH:NO /OUT:probe.exe probe.obj kernel32.lib user32.lib
 if($LASTEXITCODE){throw 'Probe link failed'}
 foreach($case in @('recognized-present','recognized-late','recognized-absent','unknown-present','recognized-mismatch')) {
  $dir=Join-Path $work ($case+'-'+[Guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory $dir > $null
  $bytes=[IO.File]::ReadAllBytes((Join-Path $work 'probe.exe'))
  if($case.StartsWith('recognized')) {
   $pe=[BitConverter]::ToInt32($bytes,0x3c)
   [BitConverter]::GetBytes([uint32]0x402BC2D9).CopyTo($bytes,$pe+8)
   Set-Content (Join-Path $dir 'recognized') 'fixture'
  }
  [IO.File]::WriteAllBytes((Join-Path $dir 'probe.exe'),$bytes)
  if(!$case.EndsWith('absent')) {Copy-Item opengl32.dll $dir}
  if($case.EndsWith('late')) {Set-Content (Join-Path $dir 'late') 'fixture'}
  if($case.EndsWith('mismatch')) {Set-Content (Join-Path $dir 'mismatch') 'fixture'}
  Copy-Item (Join-Path $PSScriptRoot '../src/KotorCoop.Loader/bin/dinput8.dll') $dir
  $p=Start-Process (Join-Path $dir 'probe.exe') -WorkingDirectory $dir -WindowStyle Hidden -PassThru
  if(!$p.WaitForExit(10000)){Stop-Process -Id $p.Id;throw "Timeout: $case"}
  if($p.ExitCode -ne 0){throw "Failed: $case, exit $($p.ExitCode)"}
  "PASS: $case; command line/bootstrap/imports preserved, windowed guard scoped and signature checked"
 }
} finally {Pop-Location}
