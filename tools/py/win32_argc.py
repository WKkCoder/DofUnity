#!/usr/bin/env python3
"""Win32 接口的 stdcall 参数个数(按公开的 Win32 文档手工整理)。
模拟器遇到导入函数时要替它清栈:stdcall 由被调用方清掉参数,个数错了栈就乱了。
没登记的接口模拟器会停下来报"未登记接口",不会悄悄按 0 个处理。
cdecl 的接口(msvcr71.dll 里的、wsprintfW)由调用方清栈,用 CDECL 集合标出。
"""

_TEXT = """
kernel32:
  CompareStringA=6 CompareStringW=6 CopyFileW=3 CreateEventA=4 CreateFileA=7 CreateFileW=7 CreateFileMappingA=6
  CreateFileMappingW=6 CreateMutexA=3 CreateMutexW=3 CreateProcessA=10 CreateThread=6 CreateToolhelp32Snapshot=2
  DeleteFileA=1 DeleteFileW=1 DeviceIoControl=8 EnumSystemLocalesA=2 ExitThread=1 FileTimeToLocalFileTime=2
  FileTimeToSystemTime=2 FindClose=1 FindFirstFileA=2 FindFirstFileW=2 FindFirstFileExA=6 FindFirstFileExW=6
  FindNextFileA=2 FindNextFileW=2 FindResourceExW=4 FlushInstructionCache=3 FormatMessageW=7 FreeResource=1
  GetComputerNameA=2 GetConsoleCP=0 GetConsoleMode=2 GetDateFormatA=6 GetDateFormatW=6 GetDiskFreeSpaceA=5
  GetDiskFreeSpaceExW=4 GetDriveTypeW=1 GetExitCodeProcess=2 GetExitCodeThread=2 GetFileAttributesA=1
  GetFileAttributesW=1 GetFileInformationByHandle=2 GetFileSize=2 GetFileTime=4 GetFullPathNameA=4
  GetPriorityClass=1 GetPrivateProfileIntW=4 GetPrivateProfileStringA=6 GetPrivateProfileStructW=5
  GetProcessTimes=5 GetSystemDefaultLangID=0 GetSystemDirectoryA=2 GetSystemDirectoryW=2
  GetSystemWindowsDirectoryW=2 GetTempPathA=2 GetThreadContext=2 GetThreadPriority=1 GetTimeFormatA=6
  GetTimeFormatW=6 GetWindowsDirectoryA=2 GetWindowsDirectoryW=2 GlobalAlloc=2 GlobalFree=1 GlobalLock=1
  GlobalMemoryStatus=1 GlobalMemoryStatusEx=1 GlobalUnlock=1 IsBadReadPtr=2 LoadResource=2 LocalAlloc=2
  LocalFree=1 LockResource=1 MapViewOfFile=5 MapViewOfFileEx=6 MoveFileW=2 OpenProcess=3 OpenThread=3
  PeekNamedPipe=6 Process32First=2 Process32Next=2 Process32FirstW=2 Process32NextW=2 ReadFile=5 ReleaseMutex=1
  ResumeThread=1 SuspendThread=1 SetCurrentDirectoryW=1 SetEndOfFile=1 SetPriorityClass=2 SetStdHandle=2
  SetThreadContext=2 SetThreadPriority=2 SizeofResource=2 SystemTimeToFileTime=2 TerminateThread=2
  Thread32First=2 Thread32Next=2 TryEnterCriticalSection=1 TzSpecificLocalTimeToSystemTime=3 UnmapViewOfFile=1
  VirtualLock=2 VirtualQueryEx=4 VirtualUnlock=2 WaitForSingleObject=2 WriteConsoleW=5
  WritePrivateProfileStringW=4 WritePrivateProfileStructW=5 lstrcmpW=2 lstrcmpiW=2 lstrcpynW=3 lstrlen=1
user32:
  CharNextW=1 ClientToScreen=2 ClipCursor=1 CloseClipboard=0 CopyRect=2 CreateWindowExW=12 DefWindowProcW=4
  DestroyCursor=1 DisableProcessWindowsGhosting=0 DispatchMessageW=1 EnableWindow=2 EnumChildWindows=3
  EnumWindows=2 FillRect=3 FindWindowA=2 FlashWindowEx=1 GetAsyncKeyState=1 GetCapture=0 GetClassNameA=3
  GetClientRect=2 GetClipboardData=1 GetCursorPos=1 GetDC=1 GetIconInfo=2 GetKeyState=1 GetKeyboardLayout=1
  GetParent=1 GetSysColorBrush=1 GetTopWindow=1 GetWindow=2 GetWindowDC=1 GetWindowInfo=2 GetWindowLongW=2
  GetWindowRect=2 GetWindowTextA=3 GetWindowTextLengthW=1 GetWindowTextW=3 GetWindowThreadProcessId=2
  IntersectRect=3 InvalidateRect=3 IsIconic=1 IsWindow=1 IsWindowVisible=1 IsZoomed=1 LoadCursorW=2 LoadIconW=2
  OffsetRect=3 OpenClipboard=1 PeekMessageW=5 PostMessageW=4 PostQuitMessage=1 PtInRect=3 RedrawWindow=4
  RegisterClassExW=1 ReleaseCapture=0 ReleaseDC=2 ScreenToClient=2 SendMessageA=4 SendMessageW=4 SetCapture=1
  SetCursor=1 SetCursorPos=2 SetFocus=1 SetForegroundWindow=1 SetRect=5 SetRectEmpty=1 SetWindowLongW=3
  SetWindowPos=7 ShowCursor=1 ShowWindow=2 SystemParametersInfoW=4 TranslateMessage=1 UpdateWindow=1
gdi32:
  AddFontResourceW=1 BitBlt=9 CreateBitmap=5 CreateCompatibleDC=1 CreateDIBSection=6 CreateFontW=14 DeleteDC=1
  DeleteObject=1 GetCharABCWidthsW=4 GetDIBits=7 GetObjectA=3 GetObjectW=3 GetStockObject=1
  GetTextExtentPoint32W=4 RemoveFontResourceW=1 SelectObject=2 SetBkMode=2 SetTextColor=2 TextOutW=5
advapi32:
  CloseServiceHandle=1 ControlService=3 DeleteService=1 OpenSCManagerW=3 OpenServiceW=3 RegCloseKey=1
  RegOpenKeyExA=5 RegOpenKeyExW=5 RegQueryValueExA=6 RegQueryValueExW=6
comdlg32:
  GetOpenFileNameW=1
d3d9:
  Direct3DCreate9=1
d3dx9_43:
  D3DXLoadSurfaceFromSurface=8 D3DXMatrixInverse=3 D3DXMatrixMultiply=3 D3DXMatrixOrthoOffCenterLH=7
  D3DXMatrixRotationY=2 D3DXMatrixRotationZ=2 D3DXMatrixScaling=4 D3DXMatrixTranslation=4
  D3DXSaveSurfaceToFileW=5 D3DXVec3TransformCoord=3
dbghelp:
  MiniDumpWriteDump=7
ddraw:
  DirectDrawCreateEx=4
dinput8:
  DirectInput8Create=5
ijl15:
  ijlFree=1 ijlInit=1 ijlWrite=2
imm32:
  ImmGetCandidateListW=4 ImmGetCompositionStringW=4 ImmGetContext=1 ImmGetConversionStatus=3
  ImmGetDefaultIMEWnd=1 ImmGetDescriptionW=3 ImmGetIMEFileNameA=3 ImmGetIMEFileNameW=3 ImmGetOpenStatus=1
  ImmGetProperty=2 ImmIsIME=1 ImmNotifyIME=4 ImmReleaseContext=2 ImmSetCompositionWindow=2
  ImmSetConversionStatus=3
iphlpapi:
  GetAdaptersInfo=2 GetTcpTable=3
ole32:
  CoCreateInstance=5 CoInitialize=1 CoInitializeEx=2 CoTaskMemFree=1 CoUninitialize=0 OleCreate=7
  OleInitialize=1 OleSetContainedObject=2 OleUninitialize=0
oleaut32:
  SysAllocString=1 SysFreeString=1 VariantChangeType=4 VariantClear=1 VariantInit=1
psapi:
  EnumProcessModules=4 EnumProcesses=3 GetModuleBaseNameA=4 GetModuleFileNameExA=4 GetModuleFileNameExW=4
  GetModuleInformation=4
shell32:
  SHCreateDirectoryExW=3 SHGetFolderPathW=5 SHGetPathFromIDListW=2 SHGetSpecialFolderLocation=3
  ShellExecuteExW=1 ShellExecuteW=6
shlwapi:
  PathAppendW=2 PathFileExistsW=1 PathFindFileNameW=1 PathRemoveFileSpecW=1 PathSearchAndQualifyW=3 StrStrIW=2
urlmon:
  URLDownloadToCacheFileW=5
version:
  GetFileVersionInfoA=4 GetFileVersionInfoW=4 GetFileVersionInfoSizeA=2 GetFileVersionInfoSizeW=2
  VerQueryValueA=4 VerQueryValueW=4
wininet:
  FtpCreateDirectoryW=2 FtpOpenFileW=5 HttpAddRequestHeadersW=4 HttpEndRequestW=4 HttpOpenRequestW=8
  HttpQueryInfoW=5 HttpSendRequestA=5 HttpSendRequestW=5 HttpSendRequestExW=5 InternetAttemptConnect=1
  InternetCloseHandle=1 InternetConnectW=8 InternetReadFile=4 InternetWriteFile=4
winmm:
  timeEndPeriod=1
ws2_32:
  WSAAsyncSelect=4 WSACleanup=0 WSACloseEvent=1 WSACreateEvent=0 WSAEnumNetworkEvents=3 WSAEventSelect=3
  WSAGetLastError=0 WSARecvFrom=9 WSAResetEvent=1 WSAStartup=2 WSAWaitForMultipleEvents=5 bind=3 closesocket=1
  connect=3 gethostbyname=1 gethostname=2 htonl=1 htons=1 inet_addr=1 inet_ntoa=1 ioctlsocket=3 recv=4
  recvfrom=6 select=5 send=4 sendto=6 setsockopt=5 socket=3
"""

ARGC = {}
for _line in _TEXT.splitlines():
    _line = _line.strip()
    if not _line or _line.endswith(":"):
        continue
    for _item in _line.split():
        _name, _n = _item.split("=")
        ARGC[_name] = int(_n)

# cdecl:调用方清栈
CDECL = {"wsprintfW", "wsprintfA", "swprintf", "sprintf"}
# 成员函数(thiscall,this 在 ecx,无其他参数)
THISCALL_NOARG = {"??0_Lockit@std@@QAE@XZ", "??1_Lockit@std@@QAE@XZ"}
