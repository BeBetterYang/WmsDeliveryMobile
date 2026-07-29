# WMS 配送移动端 IIS 一键部署

## 前置条件

- Windows 10/11 或 Windows Server
- IIS
- .NET 8 SDK，用于执行 `dotnet publish`
- Node.js，用于执行 `npm run build`
- .NET 8 Hosting Bundle，IIS 需要其中的 ASP.NET Core Module V2

> 兼容说明：本项目是 .NET 8 应用，不能运行在 .NET Framework 4 运行时上。部署脚本使用 Windows 自包含发布，并把 IIS 应用池设置为 `No Managed Code`，可以和服务器上已有 .NET 4 站点共存。

## 一键安装

右键以管理员身份运行：

```text
deploy\Install-IIS.cmd
```

安装时会提示输入：

- 数据库服务器，默认 `.`
- 数据库名称，默认 `hh2j1332`
- 数据库认证方式，默认 Windows 集成认证，也可输入 `S` 使用 SQL 用户密码
- 安装目录，默认 `D:\WmsDeliveryMobile`
- IIS 站点名称，默认 `WmsDeliveryMobile`
- IIS 端口，默认 `5189`

安装完成后访问：

```text
http://localhost:5189/
http://服务器IP:5189/
```

## 一键卸载

右键以管理员身份运行：

```text
deploy\Uninstall-IIS.cmd
```

卸载会删除：

- IIS 站点
- IIS 应用池
- 对应防火墙规则

卸载时会询问是否删除安装目录。

## 静默安装示例

Windows 集成认证：

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\Install-IIS.ps1 `
  -DbServer "." `
  -DbName "hh2j1332" `
  -InstallDir "D:\WmsDeliveryMobile" `
  -SiteName "WmsDeliveryMobile" `
  -Port 5189 `
  -NonInteractive
```

SQL 认证：

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\Install-IIS.ps1 `
  -DbServer "." `
  -DbName "hh2j1332" `
  -UseSqlAuth `
  -DbUser "sa" `
  -DbPassword "your_password" `
  -InstallDir "D:\WmsDeliveryMobile" `
  -SiteName "WmsDeliveryMobile" `
  -Port 5189 `
  -NonInteractive
```
