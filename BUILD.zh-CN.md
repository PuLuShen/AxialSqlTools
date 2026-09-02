# Axial SQL Tools 编译指南

本文说明如何在 Windows 上将 Axial SQL Tools 编译为适用于 SQL Server Management Studio 22 的 VSIX 扩展。

## 1. 环境要求

- Windows 10 或 Windows 11
- SQL Server Management Studio 22
- Visual Studio 2022 或更高版本
- Visual Studio 工作负载：
  - .NET 桌面开发
  - Visual Studio 扩展开发
- .NET Framework 4.7.2 Developer Pack
- 可以访问 NuGet，以便还原项目依赖

如果没有安装 .NET Framework 4.7.2 Developer Pack，项目中的 `Microsoft.NETFramework.ReferenceAssemblies` 包也可以提供编译所需的引用程序集。

## 2. 打开解决方案

解决方案文件位于：

```text
AxialSqlTools\AxialSqlTools.sln
```

可以直接使用 Visual Studio 打开该文件，并选择：

```text
配置：Release
平台：Any CPU
```

然后执行“生成解决方案”。

## 3. 查找 Visual Studio、SSMS 和 MSBuild

使用 Visual Studio Installer 自带的 `vswhere.exe` 查看安装实例：

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
& $vswhere -all -products * -format json
```

常见的 Visual Studio MSBuild 路径：

```text
C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe
```

SSMS 22 通常也包含完整的 MSBuild 和 Roslyn 编译器：

```text
<SSMS安装目录>\MSBuild\Current\Bin\MSBuild.exe
```

例如本机 SSMS 安装在：

```text
D:\Microsoft SQL Server Management Studio 22\Release
```

对应的 MSBuild 为：

```text
D:\Microsoft SQL Server Management Studio 22\Release\MSBuild\Current\Bin\MSBuild.exe
```

如果 Visual Studio 的 `MSBuild\Current\Bin\Roslyn` 目录不完整，可以改用 SSMS 22 自带的 MSBuild。

## 4. SSMS 安装路径

项目通过 `SsmsInstallRoot` 属性定位 SSMS 程序集。默认路径为：

```text
C:\Program Files\Microsoft SQL Server Management Studio 22\Release
```

如果 SSMS 安装在其他位置，请在命令行传入：

```text
/p:SsmsInstallRoot=<SSMS安装目录>
```

例如：

```text
/p:SsmsInstallRoot=D:\Microsoft SQL Server Management Studio 22\Release
```

## 5. 使用命令行编译

在仓库根目录打开 PowerShell。

### 标准构建

如果已经安装 .NET Framework 4.7.2 Developer Pack，可以执行：

```powershell
$msbuild = "D:\Microsoft SQL Server Management Studio 22\Release\MSBuild\Current\Bin\MSBuild.exe"
$ssmsRoot = "D:\Microsoft SQL Server Management Studio 22\Release"

& $msbuild `
    "AxialSqlTools\AxialSqlTools.sln" `
    /restore `
    /m `
    /p:Configuration=Release `
    "/p:Platform=Any CPU" `
    "/p:SsmsInstallRoot=$ssmsRoot" `
    /verbosity:minimal
```

### 未安装 .NET Framework 4.7.2 Developer Pack

先执行一次 NuGet 还原。还原完成后，引用程序集通常位于：

```text
%USERPROFILE%\.nuget\packages\microsoft.netframework.referenceassemblies.net472\1.0.3\build\.NETFramework\v4.7.2
```

然后传入 `FrameworkPathOverride`：

