﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using NodaTime;
using RestSharp;
using Newtonsoft.Json;
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Data.Market;
using QuantConnect.Lean.DataSource.CoinAPI.Messages;
using QuantConnect.Lean.Engine.DataFeeds;
using HistoryRequest = QuantConnect.Data.HistoryRequest;
using QuantConnect.Lean.DataSource.CoinAPI.Models;

namespace QuantConnect.Lean.DataSource.CoinAPI
{
    public partial class CoinApiDataProvider
    {
        private readonly RestClient restClient = new RestClient();

        private readonly RestRequest restRequest = new(Method.GET);

        /// <summary>
        /// Indicates whether the warning for invalid history <see cref="TickType"/> has been fired.
        /// </summary>
        private bool _invalidHistoryDataTypeWarningFired;

        /// <summary>
        /// Indicates whether the warning for invalid <see cref="SecurityType"/> has been fired.
        /// </summary>
        private bool _invalidSecurityTypeWarningFired;

        /// <summary>
        /// Indicates whether the warning for invalid <see cref="Resolution"/> has been fired.
        /// </summary>
        private bool _invalidResolutionTypeWarningFired;

        /// <summary>
        /// Indicates whether a warning for an invalid start time has been fired, where the start time is greater than or equal to the end time in UTC.
        /// </summary>
        private bool _invalidStartTimeWarningFired;

        public override void Initialize(HistoryProviderInitializeParameters parameters)
        {
            // NOP
        }

        public override IEnumerable<Slice>? GetHistory(IEnumerable<HistoryRequest> requests, DateTimeZone sliceTimeZone)
        {
            var subscriptions = new List<Subscription>();
            foreach (var request in requests)
            {
                var history = GetHistory(request);

                if (history == null)
                {
                    continue;
                }

                var subscription = CreateSubscription(request, history);
                subscriptions.Add(subscription);
            }

            if (subscriptions.Count == 0)
            {
                return null;
            }
            return CreateSliceEnumerableFromSubscriptions(subscriptions, sliceTimeZone);
        }

        public IEnumerable<BaseData>? GetHistory(HistoryRequest historyRequest)
        {
            if (!CanSubscribe(historyRequest.Symbol))
            {
                if (!_invalidSecurityTypeWarningFired)
                {
                    Log.Error($"CoinApiDataProvider.GetHistory(): Invalid security type {historyRequest.Symbol.SecurityType}");
                    _invalidSecurityTypeWarningFired = true;
                }
                return null;
            }

            if (historyRequest.Resolution == Resolution.Tick)
            {
                if (!_invalidResolutionTypeWarningFired)
                {
                    Log.Error($"CoinApiDataProvider.GetHistory(): No historical ticks, only OHLCV timeseries");
                    _invalidResolutionTypeWarningFired = true;
                }
                return null;
            }

            if (historyRequest.DataType == typeof(QuoteBar))
            {
                if (!_invalidHistoryDataTypeWarningFired)
                {
                    Log.Error("CoinApiDataProvider.GetHistory(): No historical QuoteBars , only TradeBars");
                    _invalidHistoryDataTypeWarningFired = true;
                }
                return null;
            }

            if (historyRequest.EndTimeUtc < historyRequest.StartTimeUtc)
            {
                if (!_invalidStartTimeWarningFired)
                {
                    Log.Error($"{nameof(CoinAPIDataDownloader)}.{nameof(GetHistory)}:InvalidDateRange. The history request start date must precede the end date, no history returned");
                    _invalidStartTimeWarningFired = true;
                }
                return null;
            }

            return GetHistory(historyRequest.Symbol,
                historyRequest.Resolution,
                historyRequest.StartTimeUtc,
                historyRequest.EndTimeUtc
                );
        }

