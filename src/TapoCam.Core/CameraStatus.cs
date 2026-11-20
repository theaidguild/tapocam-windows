namespace TapoCam.Core;

public enum RelayState
{
    Stopped,
    Starting,
    Running,
    Failed
}

public enum CameraStatusColor
{
    DimGray,
    Gray,
    Red,
    Orange,
    Green
}

public sealed record CameraStatus(string Text, CameraStatusColor Color)
{
    public static CameraStatus Resolve(
        bool enabled, bool configured, bool wantsConnection, RelayState relayState,
        bool? reachable, string? warning) =>
        (enabled, configured, wantsConnection, relayState, reachable) switch
        {
            (false, _, _, _, _) => new("Desativada", CameraStatusColor.DimGray),
            (_, false, _, _, _) => new("Configuração incompleta", CameraStatusColor.DimGray),
            (_, _, false, _, _) => new("Desconectada", CameraStatusColor.Gray),
            (_, _, _, RelayState.Failed, _) => new("Falha, tentando de novo…", CameraStatusColor.Red),
            (_, _, _, not RelayState.Running, _) => new("Conectando…", CameraStatusColor.Orange),
            (_, _, _, _, false) => new("Sem resposta", CameraStatusColor.Red),
            (_, _, _, _, true) when !string.IsNullOrWhiteSpace(warning) => new("Instável", CameraStatusColor.Orange),
            (_, _, _, _, true) => new("Online", CameraStatusColor.Green),
            _ => new("Verificando…", CameraStatusColor.Orange)
        };
}
