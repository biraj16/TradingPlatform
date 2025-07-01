using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using TradingConsole.DhanApi.Models;

namespace TradingConsole.DhanApi
{
    public class DhanApiClient
    {
        private readonly HttpClient _httpClient;

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

        public async Task<List<PositionResponse>?> GetPositionsAsync()
        {
            try
            {
                HttpResponseMessage response = await _httpClient.GetAsync("/v2/positions");
                return await HandleResponse<List<PositionResponse>>(response, "GetPositions");
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
                HttpResponseMessage response = await _httpClient.GetAsync("/v2/fundlimit");
                return await HandleResponse<FundLimitResponse>(response, "GetFundLimit");
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
                var payload = new OptionChainRequestPayload { UnderlyingScrip = int.Parse(underlyingScripId), UnderlyingSeg = segment };
                string jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                // --- DEBUGGING: Log the exact payload being sent ---
                Debug.WriteLine($"[DhanApiClient_GetExpiryList] Request Payload: {jsonPayload}");

                HttpResponseMessage response = await _httpClient.PostAsync("/v2/optionchain/expirylist", content);
                return await HandleResponse<ExpiryListResponse>(response, "GetExpiryList");
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
                var payload = new OptionChainRequestPayload
                {
                    UnderlyingScrip = int.Parse(underlyingScripId),
                    UnderlyingSeg = segment,
                    Expiry = expiryDate
                };
                string jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                // --- DEBUGGING: Log the exact payload being sent ---
                Debug.WriteLine($"[DhanApiClient_GetOptionChain] Request Payload: {jsonPayload}");

                HttpResponseMessage response = await _httpClient.PostAsync("/v2/optionchain", content);
                return await HandleResponse<OptionChainResponse>(response, "GetOptionChain");
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
                HttpResponseMessage response = await _httpClient.GetAsync($"/v2/marketdata/quote/{securityId}");
                return await HandleResponse<QuoteResponse>(response, "GetQuote");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in GetQuoteAsync: {e.Message}");
                return null;
            }
        }

        public async Task<OrderResponse?> PlaceOrderAsync(OrderRequest orderRequest)
        {
            var requestUrl = "/v2/orders";
            var jsonPayload = JsonConvert.SerializeObject(orderRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(requestUrl, httpContent);
            return await HandleResponse<OrderResponse>(response, "PlaceOrder");
        }

        public async Task<List<OrderBookEntry>?> GetOrderBookAsync()
        {
            var requestUrl = "/v2/orders";
            var response = await _httpClient.GetAsync(requestUrl);
            return await HandleResponse<List<OrderBookEntry>>(response, "GetOrderBook");
        }

        public async Task<OrderResponse?> ModifyOrderAsync(ModifyOrderRequest modifyRequest)
        {
            var requestUrl = $"/v2/orders/{modifyRequest.OrderId}";
            var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync(requestUrl, httpContent);
            return await HandleResponse<OrderResponse>(response, "ModifyOrder");
        }

        public async Task<OrderResponse?> CancelOrderAsync(string orderId)
        {
            var requestUrl = $"/v2/orders/{orderId}";
            var response = await _httpClient.DeleteAsync(requestUrl);
            return await HandleResponse<OrderResponse>(response, "CancelOrder");
        }

        public async Task<bool> ConvertPositionAsync(ConvertPositionRequest request)
        {
            var requestUrl = "/v2/positions/convert";
            var jsonPayload = JsonConvert.SerializeObject(request);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync(requestUrl, httpContent);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e)
            {
                Debug.WriteLine($"FATAL in ConvertPositionAsync: {e.Message}");
                return false;
            }
        }

        public async Task<OrderResponse?> PlaceSuperOrderAsync(SuperOrderRequest orderRequest)
        {
            var requestUrl = "/v2/super/orders";
            var jsonPayload = JsonConvert.SerializeObject(orderRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(requestUrl, httpContent);
            return await HandleResponse<OrderResponse>(response, "PlaceSuperOrder");
        }

        public async Task<OrderResponse?> ModifySuperOrderAsync(ModifySuperOrderRequest modifyRequest)
        {
            var requestUrl = $"/v2/super/orders/{modifyRequest.OrderId}";
            var jsonPayload = JsonConvert.SerializeObject(modifyRequest);
            var httpContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PutAsync(requestUrl, httpContent);
            return await HandleResponse<OrderResponse>(response, "ModifySuperOrder");
        }

        public async Task<OrderResponse?> CancelSuperOrderAsync(string orderId)
        {
            var requestUrl = $"/v2/super/orders/{orderId}";
            var response = await _httpClient.DeleteAsync(requestUrl);
            return await HandleResponse<OrderResponse>(response, "CancelSuperOrder");
        }
    }
}
