// ============================================================================
//  DllSmith 1.4 — 程序集信息（exe 版本资源）
//  文件：AssemblyInfo.cs
//  说明：csc 会把这里的程序集特性写进 Win32 版本资源，解决审计 P1
//        「exe 无版本资源（FileVersion=0.0.0.0 / Company 为空）」。
//        版本号必须与 Core.cs AppInfo.Version、installer\DllSmith.iss #define AppVersion 一致。
// ============================================================================

using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("DllSmith - DLL 修复匠")]
[assembly: AssemblyDescription("系统运行库与 DLL 修复工具（开源重建版）")]
[assembly: AssemblyProduct("DllSmith")]
[assembly: AssemblyCompany("DllSmith Project")]
[assembly: AssemblyCopyright("Copyright (C) 2026 DllSmith Project · GPL-3.0")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyConfiguration("Release")]

// 三处一致的版本号：Core.cs AppInfo.Version / installer\DllSmith.iss AppVersion / 本文件
// 测试构建（/define:DS_TESTBUILD）在信息版本里显式标注，配合 build.ps1 的 *-test.exe 命名，
// 双保险防止把带测试钥匙的产物误发出去（t13）。
#if DS_TESTBUILD
[assembly: AssemblyVersion("1.4.1.0")]
[assembly: AssemblyFileVersion("1.4.1.0")]
[assembly: AssemblyInformationalVersion("1.4.1-test DO-NOT-SHIP")]
#else
[assembly: AssemblyVersion("1.4.1.0")]
[assembly: AssemblyFileVersion("1.4.1.0")]
[assembly: AssemblyInformationalVersion("1.4.1")]
#endif

[assembly: ComVisible(false)]
[assembly: Guid("8f3c1d2e-5b6a-4c7d-9e0f-1a2b3c4d5e6f")]
