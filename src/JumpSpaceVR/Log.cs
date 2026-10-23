using MelonLoader;

namespace JumpSpaceVR;

/// <summary>Thin wrapper so non-Melon classes can log through the mod's logger.</summary>
internal static class Log
{
    private static MelonLogger.Instance _logger;

    public static void Bind(MelonLogger.Instance logger) => _logger = logger;

    public static void Msg(string message) => _logger?.Msg(message);
    public static void Warning(string message) => _logger?.Warning(message);
    public static void Error(string message) => _logger?.Error(message);

}
