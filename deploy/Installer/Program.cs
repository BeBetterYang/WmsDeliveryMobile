using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

[assembly: SupportedOSPlatform("windows")]

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

try
{
    if (!OperatingSystem.IsWindows())
    {
        Fail("This installer can only run on Windows.");
        return;
    }

    if (!IsAdministrator())
    {
        Fail("Please right click the installer and run as Administrator. IIS configuration requires administrator permission.");
        return;
    }

    Console.WriteLine("WMS Delivery Mobile IIS Setup");
    Console.WriteLine("----------------------------------------");

    var mode = args.Any(arg => arg.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
        ? "2"
        : Prompt("Choose action: 1=Install/Update, 2=Uninstall", "1");

    if (mode == "2")
    {
        var uninstallDir = Prompt("Install directory", @"D:\WmsDeliveryMobile");
        var uninstallSite = Prompt("IIS site name", "WmsDeliveryMobile");
        var confirmed = PromptYesNo($"Confirm uninstall site '{uninstallSite}' and delete '{uninstallDir}'", false);
        if (!confirmed)
        {
            Console.WriteLine("Uninstall cancelled.");
            return;
        }

        Console.WriteLine("Removing IIS site and application pool...");
        UninstallIis(uninstallSite, uninstallSite, uninstallDir, deleteInstallDir: true);
        RemoveFirewallRule(uninstallSite);
        Console.WriteLine("Uninstall completed. Database data and uploaded attachments were not deleted.");
        return;
    }

    var sqlServer = Prompt("SQL Server instance", ".");
    var database = Prompt("Database name", "hh2j1332");
    var useWindowsAuth = PromptYesNo("Use Windows authentication for SQL Server", true);

    string connectionString;
    if (useWindowsAuth)
    {
        connectionString = $"Server={sqlServer};Database={database};Trusted_Connection=True;Encrypt=False;TrustServerCertificate=True;MultipleActiveResultSets=True;";
    }
    else
    {
        var sqlUser = Prompt("SQL user", "sa");
        var sqlPassword = PromptSecret("SQL password");
        connectionString = $"Server={sqlServer};Database={database};User ID={sqlUser};Password={sqlPassword};Encrypt=False;TrustServerCertificate=True;MultipleActiveResultSets=True;";
    }

    var installDir = Prompt("Install directory", @"D:\WmsDeliveryMobile");
    var port = PromptInt("IIS port", 5189);
    var siteName = Prompt("IIS site name", "WmsDeliveryMobile");
    var appPoolName = siteName;

    Console.WriteLine();
    Console.WriteLine("Testing database connection...");
    await using (var conn = new SqlConnection(connectionString))
    {
        await conn.OpenAsync();
    }

    Console.WriteLine("Checking IIS and ASP.NET Core Module...");
    EnsureIisReady();

    Console.WriteLine("Checking IIS port...");
    StopKnownPortOwners(port);

    Console.WriteLine("Removing old IIS site with the same name...");
    UninstallIis(siteName, appPoolName, installDir, deleteInstallDir: false);

    Console.WriteLine("Extracting site files...");
    Directory.CreateDirectory(installDir);
    var tempDir = Path.Combine(Path.GetTempPath(), "wms-delivery-mobile-install-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try
    {
        ExtractPayload(tempDir);
        CopyDirectory(tempDir, installDir);
    }
    finally
    {
        TryDeleteDirectory(tempDir);
    }

    Console.WriteLine("Writing application settings...");
    WriteAppSettings(installDir, connectionString, port);

    Console.WriteLine("Configuring IIS site...");
    ConfigureIis(siteName, appPoolName, installDir, port);
    AddFirewallRule(siteName, port);

    Console.WriteLine("Creating one-click uninstaller...");
    WriteUninstaller(installDir, siteName, appPoolName);

    Console.WriteLine();
    Console.WriteLine("Install completed.");
    Console.WriteLine($"Local URL: http://localhost:{port}/");
    Console.WriteLine($"LAN URL: http://<server-ip>:{port}/");
    Console.WriteLine($"Uninstaller: {Path.Combine(installDir, "Uninstall-WmsDeliveryMobile.bat")}");
}
catch (Exception ex)
{
    Console.WriteLine();
    Fail(ex.Message);
}

static string Prompt(string label, string defaultValue)
{
    Console.Write($"{label} [{defaultValue}]: ");
    var value = Console.ReadLine();
    return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
}

static int PromptInt(string label, int defaultValue)
{
    while (true)
    {
        var value = Prompt(label, defaultValue.ToString());
        if (int.TryParse(value, out var result) && result > 0 && result <= 65535)
        {
            return result;
        }

        Console.WriteLine("Please enter a port from 1 to 65535.");
    }
}

static bool PromptYesNo(string label, bool defaultValue)
{
    var suffix = defaultValue ? "Y/n" : "y/N";
    Console.Write($"{label} [{suffix}]: ");
    var value = (Console.ReadLine() ?? string.Empty).Trim().ToLowerInvariant();
    if (value.Length == 0)
    {
        return defaultValue;
    }

    return value is "y" or "yes";
}

static string PromptSecret(string label)
{
    Console.Write($"{label}: ");
    var buffer = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return buffer.ToString();
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (buffer.Length <= 0)
            {
                continue;
            }

            buffer.Length--;
            Console.Write("\b \b");
            continue;
        }

        buffer.Append(key.KeyChar);
        Console.Write("*");
    }
}

