using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TradingConsole.DhanApi.Models;

namespace TradingConsole.DhanApi
{
    public class DhanApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _apiCallSemaphore = new SemaphoreSlim(1, 1);
        public const int ApiCallDelayMs = 210;

        private class OptionChainRequestPayload
        {
            [JsonProperty("UnderlyingScrip")]
            public int UnderlyingScrip { get; set; }

            [JsonProperty("UnderlyingSeg")]
            public string UnderlyingSeg { get; set; }

            [JsonProperty("Expiry", NullValueHandling = NullValueHandling.Ignore)]
            public string? Expiry { get; set; }
        }

        public DhanApiClient(string clientId, string accessToken)
        {
            if (string.IsNullOrWhiteSpace(clientId))
                throw new ArgumentException("Client ID cannot be null or empty.", nameof(clientId));
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("Access token cannot be null or empty.", nameof(accessToken));

            _httpClient = new HttpClient { BaseAddress = new Uri("https://api.dhan.co") };

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("access-token", accessToken);
            _httpClient.DefaultRequestHeaders.Add("client-id", clientId);
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        private async Task<T?> HandleResponse<T>(HttpResponseMessage response, string apiName) where T : class
        {
            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"SUCCESS ({apiName}): {response.StatusCode}");
                return JsonConvert.DeserializeObject<T>(responseBody);
            }
            else
            {
                string errorBody = await response.Content.ReadAsStringAsync();
                Debug.WriteLine($"ERROR ({apiName}): {response.StatusCode} - {errorBody}");
                return null;
            }
        }

        private async Task<T?> ExecuteApiCall<T>(Func<Task<HttpResponseMessage>> apiCallFunc, string apiName) where T : class
        {
            await _apiCallSemaphore.WaitAsync();
            try
            {
                var response = await apiCallFunc();
                return await HandleResponse<T>(response, apiName);
            }
            finally
            {
                _apiCallSemaphore.Release();
                await Task.Delay(ApiCallDelayMs);
            }
        }

        public async Task<List<PositionResponse>?> GetPositionsAsync()
        {
            try
            {
                return await ExecuteApiCall<List<PositionResponse>>(() => _httpClient.GetAsync("/v2/positions"), "GetPositions");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in GetPositionsAsync: {e.Message}");
                return new List<PositionResponse>();
            }
        }

        public async Task<FundLimitResponse?> GetFundLimitAsync()
        {
            try
            {
                return await ExecuteApiCall<FundLimitResponse>(() => _httpClient.GetAsync("/v2/fundlimit"), "GetFundLimit");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in GetFundLimitAsync: {e.Message}");
                return null;
            }
        }

        public async Task<ExpiryListResponse?> GetExpiryListAsync(string underlyingScripId, string segment)
        {
            try
            {
                return await ExecuteApiCall<ExpiryListResponse>(async () =>
                {
                    var payload = new OptionChainRequestPayload { UnderlyingScrip = int.Parse(underlyingScripId), UnderlyingSeg = segment };
                    string jsonPayload = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    Debug.WriteLine($"[DhanApiClient_GetExpiryList] Request Payload: {jsonPayload}");
                    return await _httpClient.PostAsync("/v2/optionchain/expirylist", content);
                }, "GetExpiryList");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in GetExpiryListAsync: {e.Message}");
                return null;
            }
        }

        public async Task<OptionChainResponse?> GetOptionChainAsync(string underlyingScripId, string segment, string expiryDate)
        {
            try
            {
                return await ExecuteApiCall<OptionChainResponse>(async () =>
                {
                    var payload = new OptionChainRequestPayload
                    {
                        UnderlyingScrip = int.Parse(underlyingScripId),
                        UnderlyingSeg = segment,
                        Expiry = expiryDate
                    };
                    string jsonPayload = JsonConvert.SerializeObject(payload);
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    Debug.WriteLine($"[DhanApiClient_GetOptionChain] Request Payload: {jsonPayload}");
                    return await _httpClient.PostAsync("/v2/optionchain", content);
                }, "GetOptionChain");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in GetOptionChainAsync: {e.Message}");
                return null;
            }
        }

        public async Task<QuoteResponse?> GetQuoteAsync(string securityId)
        {
            if (string.IsNullOrEmpty(securityId)) return null;

            try
            {
                return await ExecuteApiCall<QuoteResponse>(() => _httpClient.GetAsync($"/v2/marketdata/quote/{securityId}"), "GetQuote");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in GetQuoteAsync: {e.Message}");
                return null;
            }
        }

        public async Task<OrderResponse?> PlaceOrderAsync(OrderRequest orderRequest)
        {
            return await ExecuteApiCall<OrderResponse>(async () =>
            {
                var requestUrl = "/v2/orders";
                var jsonPayload = JsonConvert.SerializeObject(orderRequest);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                return await _httpClient.PostAsync(requestUrl, httpContent);
            }, "PlaceOrder");
        }

        // ADDED: New method for placing slice orders
        public async Task<OrderResponse?> PlaceSliceOrderAsync(SliceOrderRequest sliceRequest)
        {
            return await ExecuteApiCall<OrderResponse>(async () =>
            {
                var requestUrl = "/v2/orders/slice";
                var jsonPayload = JsonConvert.SerializeObject(sliceRequest);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                return await _httpClient.PostAsync(requestUrl, httpContent);
            }, "PlaceSliceOrder");
        }

        public async Task<List<OrderBookEntry>?> GetOrderBookAsync()
        {
            return await ExecuteApiCall<List<OrderBookEntry>>(() => _httpClient.GetAsync("/v2/orders"), "GetOrderBook");
        }

        public async Task<OrderResponse?> ModifyOrderAsync(ModifyOrderRequest modifyRequest)
        {
            return await ExecuteApiCall<OrderResponse>(async () =>
            {
                var requestUrl = $"/v2/orders/{modifyRequest.OrderId}";
                var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                return await _httpClient.PutAsync(requestUrl, httpContent);
            }, "ModifyOrder");
        }

        public async Task<OrderResponse?> CancelOrderAsync(string orderId)
        {
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.DeleteAsync($"/v2/orders/{orderId}"), "CancelOrder");
        }

        public async Task<bool> ConvertPositionAsync(ConvertPositionRequest request)
        {
            await _apiCallSemaphore.WaitAsync();
            try
            {
                var requestUrl = "/v2/positions/convert";
                var jsonPayload = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(requestUrl, httpContent);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in ConvertPositionAsync: {e.Message}");
                return false;
            }
            finally
            {
                _apiCallSemaphore.Release();
                await Task.Delay(ApiCallDelayMs);
            }
        }

        public async Task<OrderResponse?> PlaceSuperOrderAsync(SuperOrderRequest orderRequest)
        {
            return await ExecuteApiCall<OrderResponse>(async () =>
            {
                var requestUrl = "/v2/super/orders";
                var jsonPayload = JsonConvert.SerializeObject(orderRequest);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                return await _httpClient.PostAsync(requestUrl, httpContent);
            }, "PlaceSuperOrder");
        }

        public async Task<OrderResponse?> ModifySuperOrderAsync(ModifySuperOrderRequest modifyRequest)
        {
            return await ExecuteApiCall<OrderResponse>(async () =>
            {
                var requestUrl = $"/v2/super/orders/{modifyRequest.OrderId}";
                var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                return await _httpClient.PutAsync(requestUrl, httpContent);
            }, "ModifySuperOrder");
        }

        public async Task<OrderResponse?> CancelSuperOrderAsync(string orderId)
        {
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.DeleteAsync($"/v2/super/orders/{orderId}"), "CancelSuperOrder");
        }
    }
}
