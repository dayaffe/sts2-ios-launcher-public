namespace STS2Mobile.Platform;

/// <summary>
/// Keeps the app running in the background while a Steam bridge session is open, through the C functions in the app's
/// <c>native/bridge_background.mm</c>. Off the phone (tests, the Mac) those functions do not exist and nothing is held.
/// </summary>
public static unsafe class BridgeBackground
{
    /// <summary>Results of <see cref="Begin"/>.</summary>
    public const int Unavailable = -1, ShortTaskOnly = 0, Held = 1;

    private static readonly delegate* unmanaged<int> BeginFunction = (delegate* unmanaged<int>)NativeExports.Find("sts2_bridge_background_begin");
    private static readonly delegate* unmanaged<void> EndFunction = (delegate* unmanaged<void>)NativeExports.Find("sts2_bridge_background_end");

    /// <summary>
    /// <see cref="Held"/> when silent background audio keeps the app running until <see cref="End"/>,
    /// <see cref="ShortTaskOnly"/> when iOS granted only its short background task, <see cref="Unavailable"/> off the phone.
    /// </summary>
    public static int Begin() => BeginFunction != null ? BeginFunction() : Unavailable;

    public static void End()
    {
        if (EndFunction != null) EndFunction();
    }

    public static string Describe(int result) => result switch
    {
        Held => "held with silent background audio",
        ShortTaskOnly => "background audio refused; only iOS's short background time",
        _ => "unavailable here",
    };
}
