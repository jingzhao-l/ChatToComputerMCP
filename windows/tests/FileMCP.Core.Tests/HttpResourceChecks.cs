using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.IO.Pipes;
using System.Text;
using FileMCP.Core;

namespace FileMCP.Core.Tests;

internal static partial class Program
{
    private static async Task TestHttpResourceLimitsAsync(string root)
    {
        var workspace = Path.Combine(root, "http-resource-limits");
        Directory.CreateDirectory(workspace);
        var port = FreePort();
        var token = new string('a', 64);
        await using var server = new LocalMcpServer((ushort)port, workspace, "", "", false, token, _ => {},
            requestTimeout: TimeSpan.FromSeconds(1.5), responseTimeout: TimeSpan.FromMilliseconds(500), maxConnections: 2);
        await server.StartAsync();
        async Task<TcpClient> Connect()
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            return client;
        }
        static async Task<bool> Closed(TcpClient client)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { return await client.GetStream().ReadAsync(new byte[1], timeout.Token) == 0; }
            catch (IOException) { return true; }
            catch (SocketException) { return true; }
        }
        async Task WaitCount(int count)
        {
            var clock = Stopwatch.StartNew();
            while (server.ActiveConnectionCount != count && clock.Elapsed < TimeSpan.FromSeconds(4))
                await Task.Delay(10);
            Assert(server.ActiveConnectionCount == count, "HTTP active connection count converges");
        }
        using var slow = await Connect();
        using var idle = await Connect();
        var partial = Encoding.ASCII.GetBytes("POST /mcp HTTP/1.1\r\nHost: localhost\r\nX-Partial: ");
        await slow.GetStream().WriteAsync(partial);
        await idle.GetStream().WriteAsync(partial);
        await WaitCount(2);
        var started = Stopwatch.StartNew();
        using var dripping = new CancellationTokenSource();
        var drip = Task.Run(async () =>
        {
            try
            {
                while (!dripping.IsCancellationRequested)
                {
                    await Task.Delay(100, dripping.Token);
                    await slow.GetStream().WriteAsync(new byte[] { (byte)'x' }, dripping.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
        });
        using (var excess = await Connect())
            Assert(await Closed(excess), "third accepted socket is rejected at cap");
        Assert(await Closed(slow), "slow-drip sender closes at absolute deadline");
        Assert(await Closed(idle), "idle sender closes at absolute deadline");
        dripping.Cancel();
        await drip;
        Assert(started.Elapsed < TimeSpan.FromSeconds(3), "read deadline is not reset by drip bytes");
        await WaitCount(0);
        var response = await SendHttpAsync(port, "POST", "/mcp", AuthHeaders(token), "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{}}");
        Assert(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "released slots accept valid requests");
        await WaitCount(0);
        // A partial authenticated body must also expire.
        using (var body = await Connect())
        {
            var header = Encoding.ASCII.GetBytes($"POST /mcp HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\n{FileMcpConstants.LocalAuthHeaderName}: {token}\r\nContent-Length: 100\r\n\r\n{{");
            await body.GetStream().WriteAsync(header);
            Assert(await Closed(body), "incomplete authenticated body is bounded");
        }
        await WaitCount(0);
        // Exercise the same response writer used for production HTTP.
        // Windows loopback may buffer an entire TCP response even when the
        // peer does not read; a real asynchronous OS pipe with a small
        // capacity and no reader provides reproducible write backpressure.
        // The TCP checks above still exercise the actual HTTP listener.
        {
            var name = "ct-" + Guid.NewGuid().ToString("N");
            await using var writer = new NamedPipeServerStream(name, PipeDirection.Out, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024);
            await using var blocked = new NamedPipeClientStream(".", name, PipeDirection.In,
                PipeOptions.Asynchronous);
            using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accepted = writer.WaitForConnectionAsync(ceiling.Token);
            await blocked.ConnectAsync(ceiling.Token);
            await accepted;
            var clock = Stopwatch.StartNew();
            try
            {
                await server.WriteResponseAsync(writer, new byte[8_000_000], ceiling.Token);
                throw new Exception("response unexpectedly fit in unread pipe buffers");
            }
            catch (OperationCanceledException)
            {
                Assert(!ceiling.IsCancellationRequested && clock.Elapsed < TimeSpan.FromSeconds(3),
                    "blocked OS write uses its independent response deadline");
            }
        }
        using var pending = await Connect();
        await pending.GetStream().WriteAsync(partial);
        await WaitCount(1);
        server.Stop();
        Assert(await Closed(pending), "stop closes accepted sockets");
        await WaitCount(0);
        Console.WriteLine("windows-http-resource-limits: ok");
    }
}