```powershell
$msbuild = "D:\Microsoft SQL Server Management Studio 22\Release\MSBuild\Current\Bin\MSBuild.exe"
$ssmsRoot = "D:\Microsoft SQL Server Management Studio 22\Release"
$frameworkPath = Join-Path $env:USERPROFILE ".nuget\packages\microsoft.netframework.referenceassemblies.net472\1.0.3\build\.NETFramework\v4.7.2"

& $msbuild `
    "AxialSqlTools\AxialSqlTools.sln" `
    /restore `
    /m `
    /p:Configuration=Release `
    "/p:Platform=Any CPU" `
    "/p:SsmsInstallRoot=$ssmsRoot" `
    "/p:FrameworkPathOverride=$frameworkPath" `
    /verbosity:minimal
```

### 完整重新编译

将构建命令中的目标改为：

```text
/t:Rebuild
```

完整示例：

```powershell
& $msbuild `
    "AxialSqlTools\AxialSqlTools.sln" `
    /t:Rebuild `
    /m `
    /p:Configuration=Release `
    "/p:Platform=Any CPU" `
    "/p:SsmsInstallRoot=$ssmsRoot" `
    "/p:FrameworkPathOverride=$frameworkPath" `
    /verbosity:minimal
```

## 6. 构建产物

成功后会生成：

```text
AxialSqlTools\bin\Release\AxialSqlTools.dll
AxialSqlTools\bin\Release\AxialSqlTools.vsix
```

用于安装的文件是：

```text
AxialSqlTools\bin\Release\AxialSqlTools.vsix
```

## 7. 安装和升级

安装前请关闭所有 SSMS 进程，然后双击 `AxialSqlTools.vsix`。

VSIX 使用以下扩展 ID：

```text
AxialSqlTools
```

如果安装器提示同一版本已经安装，需要在 `source.extension.vsixmanifest` 中提高版本号：

```xml
<Identity Id="AxialSqlTools" Version="4.13" ... />
```

例如从 `4.13` 改为 `4.14`，重新编译后，VSIX Installer 会将其识别为升级版本。

### 7.1 旧版本处理机制

VSIX Installer 根据扩展 ID 和版本号判断应执行安装、升级还是拒绝重复安装：

- 扩展 ID 不同：视为两个独立扩展，可以同时安装。
- 扩展 ID 相同，新包版本更高：视为升级，安装器自动替换旧版本，通常不需要先卸载。
- 扩展 ID 和版本都相同：安装器提示“此扩展已安装到所有适用的产品”，不会重复覆盖。
- 扩展 ID 相同，新包版本更低：安装器通常拒绝降级，需要先卸载当前版本。

Axial SQL Tools 的扩展 ID 固定为：

```text
AxialSqlTools
```

正常开发测试时，推荐提高 `source.extension.vsixmanifest` 中的版本号，让安装器执行标准升级。不要通过修改扩展 ID 来规避旧版本，否则可能出现多个 Axial SQL Tools 同时加载。

VSIX 包不能向标准 VSIX Installer 注入自定义的“先卸载旧版”按钮或安装步骤。需要强制重装同一版本或执行降级时，应使用下面的卸载命令。

### 7.2 查找 SSMS 实例 ID

使用 `vswhere.exe` 查询 SSMS 22：

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
& $vswhere -products Microsoft.VisualStudio.Product.Ssms -format json
```

结果中的 `instanceId` 是 SSMS 安装实例 ID，例如：

```text
6d5d8555
```

结果中的 `installationPath` 是 SSMS 安装根目录。

### 7.3 使用 VSIXInstaller 卸载旧版本

首先关闭 SSMS，避免扩展程序集仍被进程占用。

SSMS 自带的卸载程序通常位于：

```text
<SSMS安装目录>\Common7\IDE\VSIXInstaller.exe
```

卸载当前用户安装的 Axial SQL Tools：

```powershell
$ssmsRoot = "D:\Microsoft SQL Server Management Studio 22\Release"
$instanceId = "6d5d8555"
$vsixInstaller = Join-Path $ssmsRoot "Common7\IDE\VSIXInstaller.exe"

& $vsixInstaller `
    /uninstall:AxialSqlTools `
    "/instanceIds:$instanceId"
