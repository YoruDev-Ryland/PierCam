using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PierCam.Ui;

/// <summary>
/// Registers PierCam to launch at login through the per-user Run key.
///
/// Per-user deliberately: HKCU needs no administrator, which matters on a machine where UAC
/// prompts are unreliable, and an observatory PC that logs in automatically gets the same result
/// either way. The scheduled-task route would survive a logged-out machine, but nothing here can
/// run without a desktop session anyway — the app draws a live camera view.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PierCam";

    /// <summary>Passed by the Run entry when the app should come up out of the way.</summary>
    public const string MinimisedSwitch = "--minimised";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    /// <summary>
    /// Whether the Run entry exists and points at this build. A stale entry left by a copy that
    /// has since been moved is reported as not registered, so ticking the box repairs it.
    /// </summary>
    public static bool IsRegistered(out string? registeredCommand)
    {
        registeredCommand = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(ValueName) is not string command) return false;
            registeredCommand = command;

            var exe = ExecutablePath;
            return !string.IsNullOrEmpty(exe) &&
                   command.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Adds, updates or removes the entry. Returns false with a reason if it could not.</summary>
    public static bool Apply(bool runAtLogin, bool startMinimised, out string error)
    {
        error = string.Empty;
        var exe = ExecutablePath;
        if (runAtLogin && string.IsNullOrEmpty(exe))
        {
            error = "Could not determine where PierCam is running from.";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) { error = "Could not open the Run key."; return false; }

            if (!runAtLogin)
            {
                if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            // Quoted, because the path may contain spaces and Windows would otherwise try to run
            // the first word of it.
            var command = startMinimised ? $"\"{exe}\" {MinimisedSwitch}" : $"\"{exe}\"";
            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }
}
