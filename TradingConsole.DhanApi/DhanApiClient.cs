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

        // --- MODIFIED: Implemented three separate semaphores for different API lanes ---
        // 1. For time-critical orders, with ZERO delay.
        private readonly SemaphoreSlim _orderApiSemaphore = new SemaphoreSlim(1, 1);
        // 2. For the option chain, with a strict 3-second delay.
        private readonly SemaphoreSlim _optionChainApiSemaphore = new SemaphoreSlim(1, 1);
        // 3. For all other non-critical data calls, with a safe default delay.
        private readonly SemaphoreSlim _generalApiSemaphore = new SemaphoreSlim(1, 1);

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
            string responseBody = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"SUCCESS ({apiName}): {response.StatusCode}");
                return JsonConvert.DeserializeObject<T>(responseBody);
            }
            else
            {
                Debug.WriteLine($"ERROR ({apiName}): {response.StatusCode} - {responseBody}");
                response.EnsureSuccessStatusCode();
                return null;
            }
        }

        // --- ADDED: High-priority execution wrapper for orders with ZERO delay ---
        private async Task<T?> ExecuteOrderApiCall<T>(Func<Task<HttpResponseMessage>> apiCallFunc, string apiName) where T : class
        {
            await _orderApiSemaphore.WaitAsync();
            try
            {
                var response = await apiCallFunc();
                return await HandleResponse<T>(response, apiName);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException)
            {
                Debug.WriteLine($"[DhanApiClient] Exception in {apiName}: {ex.Message}");
                throw new DhanApiException($"Error in {apiName}. See inner exception for details.", ex);
            }
            finally
            {
                // No delay for orders
                _orderApiSemaphore.Release();
            }
        }

        // --- ADDED: Low-priority execution wrapper for Option Chain with a 3.1s delay ---
        private async Task<T?> ExecuteOptionChainApiCall<T>(Func<Task<HttpResponseMessage>> apiCallFunc, string apiName) where T : class
        {
            await _optionChainApiSemaphore.WaitAsync();
            try
            {
                var response = await apiCallFunc();
                // Enforce the strict 3-second rate limit for option chain calls by waiting AFTER the call
                await Task.Delay(3100);
                return await HandleResponse<T>(response, apiName);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException)
            {
                Debug.WriteLine($"[DhanApiClient] Exception in {apiName}: {ex.Message}");
                throw new DhanApiException($"Error in {apiName}. See inner exception for details.", ex);
            }
            finally
            {
                _optionChainApiSemaphore.Release();
            }
        }

        // --- ADDED: Wrapper for general data calls with a safe 250ms delay ---
        private async Task<T?> ExecuteGeneralApiCall<T>(Func<Task<HttpResponseMessage>> apiCallFunc, string apiName) where T : class
        {
            await _generalApiSemaphore.WaitAsync();
            try
            {
                var response = await apiCallFunc();
                await Task.Delay(250); // Safe delay for non-critical API calls
                return await HandleResponse<T>(response, apiName);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException)
            {
                Debug.WriteLine($"[DhanApiClient] Exception in {apiName}: {ex.Message}");
                throw new DhanApiException($"Error in {apiName}. See inner exception for details.", ex);
            }
            finally
            {
                _generalApiSemaphore.Release();
            }
        }

        // --- MODIFIED: All methods now use the appropriate execution lane ---

        public async Task<List<PositionResponse>?> GetPositionsAsync()
        {
            return await ExecuteGeneralApiCall<List<PositionResponse>>(() => _httpClient.GetAsync("/v2/positions"), "GetPositions");
        }

        public async Task<FundLimitResponse?> GetFundLimitAsync()
        {
            return await ExecuteGeneralApiCall<FundLimitResponse>(() => _httpClient.GetAsync("/v2/fundlimit"), "GetFundLimit");
        }

        public async Task<ExpiryListResponse?> GetExpiryListAsync(string underlyingScripId, string segment)
        {
            var payload = new OptionChainRequestPayload { UnderlyingScrip = int.Parse(underlyingScripId), UnderlyingSeg = segment };
            string jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteGeneralApiCall<ExpiryListResponse>(() => _httpClient.PostAsync("/v2/optionchain/expirylist", content), "GetExpiryList");
        }

        public async Task<OptionChainResponse?> GetOptionChainAsync(string underlyingScripId, string segment, string expiryDate)
        {
            var payload = new OptionChainRequestPayload { UnderlyingScrip = int.Parse(underlyingScripId), UnderlyingSeg = segment, Expiry = expiryDate };
            string jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            // Uses the dedicated, rate-limited lane for option chains
            return await ExecuteOptionChainApiCall<OptionChainResponse>(() => _httpClient.PostAsync("/v2/optionchain", content), "GetOptionChain");
        }

        public async Task<QuoteResponse?> GetQuoteAsync(string securityId)
        {
            if (string.IsNullOrEmpty(securityId)) return null;
            return await ExecuteGeneralApiCall<QuoteResponse>(() => _httpClient.GetAsync($"/v2/marketdata/quote/{securityId}"), "GetQuote");
        }

        public async Task<OrderResponse?> PlaceOrderAsync(OrderRequest orderRequest)
        {
            var jsonPayload = JsonConvert.SerializeObject(orderRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteOrderApiCall<OrderResponse>(() => _httpClient.PostAsync("/v2/orders", httpContent), "PlaceOrder");
        }

        public async Task<OrderResponse?> PlaceSliceOrderAsync(SliceOrderRequest sliceRequest)
        {
            var jsonPayload = JsonConvert.SerializeObject(sliceRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteOrderApiCall<OrderResponse>(() => _httpClient.PostAsync("/v2/orders/slice", httpContent), "PlaceSliceOrder");
        }

        public async Task<List<OrderBookEntry>?> GetOrderBookAsync()
        {
            return await ExecuteGeneralApiCall<List<OrderBookEntry>>(() => _httpClient.GetAsync("/v2/orders"), "GetOrderBook");
        }

        public async Task<OrderResponse?> ModifyOrderAsync(ModifyOrderRequest modifyRequest)
        {
            var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteOrderApiCall<OrderResponse>(() => _httpClient.PutAsync($"/v2/orders/{modifyRequest.OrderId}", httpContent), "ModifyOrder");
        }

        public async Task<OrderResponse?> CancelOrderAsync(string orderId)
        {
            return await ExecuteOrderApiCall<OrderResponse>(() => _httpClient.DeleteAsync($"/v2/orders/{orderId}"), "CancelOrder");
        }

        public async Task<bool> ConvertPositionAsync(ConvertPositionRequest request)
        {
            await _orderApiSemaphore.WaitAsync();
            try
            {
                var jsonPayload = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync("/v2/positions/convert", httpContent);
                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    throw new DhanApiException($"Failed to convert position. API returned {response.StatusCode}: {errorBody}");
                }
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException ex)
            {
                throw new DhanApiException("Network error while converting position.", ex);
            }
            finally
            {
                _orderApiSemaphore.Release();
            }
        }

        public async Task<OrderResponse?> PlaceSuperOrderAsync(SuperOrderRequest orderRequest)
        {
            var jsonPayload = JsonConvert.SerializeObject(orderRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteOrderApiCall<OrderResponse>(() => _httpClient.PostAsync("/v2/super/orders", httpContent), "PlaceSuperOrder");
        }

        public async Task<OrderResponse?> ModifySuperOrderAsync(ModifySuperOrderRequest modifyRequest)
        {
            var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteOrderApiCall<OrderResponse>(() => _httpClient.PutAsync($"/v2/super/orders/{modifyRequest.OrderId}", httpContent), "ModifySuperOrder");
        }

        public async Task<OrderResponse?> CancelSuperOrderAsync(string orderId)
        {
            return await ExecuteOrderApiCall<OrderResponse>(() => _httpClient.DeleteAsync($"/v2/super/orders/{orderId}"), "CancelSuperOrder");
        }
    }
}
