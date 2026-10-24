# Runs ONLY disposable rig copies on a non-input desktop. Never calls SwitchDesktop.
# The hidden worker owns the desktop handle until its game exits. No executable disk edits.
param(
    [string]$GameDirectory,
    [ValidatePattern('^kcoop[0-9]{1,2}$')][string]$MutexName = 'kcoop81',
    [ValidatePattern('^KotorCoopBackground[A-Za-z0-9_-]*$')][string]$DesktopName = 'KotorCoopBackground20261024',
    [string]$SteamConnect = '',
    [ValidateSet('raw','+connect')][string]$ConnectSyntax = 'raw',
    [switch]$Steam,
    [string]$StatePath = '',
    [switch]$Stop,
    [switch]$ShowOnPrivateDesktop,
    [switch]$Worker
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$StatePath) { $StatePath = Join-Path $artifactRoot ('background-rig/' + [guid]::NewGuid().ToString('N') + '.json') }
$StatePath = [IO.Path]::GetFullPath($StatePath)
if (!$StatePath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'StatePath must stay under artifacts.' }

if ($ShowOnPrivateDesktop) {
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $owned = Get-Process -Id $state.Id
    if ($state.Status -ne 'Running' -or $owned.StartTime.ToUniversalTime().Ticks -ne $state.StartTicks -or
        $owned.Path -ne $state.Path -or $state.Desktop -notmatch '^KotorCoopBackground[A-Za-z0-9_-]*$') { throw 'State does not identify a live owned background game.' }
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Threading;
using System.Runtime.InteropServices;
using System.ComponentModel;
public static class CoopPrivateWindow {
  delegate bool EnumWindow(IntPtr hwnd,IntPtr param);
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenDesktopW(string name,uint flags,bool inherit,uint access);
  [DllImport("user32.dll",SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetUserObjectInformationW(IntPtr obj,int index,StringBuilder info,uint size,out uint needed);
  [DllImport("user32.dll",SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktop);
  [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desktop,EnumWindow callback,IntPtr param);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
  [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd,int mode);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
  public static int Show(string name,int pid) {
    IntPtr input=OpenInputDesktop(0,false,1);
    if(input==IntPtr.Zero) throw new Win32Exception();
    try { var current=new StringBuilder(256); uint needed;
      if(!GetUserObjectInformationW(input,2,current,512,out needed)) throw new Win32Exception();
      if(String.Equals(current.ToString(),name,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Target is the input desktop; refusing.");
    } finally { CloseDesktop(input); }
    IntPtr desk=OpenDesktopW(name,0,false,0xC1); if(desk==IntPtr.Zero) throw new Win32Exception();
    int count=0; Exception failure=null;
    try {
      var thread=new Thread(()=> { try {
        if(!SetThreadDesktop(desk)) throw new Win32Exception();
        EnumDesktopWindows(desk,(hwnd,param)=> { uint owner; GetWindowThreadProcessId(hwnd,out owner);
          if(owner==(uint)pid && ShowWindowAsync(hwnd,4)) count++; return true;
        },IntPtr.Zero);
      } catch(Exception ex) { failure=ex; } });
      thread.Start(); thread.Join(); if(failure!=null) throw failure; return count;
    } finally { CloseDesktop(desk); }
  }
}
'@
    $shown = [CoopPrivateWindow]::Show($state.Desktop,[int]$state.Id)
    return [pscustomobject]@{GameId=$state.Id;PrivateDesktop=$state.Desktop;WindowsShown=$shown}
}

if ($Stop) {
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($state.Status -ne 'Running') { return $state }
    $owned = Get-Process -Id $state.Id -ErrorAction SilentlyContinue
    if ($owned) {
        if ($owned.StartTime.ToUniversalTime().Ticks -ne $state.StartTicks -or $owned.Path -ne $state.Path) {
            throw 'Recorded game PID has been reused or its executable differs; refusing cleanup.'
        }
        Stop-Process -Id $owned.Id
        $owned.WaitForExit(10000) | Out-Null
    }
    # The worker observes this specific process exit and closes the desktop handle itself.
    return [pscustomobject]@{ StoppedGameId = $state.Id; WorkerId = $state.WorkerId; StatePath = $StatePath }
}

$game = (Resolve-Path -LiteralPath $GameDirectory).Path.TrimEnd('\')
$rigRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts/live-installs')) + [IO.Path]::DirectorySeparatorChar
if (!$game.StartsWith($rigRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Only disposable artifacts/live-installs copies may be launched.' }
$exe = Join-Path $game 'swkotor.exe'
$bytes = [IO.File]::ReadAllBytes($exe)
$pe = [BitConverter]::ToInt32($bytes, 0x3c)
if ($pe -lt 64 -or $pe + 56 -gt $bytes.Length -or [BitConverter]::ToUInt32($bytes,$pe) -ne 0x4550 -or
    [BitConverter]::ToUInt32($bytes,$pe + 8) -ne 0x402BC2D9 -or
    [BitConverter]::ToUInt32($bytes,$pe + 52) -ne 0x400000) { throw 'Expected recognized KOTOR 1.0.3 image.' }
$entry = [BitConverter]::ToUInt32($bytes,$pe + 40)
if ($Steam) {
    # Exact shipped SteamStub signature, read from disk only. No unwrap or memory patch.
    if ($entry -ne 0x46D2ED -or $bytes.Length -lt 0x3DB2ED -or
        [BitConverter]::ToUInt32($bytes,0x3DB2E9) -ne [Convert]::ToUInt32('C0DEC0DE',16)) { throw 'Expected the recognized retail SteamStub wrapper.' }
    if (!(Get-Process -Name steam -ErrorAction SilentlyContinue)) { throw 'Steam must already be running; this harness never starts its UI.' }
} elseif ($entry -ne 0x2FB38D) { throw 'Expected DRM-free KOTOR 1.0.3; use explicit -Steam only for the recognized retail wrapper.' }
$ini = [IO.File]::ReadAllText((Join-Path $game 'swkotor.ini'))
$fullscreen = [regex]::Matches($ini,'(?im)^\s*FullScreen\s*=\s*(\d+)\s*$')
if (!$fullscreen.Count -or @($fullscreen | Where-Object { $_.Groups[1].Value -ne '0' }).Count -or
    $ini -notmatch '(?im)^\s*AllowWindowedMode\s*=\s*1\s*$') { throw 'Rig must already be configured FullScreen=0 and AllowWindowedMode=1.' }
if ($SteamConnect -and $SteamConnect -notmatch '^kc1/[A-Za-z0-9.\-:\[\]%]+:[0-9]{1,5}(/[A-Za-z0-9_-]+)?$') { throw 'Only a single safe co-op connect argument is supported.' }

if (!$Worker) {
    if (Test-Path -LiteralPath $StatePath) { throw 'Choose a fresh StatePath; existing state will not be overwritten.' }
    New-Item -ItemType Directory -Path (Split-Path $StatePath) -Force | Out-Null
    $workerArgs = @('-NoProfile','-File',$PSCommandPath,'-Worker','-GameDirectory',$game,'-MutexName',$MutexName,'-DesktopName',$DesktopName,'-StatePath',$StatePath)
    if ($SteamConnect) { $workerArgs += @('-SteamConnect',$SteamConnect) }
    if ($ConnectSyntax -ne 'raw') { $workerArgs += @('-ConnectSyntax',$ConnectSyntax) }
    if ($Steam) { $workerArgs += '-Steam' }
    # All path arguments are quoted, with no shell interpolation/evaluation.
    $workerArgs = @($workerArgs | ForEach-Object { if ($_ -match '"') { throw 'Quotes are not supported in rig paths.' }; '"' + $_ + '"' })
    $child = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList $workerArgs -WindowStyle Hidden -PassThru -RedirectStandardOutput ($StatePath + '.stdout.log') -RedirectStandardError ($StatePath + '.stderr.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath $StatePath) -and !$child.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100; $child.Refresh() }
    if (!(Test-Path -LiteralPath $StatePath)) { throw "Background worker did not produce state. Inspect $StatePath.stderr.log; worker PID $($child.Id)." }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($state.Status -eq 'Failed') { throw $state.Error }
    return $state
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class CoopBackgroundDesktop {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct SI {
    public int cb; public string reserved,desktop,title; public int x,y,cx,cy,xChars,yChars,fill,flags;
    public short show,reserved2Size; public IntPtr reserved2,input,output,error;
  }
  [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr process,thread; public int pid,tid; }
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktopW(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
  [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
  [DllImport("user32.dll",SetLastError=true)] static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetUserObjectInformationW(IntPtr obj,int index,StringBuilder info,uint size,out uint needed);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessW(string app,StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string cwd,ref SI si,out PI pi);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadProcessMemory(IntPtr p,IntPtr address,byte[] bytes,UIntPtr size,out UIntPtr read);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteProcessMemory(IntPtr p,IntPtr address,byte[] bytes,UIntPtr size,out UIntPtr written);
  [DllImport("kernel32.dll",SetLastError=true)] static extern bool VirtualProtectEx(IntPtr p,IntPtr address,UIntPtr size,uint protect,out uint old);
  [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
  [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr p,uint code);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  static void Check(bool ok,string step) { if(!ok) throw new Win32Exception(Marshal.GetLastWin32Error(),step); }
  public static IntPtr Open(string name) {
    IntPtr input=OpenInputDesktop(0,false,1); Check(input!=IntPtr.Zero,"OpenInputDesktop");
    try { var current=new StringBuilder(256); uint needed; Check(GetUserObjectInformationW(input,2,current,512,out needed),"input desktop name");
      if(String.Equals(current.ToString(),name,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing to launch on the input desktop.");
    } finally { CloseDesktop(input); }
    // READOBJECTS|CREATEWINDOW|CREATEMENU|ENUMERATE|WRITEOBJECTS; NO SWITCHDESKTOP.
    IntPtr desk=CreateDesktopW(name,IntPtr.Zero,IntPtr.Zero,0,0xC7,IntPtr.Zero);
    Check(desk!=IntPtr.Zero,"CreateDesktop"); return desk;
  }
  public static Process Launch(string exe,string cwd,string desktop,string mutex,string connect,string syntax,bool renameMutex) {
    var si=new SI { cb=Marshal.SizeOf<SI>(),desktop="WinSta0\\"+desktop,flags=1,show=0 };
    var cmd=new StringBuilder("\""+exe+"\""+(connect.Length==0?"":" "+(syntax=="+connect"?"+connect ":"")+"\""+connect+"\"")); PI pi;
    Check(CreateProcessW(exe,cmd,IntPtr.Zero,IntPtr.Zero,false,4|0x08000000,IntPtr.Zero,cwd,ref si,out pi),"CreateProcess suspended");
    bool resumed=false;
    try {
      if(renameMutex) {
      var address=new IntPtr(0x0073D75C); var original=new byte[8]; UIntPtr count;
      Check(ReadProcessMemory(pi.process,address,original,(UIntPtr)8,out count)&&count.ToUInt64()==8,"read mutex");
      if(Encoding.ASCII.GetString(original)!="swkotor\0") throw new InvalidOperationException("Unexpected mutex bytes; refusing to alter process.");
      var replacement=new byte[8]; Encoding.ASCII.GetBytes(mutex).CopyTo(replacement,0); uint old;
      Check(VirtualProtectEx(pi.process,address,(UIntPtr)8,4,out old),"protect mutex");
      try { Check(WriteProcessMemory(pi.process,address,replacement,(UIntPtr)8,out count)&&count.ToUInt64()==8,"write mutex"); }
      finally { uint ignored; VirtualProtectEx(pi.process,address,(UIntPtr)8,old,out ignored); }
      }
      Check(ResumeThread(pi.thread)!=uint.MaxValue,"resume game"); resumed=true;
      var owned=Process.GetProcessById(pi.pid);
      _=owned.Handle; // Retain a query handle so the exit code remains available after exit.
      return owned;
    } finally { if(!resumed) TerminateProcess(pi.process,1); CloseHandle(pi.thread); CloseHandle(pi.process); }
  }
}
'@
$desktop = [IntPtr]::Zero
$ownedGame = $null
try {
    $mutexProbe = $null
    $effectiveMutex = if ($Steam) { 'swkotor' } else { $MutexName }
    if ([Threading.Mutex]::TryOpenExisting($effectiveMutex,[ref]$mutexProbe)) { $mutexProbe.Dispose(); throw 'Game mutex already exists; refusing a conflicting launch.' }
    $desktop = [CoopBackgroundDesktop]::Open($DesktopName)
    $ownedGame = [CoopBackgroundDesktop]::Launch($exe,$game,$DesktopName,$MutexName,$SteamConnect,$ConnectSyntax,(!$Steam))
    $state = [ordered]@{Status='Running';Id=$ownedGame.Id;StartTicks=$ownedGame.StartTime.ToUniversalTime().Ticks;Path=$exe;Desktop=$DesktopName;WorkerId=$PID;WorkerStartTicks=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;StatePath=$StatePath;HasStartupInvite=[bool]$SteamConnect;RetailSteam=[bool]$Steam}
    $state | ConvertTo-Json | Set-Content -LiteralPath ($StatePath + '.pending')
    Move-Item -LiteralPath ($StatePath + '.pending') -Destination $StatePath
    $ownedGame.WaitForExit()
    $state.Status = 'Exited'
    $state.ExitCode = $ownedGame.ExitCode
    $state.ExitedUtc = [DateTime]::UtcNow.ToString('o')
    $state | ConvertTo-Json | Set-Content -LiteralPath $StatePath
} catch {
    # A failure between native launch and state publication must not leave an untracked game.
    if ($ownedGame -and !$ownedGame.HasExited) {
        $ownedGame.Kill()
        $ownedGame.WaitForExit(10000) | Out-Null
    }
    @{Status='Failed';Error=$_.Exception.Message;WorkerId=$PID;StatePath=$StatePath} | ConvertTo-Json | Set-Content -LiteralPath $StatePath
    throw
} finally {
    if ($desktop -ne [IntPtr]::Zero) { [CoopBackgroundDesktop]::CloseDesktop($desktop) | Out-Null }
}
