namespace Content.Server.AiAgent.Tools;

/// <summary>
/// Which link of the interaction gate chain refused, or <see cref="Ok"/>.
///
/// The AI must be exactly as capable as a human player and no more, so every device tool walks the
/// same checks a human's click walks — whitelist, power, vision, access — in the same order.
/// Reporting <em>which</em> link failed is what lets the model recover: "no_access" means find
/// another way in, "unpowered" means fix the power first, "wire_cut" means that device is simply
/// gone from your world until someone repairs it.
/// </summary>
public enum DeviceGate : byte
{
    Ok,

    /// <summary>No StationAiWhitelistComponent at all — not a device the AI may ever touch.</summary>
    NotWhitelisted,

    /// <summary>Whitelisted, but the AI-control wire has been cut.</summary>
    WireCut,

    Unpowered,

    /// <summary>Different grid, or outside the coverage of any camera the AI can see through.</summary>
    NotVisible,

    /// <summary>AccessReader refused. The brain has AllAccess, so this is rare but not impossible.</summary>
    NoAccess,

    /// <summary>The AI is in an intellicard: it can still talk, but not touch anything.</summary>
    Carded,

    Dead,
}

public static class DeviceGateExt
{
    public static string ToError(this DeviceGate gate) => gate switch
    {
        DeviceGate.NotWhitelisted => ToolError.NotControllable,
        DeviceGate.WireCut => ToolError.WireCut,
        DeviceGate.Unpowered => ToolError.Unpowered,
        DeviceGate.NotVisible => ToolError.NotVisible,
        DeviceGate.NoAccess => ToolError.NoAccess,
        DeviceGate.Carded => ToolError.Carded,
        DeviceGate.Dead => ToolError.Dead,
        _ => ToolError.Internal,
    };

    public static string ToDetail(this DeviceGate gate) => gate switch
    {
        DeviceGate.NotWhitelisted => "это устройство не подключено к системам ИИ",
        DeviceGate.WireCut => "провод управления ИИ перерезан — устройство больше не отвечает",
        DeviceGate.Unpowered => "устройство обесточено",
        DeviceGate.NotVisible => "устройство вне зоны действия камер или на другом гриде",
        DeviceGate.NoAccess => "нет прав доступа к этому устройству",
        DeviceGate.Carded => "ты в интелликарте — оборудование станции недоступно",
        DeviceGate.Dead => "ИИ выведен из строя",
        _ => "неизвестная ошибка",
    };

    public static string? Retry(this DeviceGate gate) => gate switch
    {
        DeviceGate.Unpowered => "later",
        DeviceGate.NotVisible => "other_target",
        DeviceGate.NoAccess => "other_target",
        DeviceGate.WireCut => "none",
        DeviceGate.NotWhitelisted => "none",
        _ => null,
    };
}
