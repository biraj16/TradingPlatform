using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TradingConsole.DhanApi.Models;
using TradingConsole.DhanApi.Models.WebSocket;

namespace TradingConsole.DhanApi
{
    public class DhanWebSocketClient : IDisposable
    {
        private ClientWebSocket? _webSocket;
        private readonly string _clientId;
        private readonly string _accessToken;
        private CancellationTokenSource? _cancellationTokenSource;

        // Strict limit enforcement
        private const int MAX_INSTRUMENTS_PER_REQUEST = 99; // Using 99 to be extra safe
        private readonly SemaphoreSlim _subscriptionSemaphore = new SemaphoreSlim(1, 1);

        public event Action<TickerPacket>? OnLtpUpdate;
        public event Action<PreviousClosePacket>? OnPreviousCloseUpdate;
        public event Action<QuotePacket>? OnQuoteUpdate;
        public event Action<OiPacket>? OnOiUpdate;
        public event Action<OrderBookEntry>? OnOrderUpdate; // ADDED: Event for order updates
        public event Action? OnConnected;

        private class WebSocketSubscriptionInstrument
        {
            [JsonProperty("exchangeSegment")]
            public string ExchangeSegment { get; set; } = string.Empty;

            [JsonProperty("securityId")]
            public string SecurityId { get; set; } = string.Empty;
        }

        private class WebSocketSubscriptionRequest
        {
            [JsonProperty("requestCode")]
            public int RequestCode { get; set; }

            [JsonProperty("instrumentCount")]
            public int InstrumentCount { get; set; }

            [JsonProperty("instrumentList")]
            public List<WebSocketSubscriptionInstrument> InstrumentList { get; set; } = new List<WebSocketSubscriptionInstrument>();
        }

        public DhanWebSocketClient(string clientId, string accessToken)
        {
            _clientId = clientId;
            _accessToken = accessToken;
        }

        public async Task ConnectAsync()
        {
            if (_webSocket != null && _webSocket.State == WebSocketState.Open) return;
            _webSocket?.Dispose();

            _webSocket = new ClientWebSocket();
            _cancellationTokenSource = new CancellationTokenSource();
            try
            {
                var uri = new Uri($"wss://api-feed.dhan.co?version=2&token={_accessToken}&clientId={_clientId}&authType=2");
                Debug.WriteLine($"[WebSocket] Connecting to: {uri}");
                await _webSocket.ConnectAsync(uri, _cancellationTokenSource.Token);
                Debug.WriteLine("[WebSocket] Connected Successfully.");
                OnConnected?.Invoke();
                _ = Task.Run(StartReceivingAsync, _cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocket] Connection error: {ex.Message}");
            }
        }

