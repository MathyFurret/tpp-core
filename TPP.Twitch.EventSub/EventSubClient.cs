using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NodaTime;
using TPP.Common.Utils;
using TPP.Twitch.EventSub.Messages;
using static TPP.Twitch.EventSub.Parsing;

namespace TPP.Twitch.EventSub;

internal record WebsocketChangeover(ClientWebSocket NewWebSocket, SessionWelcome Welcome);

public class EventSubClient
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Duration KeepAliveGrace = Duration.FromSeconds(3);
    private static readonly Duration MaxMessageAge = Duration.FromMinutes(10);
    private static readonly Task<WebsocketChangeover> NoChangeoverTask =
        new TaskCompletionSource<WebsocketChangeover>().Task; // a task that never gets completed

    private readonly IClock _clock;
    private readonly Uri _uri;
    private int? _keepaliveTimeSeconds;
    private Duration KeepaliveDuration => Duration.FromSeconds(_keepaliveTimeSeconds ?? 600);

    private ClientWebSocket _webSocket;
    private Task<WebsocketChangeover> _changeoverTask = NoChangeoverTask;
    private TtlSet<string> _seenMessageIDs;
    private Instant _lastMessageTimestamp;
    private bool _welcomeReceived = false;

    public enum DisconnectReason { KeepaliveTimeout, RemoteDisconnected }
    public event EventHandler<INotification>? NotificationReceived;
    public event EventHandler<Revocation>? RevocationReceived;
    public event EventHandler<SessionWelcome>? Connected;
    public event EventHandler<DisconnectReason>? ConnectionLost;

    public event EventHandler<string>? UnknownMessageTypeReceived;
    public event EventHandler<string>? UnknownSubscriptionTypeReceived;
    public event EventHandler<string>? MessageParsingFailed;

    private void Reset()
    {
        _webSocket.Abort();
        _webSocket.Dispose();
        _webSocket = new ClientWebSocket();
        if (_keepaliveTimeSeconds != null)
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(_keepaliveTimeSeconds.Value);
        _changeoverTask = NoChangeoverTask;
        _seenMessageIDs = new TtlSet<string>(MaxMessageAge, _clock);
        _welcomeReceived = false;
    }

    public EventSubClient(
        IClock clock,
        string url = "wss://eventsub.wss.twitch.tv/ws",
        int? keepaliveTimeSeconds = null)
    {
        if (keepaliveTimeSeconds is < 10 or > 600)
            throw new ArgumentException(
                "Twitch only allows keepalive timeouts between 10 and 600 seconds", nameof(keepaliveTimeSeconds));

        _clock = clock;
        _uri = keepaliveTimeSeconds == null
            ? new Uri(url)
            : new Uri(url + "?keepalive_timeout_seconds=" + keepaliveTimeSeconds);
        _keepaliveTimeSeconds = keepaliveTimeSeconds;
        _seenMessageIDs = new TtlSet<string>(MaxMessageAge, _clock);
        _webSocket = new ClientWebSocket();
        if (_keepaliveTimeSeconds != null)
            _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(_keepaliveTimeSeconds.Value);
    }

    private static async Task<string?> ReadMessageText(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var bufferSegment = new ArraySegment<byte>(new byte[8192]);
        await using var ms = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(bufferSegment, cancellationToken);
            if (result.CloseStatus != null)
            {
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken);
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new NotSupportedException();
            }
            await ms.WriteAsync(bufferSegment.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage)
            {
                break;
            }
        }
        if (cancellationToken.IsCancellationRequested)
            throw new TaskCanceledException();

        ms.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(ms, Utf8NoBom).ReadToEndAsync(cancellationToken);
    }

    private static async Task<ParseResult?> ReadMessage(WebSocket webSocket, CancellationToken cancellationToken)
    {
        string? json = await ReadMessageText(webSocket, cancellationToken);
        if (json == null)
            return null; // connection closed
        return Parse(json);
    }

    public async Task Connect(CancellationToken cancellationToken)
    {
        await _webSocket.ConnectAsync(_uri, cancellationToken);
        _lastMessageTimestamp = _clock.GetCurrentInstant(); // treat a fresh connection as a received message
        while (!cancellationToken.IsCancellationRequested)
        {
            Task<ParseResult?> readTask = ReadMessage(_webSocket, cancellationToken);
            Instant assumeDeadAt = _lastMessageTimestamp + KeepaliveDuration + KeepAliveGrace;
            Duration assumeDeadIn = assumeDeadAt - _clock.GetCurrentInstant();
            Task timeoutTask = Task.Delay(assumeDeadIn.ToTimeSpan(), cancellationToken);
            Task firstFinishedTask = await Task.WhenAny(_changeoverTask, readTask, timeoutTask);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            if (firstFinishedTask == _changeoverTask)
            {
                ClientWebSocket oldWebsocket = _webSocket;
                WebsocketChangeover changeover = await _changeoverTask;
                _webSocket = changeover.NewWebSocket;
                _keepaliveTimeSeconds = changeover.Welcome.Payload.Session.KeepaliveTimeoutSeconds;
                _lastMessageTimestamp = changeover.Welcome.Metadata.MessageTimestamp;
                _changeoverTask = NoChangeoverTask;
                await oldWebsocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken);
                continue;
            }
            if (firstFinishedTask == timeoutTask)
            {
                // Regarding "Keepalive message", Twitch recommends:
                // If your client doesn't receive an event or keepalive message for longer than keepalive_timeout_seconds,
                // you should assume the connection is lost and reconnect to the server and resubscribe to the events.
                Reset();
                ConnectionLost?.Invoke(this, DisconnectReason.KeepaliveTimeout);
                break;
            }
            ParseResult? parseResult = await readTask;
            if (parseResult == null) // connection closed
            {
                Reset();
                ConnectionLost?.Invoke(this, DisconnectReason.RemoteDisconnected);
                break;
            }
            if (parseResult is not ParseResult.Ok(var message))
            {
                if (parseResult is ParseResult.InvalidMessage(var error))
                    MessageParsingFailed?.Invoke(this, error);
                else if (parseResult is ParseResult.UnknownMessageType(var messageType))
                    UnknownMessageTypeReceived?.Invoke(this, messageType);
                else if (parseResult is ParseResult.UnknownSubscriptionType(var subType))
                    UnknownSubscriptionTypeReceived?.Invoke(this, subType);
                else
                    throw new ArgumentOutOfRangeException(nameof(parseResult));
                continue;
            }
            if (message.Metadata.MessageTimestamp < _clock.GetCurrentInstant() - MaxMessageAge)
            {
                // Regarding "Guarding against replay attacks", Twitch recommends:
                // Make sure the value in the message_timestamp field isn’t older than 10 minutes.
                throw new ProtocolViolationException(
                    $"Unexpectedly received message older than {MaxMessageAge.TotalSeconds}s: {message}");
            }
            if (!_seenMessageIDs.Add(message.Metadata.MessageId))
            {
                // Regarding "Guarding against replay attacks", Twitch recommends:
                // Make sure you haven’t seen the ID in the message_id field before.
                continue; // Just drop silently. TODO maybe log or issue an optional "duplicate message" event?
            }
            _lastMessageTimestamp = message.Metadata.MessageTimestamp;
            if (message is SessionWelcome welcome)
            {
                if (_welcomeReceived)
                    throw new ProtocolViolationException(
                        $"Unexpected received a second welcome message on websocket: {message}");
                _welcomeReceived = true;
                _keepaliveTimeSeconds = welcome.Payload.Session.KeepaliveTimeoutSeconds;
                Connected?.Invoke(this, welcome);
            }
            else if (!_welcomeReceived)
            {
                throw new ProtocolViolationException(
                    $"Expected first message on websocket to be {SessionWelcome.MessageType}, " +
                    $"but unexpectedly was {message.Metadata.MessageType}");
            }
            else if (message is INotification notification)
            {
                NotificationReceived?.Invoke(this, notification);
            }
            else if (message is SessionReconnect reconnect)
            {
                var reconnectUri = new Uri(reconnect.Payload.Session
                    .ReconnectUrl ?? throw new ProtocolViolationException(
                    "twitch must provide a reconnect URL in a reconnect message"));
                _changeoverTask = PerformChangeover(reconnectUri, cancellationToken);
            }
            else if (message is Revocation revocation)
            {
                RevocationReceived?.Invoke(this, revocation);
            }
            else if (message is SessionKeepalive)
            {
                // Do nothing. All this is good for is resetting the timestamp of the last received message
                // to honor the keepalive time, which gets done for all messages above.
            }
            else
            {
                // TODO Maybe this shouldn't be a crash. Better just log as error.
                throw new ProtocolViolationException("Unprocessed message type: " + message);
            }
        }
    }

    private static async Task<WebsocketChangeover> PerformChangeover(Uri reconnectUri,
        CancellationToken cancellationToken)
    {
        var newWebSocket = new ClientWebSocket();
        await newWebSocket.ConnectAsync(reconnectUri, cancellationToken);
        ParseResult? firstReceivedMessage = await ReadMessage(newWebSocket, cancellationToken);
        if (firstReceivedMessage is ParseResult.Ok(SessionWelcome welcomeOnNewWebsocket))
        {
            // Small delay so it's unlikely we drop already in-flight messages from the old websocket.
            // Tradeoff is that we delay new messages for up to this amount after a reconnect.
            //await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            return new WebsocketChangeover(newWebSocket, welcomeOnNewWebsocket);
        }
        else if (firstReceivedMessage is ParseResult.Ok(var anyOtherMessage))
        {
            throw new ProtocolViolationException(
                $"Expected first message on reconnect websocket to be {SessionWelcome.MessageType}, " +
                $"but unexpectedly was {anyOtherMessage.Metadata.MessageType}");
        }
        else
        {
            throw new ProtocolViolationException(
                $"Expected first message on reconnect websocket to be {SessionWelcome.MessageType}, " +
                $"but couldn't understand the message: {firstReceivedMessage}");
        }
    }
}
