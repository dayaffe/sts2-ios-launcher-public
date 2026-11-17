using System.Diagnostics;

namespace STS2Mobile.SteamBridge;

/// <summary>Controls only Steam inside the explicitly enabled, dedicated Docker desktop.</summary>
public sealed class SteamDesktop(BridgeOptions options, ILogger<SteamDesktop> logger)
{
    private bool _started;
    private Process? _launcher;

    public void Start()
    {
        if (!options.ManageSteam || _started) return;
        _launcher = Launch("exec steam -silent -cef-disable-gpu >> \"$HOME/logs/steam.log\" 2>&1");
        _started = true;
        logger.LogInformation("Starting the private Steam client for an attached phone.");
    }

    public void Stop()
    {
        if (!options.ManageSteam || !_started) return;
        _started = false;
        try
        {
            using var request = Launch("exec steam -shutdown >> \"$HOME/logs/steam.log\" 2>&1");
            request.WaitForExit(2000);
            // Only the dedicated container can enable this controller. Never terminate a host desktop's Steam.
            foreach (var steam in Process.GetProcessesByName("steam"))
                using (steam)
                    if (!steam.WaitForExit(5000)) steam.Kill(entireProcessTree: true);
            if (_launcher is { HasExited: false }) _launcher.Kill(entireProcessTree: true);
            _launcher?.Dispose();
            _launcher = null;
            logger.LogInformation("Stopped the private Steam client; no phone is attached.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning("Private Steam shutdown needs attention ({Kind}).", error.GetType().Name);
        }
    }

    private static Process Launch(string command)
    {
        var start = new ProcessStartInfo("bash") { WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        foreach (string variable in new[] { "SteamAppId", "SteamGameId", "SteamOverlayGameId" }) start.Environment.Remove(variable);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the private Steam command.");
    }
}