static bool IsAdministrator()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static void ExtractPayload(string targetDir)
{
    var assembly = Assembly.GetExecutingAssembly();
    using var stream = assembly.GetManifestResourceStream("payload.zip")
        ?? throw new InvalidOperationException("The installer package is missing embedded payload.zip. Please rebuild the installer.");
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    archive.ExtractToDirectory(targetDir, overwriteFiles: true);
}

static void CopyDirectory(string sourceDir, string targetDir)
{
    foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(dir.Replace(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase));
    }

    foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
    {
        var target = file.Replace(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static void WriteAppSettings(string installDir, string connectionString, int port)
{
    var path = Path.Combine(installDir, "appsettings.json");
    var json = File.Exists(path)
        ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject()
        : new JsonObject();

    if (json["ConnectionStrings"] is not JsonObject connectionStrings)
    {
        connectionStrings = new JsonObject();
        json["ConnectionStrings"] = connectionStrings;
    }

    connectionStrings["Wms"] = connectionString;
    json["Urls"] = $"http://0.0.0.0:{port}";
    json["AllowedHosts"] = "*";

    File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true
    }), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}

static void WriteUninstaller(string installDir, string siteName, string appPoolName)
{
    var script = string.Join(Environment.NewLine, new[]
    {
        "@echo off",
        "net session >nul 2>&1",
        "if %errorlevel% neq 0 (",
        "  powershell -NoProfile -ExecutionPolicy Bypass -Command \"Start-Process -FilePath '%~f0' -Verb RunAs\"",
        "  exit /b",
        ")",
        "",
        "echo Uninstalling WMS Delivery Mobile...",
        "set \"APPCMD=%windir%\\System32\\inetsrv\\appcmd.exe\"",
        "if exist \"%APPCMD%\" (",
        $"  \"%APPCMD%\" stop site /site.name:\"{EscapeCmd(siteName)}\" >nul 2>&1",
        $"  \"%APPCMD%\" delete site \"{EscapeCmd(siteName)}\" >nul 2>&1",
        $"  \"%APPCMD%\" stop apppool /apppool.name:\"{EscapeCmd(appPoolName)}\" >nul 2>&1",
        $"  \"%APPCMD%\" delete apppool \"{EscapeCmd(appPoolName)}\" >nul 2>&1",
        ")",
        $"netsh advfirewall firewall delete rule name=\"{EscapeCmd(siteName)}\" >nul 2>&1",
        "",
        "echo IIS site and application pool removed.",
        "echo Database data and uploaded attachments were not deleted.",
        "echo Deleting install directory...",
        "cd /d \"%TEMP%\"",
        $"start \"\" cmd /c \"timeout /t 3 /nobreak >nul & rmdir /s /q \"\"{EscapeCmd(installDir)}\"\"\"",
        "exit /b",
        string.Empty,
    });

    File.WriteAllText(Path.Combine(installDir, "Uninstall-WmsDeliveryMobile.bat"), script, Encoding.ASCII);
}

static string EscapeCmd(string value) => value.Replace("\"", "\"\"", StringComparison.Ordinal);

static void EnsureIisReady()
{
    var appcmd = GetAppCmdPath();
    var moduleCheck = RunProcess(appcmd, "list module /name:AspNetCoreModuleV2", throwOnError: false);
    if (moduleCheck.ExitCode != 0)
    {
        throw new InvalidOperationException("IIS does not have ASP.NET Core Module V2. Install the .NET 8 Hosting Bundle first.");
    }
}