        private IEnumerable<BaseData> GetHistory(Symbol symbol, Resolution resolution, DateTime startDateTimeUtc, DateTime endDateTimeUtc)
        {
            var resolutionTimeSpan = resolution.ToTimeSpan();
            var lastRequestedBarStartTime = endDateTimeUtc.RoundDown(resolutionTimeSpan);
            var currentStartTime = startDateTimeUtc.RoundUp(resolutionTimeSpan);
            var currentEndTime = lastRequestedBarStartTime;

            // Perform a check of the number of bars requested, this must not exceed a static limit
            var dataRequestedCount = (currentEndTime - currentStartTime).Ticks
                                     / resolutionTimeSpan.Ticks;

            if (dataRequestedCount > HistoricalDataPerRequestLimit)
            {
                currentEndTime = currentStartTime
                                 + TimeSpan.FromTicks(resolutionTimeSpan.Ticks * HistoricalDataPerRequestLimit);
            }

            while (currentStartTime < lastRequestedBarStartTime)
            {
                var coinApiSymbol = _symbolMapper.GetBrokerageSymbol(symbol);
                var coinApiPeriod = _ResolutionToCoinApiPeriodMappings[resolution];

                // Time must be in ISO 8601 format
                var coinApiStartTime = currentStartTime.ToStringInvariant("s");
                var coinApiEndTime = currentEndTime.ToStringInvariant("s");

                // Construct URL for rest request
                restClient.BaseUrl = new Uri("https://rest.coinapi.io/v1/ohlcv/" +
                    $"{coinApiSymbol}/history?period_id={coinApiPeriod}&limit={HistoricalDataPerRequestLimit}" +
                    $"&time_start={coinApiStartTime}&time_end={coinApiEndTime}");

                restRequest.AddOrUpdateHeader("X-CoinAPI-Key", _apiKey);
                var response = restClient.Execute(restRequest);

                // Log the information associated with the API Key's rest call limits.
                TraceRestUsage(response);

                HistoricalDataMessage[]? coinApiHistoryBars;
                try
                {
                    // Deserialize to array
                    coinApiHistoryBars = JsonConvert.DeserializeObject<HistoricalDataMessage[]>(response.Content);
                }
                catch (JsonSerializationException)
                {
                    var error = JsonConvert.DeserializeObject<CoinApiErrorResponse>(response.Content);
                    throw new Exception(error.Error);
                }

                // Can be no historical data for a short period interval
                if (!coinApiHistoryBars.Any())
                {
                    Log.Error($"CoinApiDataProvider.GetHistory(): API returned no data for the requested period [{coinApiStartTime} - {coinApiEndTime}] for symbol [{symbol}]");
                    continue;
                }

                foreach (var ohlcv in coinApiHistoryBars)
                {
                    yield return
                        new TradeBar(ohlcv.TimePeriodStart, symbol, ohlcv.PriceOpen, ohlcv.PriceHigh,
                            ohlcv.PriceLow, ohlcv.PriceClose, ohlcv.VolumeTraded, resolutionTimeSpan);
                }

                currentStartTime = currentEndTime;
                currentEndTime += TimeSpan.FromTicks(resolutionTimeSpan.Ticks * HistoricalDataPerRequestLimit);
            }
        }

        private void TraceRestUsage(IRestResponse response)
        {
            var total = GetHttpHeaderValue(response, "x-ratelimit-limit");
            var used = GetHttpHeaderValue(response, "x-ratelimit-used");
            var remaining = GetHttpHeaderValue(response, "x-ratelimit-remaining");

            Log.Trace($"CoinApiDataProvider.TraceRestUsage(): Used {used}, Remaining {remaining}, Total {total}");
        }

        private string GetHttpHeaderValue(IRestResponse response, string propertyName)
        {
            return response.Headers
                .FirstOrDefault(x => x.Name == propertyName)?
                .Value.ToString();
        }

        // WARNING: here to be called from tests to reduce explicitly the amount of request's output 
        protected void SetUpHistDataLimit(int limit)
        {
            HistoricalDataPerRequestLimit = limit;
        }
    }
}
