using System.Net.Sockets;

namespace TapoCam.Core;

public sealed record CameraProbeResult(bool Reachable, string? Message = null);

public interface ICameraHealthProbe
{
    Task<CameraProbeResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default);
}

public sealed class TcpCameraHealthProbe : ICameraHealthProbe
{
    public async Task<CameraProbeResult> ProbeAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return new(true);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return new(false,
                $"A câmera em {host}:{port} recusou a conexão. Confira a porta RTSP e se o serviço RTSP está habilitado na câmera.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unreachable(host, port, "tempo limite excedido");
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            return Unreachable(host, port, CredentialMasker.Mask(ex.Message));
        }
    }

    private static CameraProbeResult Unreachable(string host, int port, string error) =>
        new(false, $"Sem acesso à câmera em {host}:{port} ({error}). Verifique se ela está ligada e na mesma rede, e se o Firewall do Windows não está bloqueando o TapoCam.");
}
