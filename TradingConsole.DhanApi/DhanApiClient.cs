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
        public const int ApiCallDelayMs = 210; // Minimum delay between API calls to respect rate limits.

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

        /// <summary>
        /// Handles the HTTP response, deserializing the content on success or throwing a detailed exception on failure.
        /// </summary>
        /// <typeparam name="T">The type to deserialize the JSON response into.</typeparam>
        /// <param name="response">The HttpResponseMessage from the API call.</param>
        /// <param name="apiName">The name of the API endpoint for logging purposes.</param>
        /// <returns>The deserialized object.</returns>
        /// <exception cref="HttpRequestException">Thrown if the API returns a non-success status code.</exception>
        /// <exception cref="JsonException">Thrown if the response body cannot be deserialized to the target type.</exception>
        private async Task<T?> HandleResponse<T>(HttpResponseMessage response, string apiName) where T : class
        {
            string responseBody = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"SUCCESS ({apiName}): {response.StatusCode}");
                // This can throw a JsonException, which will be caught by the calling method.
                return JsonConvert.DeserializeObject<T>(responseBody);
            }
            else
            {
                Debug.WriteLine($"ERROR ({apiName}): {response.StatusCode} - {responseBody}");
                // This will throw an HttpRequestException, which is more specific and contains response details.
                response.EnsureSuccessStatusCode();
                return null; // Should not be reached.
            }
        }

        /// <summary>
        /// A generic wrapper for executing API calls that enforces rate limiting and provides centralized error handling.
        /// </summary>
        /// <typeparam name="T">The expected return type from the API call.</typeparam>
        /// <param name="apiCallFunc">A function that returns the Task<HttpResponseMessage> for the API call.</param>
        /// <param name="apiName">The name of the API for logging.</param>
        /// <returns>The deserialized API response.</returns>
        /// <exception cref="DhanApiException">Wraps exceptions from the HTTP client or JSON deserialization for uniform handling by the caller.</exception>
        private async Task<T?> ExecuteApiCall<T>(Func<Task<HttpResponseMessage>> apiCallFunc, string apiName) where T : class
        {
            // Wait for the semaphore to ensure only one API call happens at a time, respecting rate limits.
            await _apiCallSemaphore.WaitAsync();
            try
            {
                var response = await apiCallFunc();
                return await HandleResponse<T>(response, apiName);
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[DhanApiClient] HTTP Request Exception in {apiName}: {ex.Message}");
                throw new DhanApiException($"Network error in {apiName}. Please check your connection or API status.", ex);
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[DhanApiClient] JSON Deserialization Exception in {apiName}: {ex.Message}");
                throw new DhanApiException($"Error parsing response from {apiName}. The API might have changed.", ex);
            }
            // A generic catch is intentionally omitted to let truly unexpected errors bubble up.
            finally
            {
                // Release the semaphore and wait for the minimum delay period.
                _apiCallSemaphore.Release();
                await Task.Delay(ApiCallDelayMs);
            }
        }

        public async Task<List<PositionResponse>?> GetPositionsAsync()
        {
            return await ExecuteApiCall<List<PositionResponse>>(() => _httpClient.GetAsync("/v2/positions"), "GetPositions");
        }

        public async Task<FundLimitResponse?> GetFundLimitAsync()
        {
            return await ExecuteApiCall<FundLimitResponse>(() => _httpClient.GetAsync("/v2/fundlimit"), "GetFundLimit");
        }

        public async Task<ExpiryListResponse?> GetExpiryListAsync(string underlyingScripId, string segment)
        {
            var payload = new OptionChainRequestPayload { UnderlyingScrip = int.Parse(underlyingScripId), UnderlyingSeg = segment };
            string jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            Debug.WriteLine($"[DhanApiClient_GetExpiryList] Request Payload: {jsonPayload}");
            return await ExecuteApiCall<ExpiryListResponse>(() => _httpClient.PostAsync("/v2/optionchain/expirylist", content), "GetExpiryList");
        }

        public async Task<OptionChainResponse?> GetOptionChainAsync(string underlyingScripId, string segment, string expiryDate)
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
            return await ExecuteApiCall<OptionChainResponse>(() => _httpClient.PostAsync("/v2/optionchain", content), "GetOptionChain");
        }

        public async Task<QuoteResponse?> GetQuoteAsync(string securityId)
        {
            if (string.IsNullOrEmpty(securityId)) return null;
            return await ExecuteApiCall<QuoteResponse>(() => _httpClient.GetAsync($"/v2/marketdata/quote/{securityId}"), "GetQuote");
        }

        public async Task<OrderResponse?> PlaceOrderAsync(OrderRequest orderRequest)
        {
            var requestUrl = "/v2/orders";
            var jsonPayload = JsonConvert.SerializeObject(orderRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.PostAsync(requestUrl, httpContent), "PlaceOrder");
        }

        public async Task<OrderResponse?> PlaceSliceOrderAsync(SliceOrderRequest sliceRequest)
        {
            var requestUrl = "/v2/orders/slice";
            var jsonPayload = JsonConvert.SerializeObject(sliceRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.PostAsync(requestUrl, httpContent), "PlaceSliceOrder");
        }

        public async Task<List<OrderBookEntry>?> GetOrderBookAsync()
        {
            return await ExecuteApiCall<List<OrderBookEntry>>(() => _httpClient.GetAsync("/v2/orders"), "GetOrderBook");
        }

        public async Task<OrderResponse?> ModifyOrderAsync(ModifyOrderRequest modifyRequest)
        {
            var requestUrl = $"/v2/orders/{modifyRequest.OrderId}";
            var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.PutAsync(requestUrl, httpContent), "ModifyOrder");
        }

        public async Task<OrderResponse?> CancelOrderAsync(string orderId)
        {
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.DeleteAsync($"/v2/orders/{orderId}"), "CancelOrder");
        }

        public async Task<bool> ConvertPositionAsync(ConvertPositionRequest request)
        {
            // This method is slightly different as it returns a boolean based on status code, not a deserialized object.
            await _apiCallSemaphore.WaitAsync();
            try
            {
                var requestUrl = "/v2/positions/convert";
                var jsonPayload = JsonConvert.SerializeObject(request);
                var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(requestUrl, httpContent);
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
                _apiCallSemaphore.Release();
                await Task.Delay(ApiCallDelayMs);
            }
        }

        public async Task<OrderResponse?> PlaceSuperOrderAsync(SuperOrderRequest orderRequest)
        {
            var requestUrl = "/v2/super/orders";
            var jsonPayload = JsonConvert.SerializeObject(orderRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.PostAsync(requestUrl, httpContent), "PlaceSuperOrder");
        }

        public async Task<OrderResponse?> ModifySuperOrderAsync(ModifySuperOrderRequest modifyRequest)
        {
            var requestUrl = $"/v2/super/orders/{modifyRequest.OrderId}";
            var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.PutAsync(requestUrl, httpContent), "ModifySuperOrder");
        }

        public async Task<OrderResponse?> CancelSuperOrderAsync(string orderId)
        {
            return await ExecuteApiCall<OrderResponse>(() => _httpClient.DeleteAsync($"/v2/super/orders/{orderId}"), "CancelSuperOrder");
        }
    }
}