```

如果旧扩展是以管理员身份为所有用户安装的，请使用管理员 PowerShell，并增加 `/admin`：

```powershell
& $vsixInstaller `
    /admin `
    /uninstall:AxialSqlTools `
    "/instanceIds:$instanceId"
```

无人值守卸载可以增加 `/quiet`：

```powershell
& $vsixInstaller `
    /quiet `
    /uninstall:AxialSqlTools `
    "/instanceIds:$instanceId"
```

卸载完成后，再双击新生成的 `AxialSqlTools.vsix`，或者从命令行安装：

```powershell
& $vsixInstaller `
    "AxialSqlTools\bin\Release\AxialSqlTools.vsix" `
    "/instanceIds:$instanceId"
```

### 7.4 推荐的开发重装流程

通常使用以下两种方式之一：

1. 推荐：提高 VSIX 版本号、重新编译、关闭 SSMS、双击 VSIX，让安装器自动升级。
2. 同版本测试：关闭 SSMS、使用 `/uninstall:AxialSqlTools` 卸载、重新安装生成的 VSIX。

不要在 SSMS 仍运行时直接删除扩展目录；这可能留下扩展缓存、注册信息或被占用的程序集。

### 7.5 一键卸载旧版并安装新版

构建生成的版本化 ZIP 同时包含：

```text
AxialSqlTools.vsix
Install-Or-Reinstall.ps1
安装或重装.cmd
```

解压后关闭所有 SSMS 窗口，双击 `安装或重装.cmd`。该入口会自动查找 SSMS 22，卸载扩展 ID 为 `AxialSqlTools` 的当前用户版本，然后安装同目录中的新 VSIX。

标准 VSIX 格式本身不支持在双击 `.vsix` 时执行自定义的卸载脚本。因此，需要同版本覆盖、降级或明确先卸载再安装时，应执行 `安装或重装.cmd`；版本号更高时仍可直接双击 VSIX 完成标准升级。

## 8. 常见问题

### Release|AnyCPU 配置无效

解决方案平台名包含空格，应使用：

```text
/p:Platform="Any CPU"
```

而不是：

```text
/p:Platform=AnyCPU
```

### 找不到 Microsoft.CSharp.Core.targets

错误示例：

```text
找不到 Roslyn\Microsoft.CSharp.Core.targets
```

处理方法：

1. 通过 Visual Studio Installer 安装“.NET 桌面开发”；或
2. 改用 SSMS 22 自带的 MSBuild。

### 找不到 .NETFramework,Version=v4.7.2 引用程序集

处理方法：

1. 安装 .NET Framework 4.7.2 Developer Pack；或
2. 使用 NuGet 引用程序集，并传入 `FrameworkPathOverride`。

### 找不到 SSMS 程序集

检查 `SsmsInstallRoot` 是否指向包含以下目录的 SSMS 根目录：

```text
Common7\IDE
Common7\IDE\Extensions\Application
Common7\IDE\PublicAssemblies
```

### AvalonEdit、OxyPlot 或其他 NuGet 类型无法在 XAML 中解析

该项目是旧式非 SDK WPF 项目。WPF 会生成临时 `*_wpftmp.csproj`，临时项目可能无法自动继承所有 `PackageReference`。

项目文件已经为这些依赖提供显式、基于 `$(NuGetPackageRoot)` 的引用。遇到此错误时应先执行：

```text
/restore
```

然后再执行完整重新编译。

### 编译成功但 VSIX 没有更新

确认执行了 `/t:Rebuild`，并检查：

```powershell
Get-Item "AxialSqlTools\bin\Release\AxialSqlTools.vsix" |
    Select-Object FullName, Length, LastWriteTime
```

## 9. 提交前检查

建议执行：

```powershell
git diff --check
git status --short
```

编译生成的 `bin`、`obj` 和 VSIX 文件通常不应提交到 Git；只提交源代码、项目文件和必要的构建说明。