static void ConfigureIis(string siteName, string appPoolName, string installDir, int port)
{
    var appcmd = GetAppCmdPath();
    if (RunProcess(appcmd, $"list apppool /name:\"{appPoolName}\"", throwOnError: false).ExitCode != 0)
    {
        RunProcess(appcmd, $"add apppool /name:\"{appPoolName}\"");
    }

    RunProcess(appcmd, $"set apppool /apppool.name:\"{appPoolName}\" /managedRuntimeVersion:\"\"");

    if (RunProcess(appcmd, $"list site /name:\"{siteName}\"", throwOnError: false).ExitCode != 0)
    {
        RunProcess(appcmd, $"add site /name:\"{siteName}\" /bindings:http/*:{port}: /physicalPath:\"{installDir}\"");
    }
    else
    {
        RunProcess(appcmd, $"set site /site.name:\"{siteName}\" /bindings:http/*:{port}:");
        RunProcess(appcmd, $"set vdir \"{siteName}/\" /physicalPath:\"{installDir}\"");
    }

    RunProcess(appcmd, $"set app \"{siteName}/\" /applicationPool:\"{appPoolName}\"");
    RunProcess("icacls", $"\"{installDir}\" /grant \"IIS AppPool\\{appPoolName}\":(OI)(CI)M /T", throwOnError: false);
    RunProcess(appcmd, $"start apppool /apppool.name:\"{appPoolName}\"", throwOnError: false);
    RunProcess(appcmd, $"start site /site.name:\"{siteName}\"", throwOnError: false);
}

static void UninstallIis(string siteName, string appPoolName, string installDir, bool deleteInstallDir)
{
    var appcmd = GetAppCmdPath(throwIfMissing: false);
    if (File.Exists(appcmd))
    {
        RunProcess(appcmd, $"stop site /site.name:\"{siteName}\"", throwOnError: false);
        RunProcess(appcmd, $"delete site \"{siteName}\"", throwOnError: false);
        RunProcess(appcmd, $"stop apppool /apppool.name:\"{appPoolName}\"", throwOnError: false);
        RunProcess(appcmd, $"delete apppool \"{appPoolName}\"", throwOnError: false);
    }

    if (!deleteInstallDir || !Directory.Exists(installDir))
    {
        return;
    }

    var normalizedInstallDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var executableDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (executableDir.StartsWith(normalizedInstallDir, StringComparison.OrdinalIgnoreCase))
    {
        ScheduleDirectoryDelete(normalizedInstallDir);
        Console.WriteLine("Install directory will be deleted after the installer exits.");
        return;
    }

    Directory.Delete(normalizedInstallDir, recursive: true);
}

static void StopKnownPortOwners(int port)
{
    var output = RunProcess("netstat", "-ano -p tcp", throwOnError: false).Output;
    foreach (var line in output.Split(Environment.NewLine))
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 ||
            !parts[1].EndsWith(":" + port, StringComparison.OrdinalIgnoreCase) ||
            !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(parts[4], out var pid) ||
            pid == 4)
        {
            continue;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            if (process.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
                process.ProcessName.StartsWith("WmsDeliveryMobile", StringComparison.OrdinalIgnoreCase))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
                continue;
            }
        }
        catch
        {
            // Process lookup can race with process exit.
        }

        throw new InvalidOperationException($"Port {port} is already used by process {pid}. Release the port or use another IIS port.");
    }
}

static void AddFirewallRule(string siteName, int port)
{
    RunProcess("netsh", $"advfirewall firewall delete rule name=\"{siteName}\"", throwOnError: false);
    RunProcess("netsh", $"advfirewall firewall add rule name=\"{siteName}\" dir=in action=allow protocol=TCP localport={port}", throwOnError: false);
}

static void RemoveFirewallRule(string siteName)
{
    RunProcess("netsh", $"advfirewall firewall delete rule name=\"{siteName}\"", throwOnError: false);
}

static string GetAppCmdPath(bool throwIfMissing = true)
{
    var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    var appcmd = Path.Combine(systemRoot, "System32", "inetsrv", "appcmd.exe");
    if (throwIfMissing && !File.Exists(appcmd))
    {
        throw new InvalidOperationException("IIS was not detected. Install IIS and the .NET 8 Hosting Bundle first.");
    }

    return appcmd;
}

static void ScheduleDirectoryDelete(string installDir)
{
    using var process = new Process();
    process.StartInfo = new ProcessStartInfo
    {
        FileName = "cmd.exe",
        Arguments = $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{installDir}\"",
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    process.Start();
}

static (int ExitCode, string Output) RunProcess(string fileName, string arguments, bool throwOnError = true)
{
    using var process = new Process();
    process.StartInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    process.Start();
    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (throwOnError && process.ExitCode != 0)
    {
        throw new InvalidOperationException($"{Path.GetFileName(fileName)} failed: {output}");
    }

    return (process.ExitCode, output);
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
        // Temporary cleanup failures do not affect installation.
    }
}

static void Fail(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Setup failed: " + message);
    Console.ResetColor();
    Console.WriteLine("Press Enter to exit...");
    Console.ReadLine();
}
