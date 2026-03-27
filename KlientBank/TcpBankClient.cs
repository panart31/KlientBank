using System.Net.Sockets;
using System.Text;

namespace KlientBank;

public class TcpBankClient
{
    private readonly string _ip;
    private readonly int _port;

    public TcpBankClient(string ip, int port)
    {
        _ip = ip;
        _port = port;
    }

    // Для стабильной работы с этим сервером:
    // отправляем запрос, закрываем сторону отправки и читаем ответ до EOF.
    public async Task<string> SendRequestAsync(string request)
    {
        byte[] payload = Encoding.UTF8.GetBytes(request);

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(_ip, _port);

        using NetworkStream stream = tcp.GetStream();

        await stream.WriteAsync(payload, 0, payload.Length);
        await stream.FlushAsync();

        try
        {
            tcp.Client.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            // Ignore: some stacks can throw if socket already transitions state.
        }

        var buffer = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0) break;
            sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }

        return sb.ToString();
    }
}
