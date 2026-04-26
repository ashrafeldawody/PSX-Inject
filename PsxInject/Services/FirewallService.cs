using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PsxInject.Services;

/// <summary>
/// Manages a Windows Defender Firewall inbound rule for this app's executable.
/// Reads via netsh (no admin needed); writes via an elevated batch script (UAC prompt).
/// </summary>
public static class FirewallService
{
    public const string RuleName = "PSX inject";

    public enum Status
    {
        Unknown,
        Present,
        Missing
    }

    public readonly struct AddResult
    {
        public AddResult(bool success, string detail, string command)
        {
            Success = success;
            Detail = detail;
            ManualCommand = command;
        }
        public bool Success { get; }
        public string Detail { get; }
        public string ManualCommand { get; }
    }

    public static string CurrentExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static Status CheckRule()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\" verbose")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null) return Status.Unknown;

            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(); } catch { }
                return Status.Unknown;
            }

            // netsh returns 1 with "No rules match the specified criteria" when missing.
            if (p.ExitCode != 0) return Status.Missing;

            // Defensive: also confirm our exe path is referenced.
            var exe = CurrentExecutablePath;
            if (!string.IsNullOrEmpty(exe) &&
                stdout.IndexOf(exe, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return Status.Missing;
            }

            return Status.Present;
        }
        catch
        {
            return Status.Unknown;
        }
    }

    /// <summary>
    /// Triggers a UAC prompt and adds an inbound allow rule for this exe on private+domain
    /// profiles. Verifies success by re-reading the rule afterwards. On failure, returns a
    /// log of what netsh actually said plus a copy-pasteable manual command.
    /// </summary>
    public static AddResult TryAddRuleElevated()
    {
        var exe = CurrentExecutablePath;
        var manualCommand = BuildManualCommand(exe);

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            return new AddResult(false,
                $"Could not locate this executable on disk: '{exe}'. Run the published .exe (not 'dotnet run') to use this feature.",
                manualCommand);
        }

        var stamp = Guid.NewGuid().ToString("N");
        var logPath = Path.Combine(Path.GetTempPath(), $"psxdh-fw-{stamp}.log");
        var batPath = Path.Combine(Path.GetTempPath(), $"psxdh-fw-{stamp}.cmd");

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("setlocal");
        script.AppendLine($"set LOG=\"{logPath}\"");
        script.AppendLine("> %LOG% echo === PSX inject firewall rule install ===");
        script.AppendLine($">> %LOG% echo Rule: {RuleName}");
        script.AppendLine($">> %LOG% echo Exe:  {exe}");
        script.AppendLine(">> %LOG% echo.");
        script.AppendLine(">> %LOG% echo --- delete (any prior rule) ---");
        script.AppendLine($"netsh advfirewall firewall delete rule name=\"{RuleName}\" >> %LOG% 2>&1");
        script.AppendLine(">> %LOG% echo.");
        script.AppendLine(">> %LOG% echo --- add ---");
        script.AppendLine(
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" " +
            $"dir=in action=allow program=\"{exe}\" enable=yes profile=any " +
            ">> %LOG% 2>&1");
        script.AppendLine("set ec=%errorlevel%");
        script.AppendLine(">> %LOG% echo.");
        script.AppendLine(">> %LOG% echo Exit code: %ec%");
        script.AppendLine("endlocal & exit /b %ec%");

        try
        {
            File.WriteAllText(batPath, script.ToString(), Encoding.ASCII);
        }
        catch (Exception ex)
        {
            return new AddResult(false, $"Could not stage batch script: {ex.Message}", manualCommand);
        }

        int batExitCode;
        try
        {
            var psi = new ProcessStartInfo(batPath)
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                Cleanup(batPath, logPath);
                return new AddResult(false, "Failed to launch elevated process.", manualCommand);
            }

            if (!p.WaitForExit(20000))
            {
                try { p.Kill(); } catch { }
                Cleanup(batPath, logPath);
                return new AddResult(false, "Timed out waiting for elevated netsh.", manualCommand);
            }
            batExitCode = p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Cleanup(batPath, logPath);
            return new AddResult(false, "UAC prompt was declined.", manualCommand);
        }
        catch (Exception ex)
        {
            Cleanup(batPath, logPath);
            return new AddResult(false, $"Could not start elevated process: {ex.Message}", manualCommand);
        }

        // Read the log before deleting it (netsh output captured server-side of the elevation).
        string log = "";
        try { log = File.ReadAllText(logPath); } catch { /* ignore */ }
        Cleanup(batPath, logPath);

        // The authoritative test: re-query the rule.
        var status = CheckRule();
        if (status == Status.Present)
        {
            return new AddResult(true, "Firewall rule added successfully.", manualCommand);
        }

        var detail = string.IsNullOrWhiteSpace(log)
            ? $"netsh did not produce output (exit {batExitCode}). The rule was not created."
            : $"netsh exit {batExitCode}. Output:\n\n{log.Trim()}";

        return new AddResult(false, detail, manualCommand);
    }

    private static string BuildManualCommand(string exe) =>
        $"netsh advfirewall firewall add rule name=\"{RuleName}\" " +
        $"dir=in action=allow program=\"{exe}\" enable=yes profile=any";

    private static void Cleanup(string batPath, string logPath)
    {
        try { if (File.Exists(batPath)) File.Delete(batPath); } catch { }
        try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
    }
}