        public async Task DisconnectAsync()
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                _cancellationTokenSource?.Cancel();
                try
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Changing symbols", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WebSocket] Error closing websocket: {ex.Message}");
                }
            }
            _webSocket?.Dispose();
            _webSocket = null;
        }

        // ADDED: Method to subscribe to the live order feed
        public async Task SubscribeToOrderFeedAsync()
        {
            if (_webSocket?.State != WebSocketState.Open || _cancellationTokenSource == null) return;

            var orderSubscriptionRequest = new { customers = new[] { _clientId } };
            string jsonRequest = JsonConvert.SerializeObject(orderSubscriptionRequest);
            var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonRequest));

            try
            {
                await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                Debug.WriteLine("[WebSocket] Successfully subscribed to order feed.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocket] Error subscribing to order feed: {ex.Message}");
            }
        }

        public async Task SubscribeToInstrumentsAsync(Dictionary<string, int> instruments, int feedType = 15)
        {
            if (_webSocket?.State != WebSocketState.Open || !instruments.Any() || _cancellationTokenSource == null)
            {
                Debug.WriteLine("[WebSocket] Cannot subscribe - invalid state or no instruments");
                return;
            }

            // Use semaphore to ensure only one subscription request at a time
            await _subscriptionSemaphore.WaitAsync(_cancellationTokenSource.Token);

            try
            {
                // Validate instrument count before processing
                if (instruments.Count > MAX_INSTRUMENTS_PER_REQUEST)
                {
                    Debug.WriteLine($"[WebSocket] ERROR: Attempting to subscribe to {instruments.Count} instruments, but limit is {MAX_INSTRUMENTS_PER_REQUEST}");

                    // Auto-chunk the instruments if too many
                    var chunks = ChunkInstruments(instruments, MAX_INSTRUMENTS_PER_REQUEST);
                    foreach (var chunk in chunks)
                    {
                        await SubscribeToSingleChunkAsync(chunk, feedType);
                        // Add delay between chunks to avoid overwhelming the server
                        await Task.Delay(300, _cancellationTokenSource.Token);
                    }
                    return;
                }

                await SubscribeToSingleChunkAsync(instruments, feedType);
            }
            finally
            {
                _subscriptionSemaphore.Release();
            }
        }

        private async Task SubscribeToSingleChunkAsync(Dictionary<string, int> instruments, int feedType)
        {
            if (_webSocket?.State != WebSocketState.Open || _cancellationTokenSource == null) return;

            var instrumentList = instruments.Select(kvp => new WebSocketSubscriptionInstrument
            {
                SecurityId = kvp.Key,
                ExchangeSegment = GetExchangeSegmentName(kvp.Value)
            }).ToList();

            // Double-check the count before creating the request
            if (instrumentList.Count > MAX_INSTRUMENTS_PER_REQUEST)
            {
                Debug.WriteLine($"[WebSocket] CRITICAL ERROR: InstrumentList count {instrumentList.Count} exceeds limit {MAX_INSTRUMENTS_PER_REQUEST}");
                return;
            }

            var subscriptionRequest = new WebSocketSubscriptionRequest
            {
                RequestCode = feedType,
                InstrumentCount = instrumentList.Count,
                InstrumentList = instrumentList
            };

            string jsonRequest = JsonConvert.SerializeObject(subscriptionRequest);

            // Log the actual request for debugging
            Debug.WriteLine($"[WebSocket] Sending Subscription - FeedType: {feedType}, Count: {subscriptionRequest.InstrumentCount}");
            Debug.WriteLine($"[WebSocket] JSON Length: {jsonRequest.Length} characters");

            // Only log full JSON in debug builds to avoid spam
#if DEBUG
            Debug.WriteLine($"[WebSocket] Full JSON: {jsonRequest}");
#endif

            try
            {
                var buffer = new ArraySegment<byte>(Encoding.UTF8.GetBytes(jsonRequest));
                await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, _cancellationTokenSource.Token);
                Debug.WriteLine($"[WebSocket] Successfully sent subscription for {subscriptionRequest.InstrumentCount} instruments");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocket] Error sending subscription: {ex.Message}");
                throw;
            }
        }

        private static IEnumerable<Dictionary<string, int>> ChunkInstruments(Dictionary<string, int> instruments, int chunkSize)
        {
            var instrumentList = instruments.ToList();
            for (int i = 0; i < instrumentList.Count; i += chunkSize)
            {
                yield return instrumentList
                    .Skip(i)
                    .Take(chunkSize)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }

        private string GetExchangeSegmentName(int segmentId)
        {
            return segmentId switch
            {
                0 => "IDX_I",
                1 => "NSE_EQ",
                2 => "NSE_FNO",
                8 => "BSE_FNO",
                _ => "UNKNOWN"
            };
        }

        private async Task StartReceivingAsync()
        {
            if (_webSocket == null || _cancellationTokenSource == null) return;

            var buffer = new ArraySegment<byte>(new byte[1024 * 8]); // Increased buffer size
            try
            {
                while (_webSocket.State == WebSocketState.Open && !_cancellationTokenSource.IsCancellationRequested)
                {
                    var result = await _webSocket.ReceiveAsync(buffer, _cancellationTokenSource.Token);
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        ParseBinaryMessage(new ArraySegment<byte>(buffer.Array, 0, result.Count));
                    }
                    // ADDED: Logic to handle text messages for order updates
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var jsonString = Encoding.UTF8.GetString(buffer.Array, 0, result.Count);
                        ParseTextMessage(jsonString);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[WebSocket] Receiving task was cancelled.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WebSocket] Error receiving data: {ex.Message}");
            }
        }

        // ADDED: New method to parse JSON-based order updates
        private void ParseTextMessage(string json)
        {
            try
            {
                // Dhan sends a simple "Connected" message which we can ignore
                if (json.Contains("Connected")) return;

                var orderUpdate = JsonConvert.DeserializeObject<OrderBookEntry>(json);
                if (orderUpdate != null && !string.IsNullOrEmpty(orderUpdate.OrderId))
                {
                    Debug.WriteLine($"[PARSER] >>> SUCCESS: Parsed Order Update for OrderId {orderUpdate.OrderId}. Status: {orderUpdate.OrderStatus}");
                    OnOrderUpdate?.Invoke(orderUpdate);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PARSER] CRITICAL ERROR parsing text message: {ex.Message}. JSON: {json}");
            }
        }

        private void ParseBinaryMessage(ArraySegment<byte> data)
        {
            if (data.Array == null) return;

            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count);
                using var reader = new BinaryReader(stream);

                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    byte feedCode = reader.ReadByte();
                    ushort messageLength = reader.ReadUInt16();
                    byte exchangeSegment = reader.ReadByte();
                    int securityId = reader.ReadInt32();

                    long messageEndPosition = reader.BaseStream.Position - 8 + messageLength;
                    if (messageEndPosition > reader.BaseStream.Length)
                    {
                        Debug.WriteLine($"[PARSER] Incomplete packet received. Stated length {messageLength} exceeds buffer size.");
                        break;
                    }

                    switch (feedCode)
                    {
                        case 2: // Ticker Packet
                            if (reader.BaseStream.Position + 8 <= messageEndPosition)
                            {
                                float ltp = reader.ReadSingle();
                                int lastTradeTime = reader.ReadInt32();
                                OnLtpUpdate?.Invoke(new TickerPacket { SecurityId = securityId.ToString(), LastPrice = (decimal)ltp, LastTradeTime = lastTradeTime });
                            }
                            break;

                        case 4: // Quote Packet
                            if (reader.BaseStream.Position + 42 <= messageEndPosition)
                            {
                                var quote = new QuotePacket { SecurityId = securityId.ToString(), LastPrice = (decimal)reader.ReadSingle(), LastTradeQuantity = reader.ReadInt16(), LastTradeTime = reader.ReadInt32(), AvgTradePrice = (decimal)reader.ReadSingle(), Volume = reader.ReadInt32(), TotalSellQuantity = reader.ReadInt32(), TotalBuyQuantity = reader.ReadInt32(), Open = (decimal)reader.ReadSingle(), Close = (decimal)reader.ReadSingle(), High = (decimal)reader.ReadSingle(), Low = (decimal)reader.ReadSingle() };
                                OnQuoteUpdate?.Invoke(quote);
                                Debug.WriteLine($"[PARSER] >>> SUCCESS: Parsed Quote for SecId {securityId}. LTP: {quote.LastPrice}, Volume: {quote.Volume}");
                            }
                            break;

                        case 5: // OI Packet
                            if (reader.BaseStream.Position + 4 <= messageEndPosition)
                            {
                                int openInterest = reader.ReadInt32();
                                Debug.WriteLine($"[PARSER] >>> SUCCESS: Parsed OI Packet for SecId {securityId}. Open Interest: {openInterest}");
                                OnOiUpdate?.Invoke(new OiPacket { SecurityId = securityId.ToString(), OpenInterest = openInterest });
                            }
                            break;

                        case 6: // Previous Close Packet
                            if (reader.BaseStream.Position + 4 <= messageEndPosition)
                            {
                                float prevClose = reader.ReadSingle();
                                OnPreviousCloseUpdate?.Invoke(new PreviousClosePacket { SecurityId = securityId.ToString(), PreviousClose = (decimal)prevClose });
                            }
                            break;

                        default:
                            Debug.WriteLine($"[PARSER] Unknown Feed Code: {feedCode}, SecId: {securityId}. Skipping.");
                            break;
                    }

                    reader.BaseStream.Position = messageEndPosition;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PARSER] CRITICAL ERROR parsing binary message: {ex.GetType().Name} - {ex.Message}");
                if (ex.InnerException != null) Debug.WriteLine($"[PARSER] Inner Exception: {ex.InnerException.Message}");
            }
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _webSocket?.Dispose();
            _subscriptionSemaphore?.Dispose();
        }
    }
}
