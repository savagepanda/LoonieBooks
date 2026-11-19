using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using MapleLedger.Core.Lan;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace MapleLedger.Web;

/// <summary>
/// Sends plain HTTP that reaches a shared desktop install's HTTPS port to https:// on the same port
/// (<see cref="PlainHttpRedirect"/>). Runs on the connection, before TLS.
/// </summary>
public static class PlainHttpRedirectMiddleware
{
    // Like Kestrel's own TLS handshake timeout: a connection that sends nothing isn't kept.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    public static void UsePlainHttpRedirect(this ListenOptions listen) =>
        listen.Use(next => async connection =>
        {
            var input = connection.Transport.Input;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
            timeout.CancelAfter(ReadTimeout);

            ReadResult result;
            try
            {
                result = await input.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                connection.Abort();
                return;
            }

            if (result.Buffer.IsEmpty || PlainHttpRedirect.IsTlsHandshake(FirstByte(result.Buffer)))
            {
                // Nothing consumed: TLS reads the same bytes again.
                input.AdvanceTo(result.Buffer.Start);
                await next(connection);
                return;
            }

            try
            {
                await RedirectAsync(connection, input, result, timeout.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ConnectionResetException)
            {
                connection.Abort();
            }
        });

    private static async Task RedirectAsync(ConnectionContext connection, PipeReader input, ReadResult result, CancellationToken ct)
    {
        var buffer = result.Buffer;
        while (!result.IsCompleted && buffer.Length < PlainHttpRedirect.MaxHeadLength && !PlainHttpRedirect.HasWholeHead(Head(buffer)))
        {
            input.AdvanceTo(buffer.Start, buffer.End);
            result = await input.ReadAsync(ct);
            buffer = result.Buffer;
        }

        var head = Head(buffer);
        input.AdvanceTo(buffer.End);

        var local = connection.LocalEndPoint as IPEndPoint;
        var location = local is null ? null : PlainHttpRedirect.Location(head, local.Address, local.Port);
        if (location is not null)
        {
            await connection.Transport.Output.WriteAsync(PlainHttpRedirect.Response(location), ct);
        }

        await connection.Transport.Output.CompleteAsync();
        await input.CompleteAsync();
    }

    private static byte FirstByte(ReadOnlySequence<byte> buffer)
    {
        Span<byte> first = stackalloc byte[1];
        buffer.Slice(0, 1).CopyTo(first);
        return first[0];
    }

    private static byte[] Head(ReadOnlySequence<byte> buffer) =>
        buffer.Slice(0, Math.Min(buffer.Length, PlainHttpRedirect.MaxHeadLength)).ToArray();
}
