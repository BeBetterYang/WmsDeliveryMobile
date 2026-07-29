# WmsDeliveryMobile

WMS independent mobile delivery web app with an Android WebView shell and IIS deployment scripts.

## Structure

- `Program.cs` / `WmsDeliveryMobile.csproj`: ASP.NET Core backend API.
- `src/`: React + Ant Design Mobile frontend.
- `android-shell/`: Android WebView wrapper for APK packaging.
- `deploy/`: IIS install/uninstall scripts and one-click installer source.

## Local Development

```powershell
npm install
npm run build
dotnet run --urls http://0.0.0.0:5189
```

Database connection is read from `appsettings.json` or the deployed `appsettings.json`.

## IIS Deployment

Use the scripts under `deploy/`.

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\Install-IIS.ps1
```

To build the IIS one-click installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\Pack-Installer.ps1
```

Generated installer output is written to `deploy/output/` and is intentionally ignored by git.

## APK Packaging

Install Android SDK and Gradle first, then run:

```powershell
powershell -ExecutionPolicy Bypass -File .\android-shell\Build-Apk.ps1
```

If Gradle or Android SDK is not on the default path, pass them explicitly:

```powershell
powershell -ExecutionPolicy Bypass -File .\android-shell\Build-Apk.ps1 -GradleBat "C:\path\to\gradle.bat" -AndroidSdk "C:\path\to\Android\Sdk"
```

Generated APK output is written to `deploy/output/` and is intentionally ignored by git.
