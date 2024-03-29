﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 *
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using NUnit.Framework;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Data.Market;
using QuantConnect.Securities;

namespace QuantConnect.Lean.DataSource.CoinAPI.Tests
{
    [TestFixture]
    public class CoinAPIHistoryProviderTests
    {
        private static readonly Symbol _CoinbaseBtcUsdSymbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Coinbase);
        private static readonly Symbol _BitfinexBtcUsdSymbol = Symbol.Create("BTCUSD", SecurityType.Crypto, Market.Bitfinex);
        private CoinApiDataQueueHandlerMock _coinApiDataQueueHandler;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            _coinApiDataQueueHandler = new CoinApiDataQueueHandlerMock();
        }

        // -- DATA TO TEST --
        private static TestCaseData[] TestData => new[]
        {
            // No data - invalid resolution or data type, or period is more than limit
            new TestCaseData(_BitfinexBtcUsdSymbol, Resolution.Tick, typeof(TradeBar), 100, false),
            new TestCaseData(_BitfinexBtcUsdSymbol, Resolution.Daily, typeof(QuoteBar), 100, false),
            // Has data
            new TestCaseData(_BitfinexBtcUsdSymbol, Resolution.Minute, typeof(TradeBar), 216, true),
            new TestCaseData(_CoinbaseBtcUsdSymbol, Resolution.Minute, typeof(TradeBar), 342, true),
            new TestCaseData(_CoinbaseBtcUsdSymbol, Resolution.Hour, typeof(TradeBar), 107, true),
            new TestCaseData(_CoinbaseBtcUsdSymbol, Resolution.Daily, typeof(TradeBar), 489, true),
            // Can get data for resolution second
            new TestCaseData(_BitfinexBtcUsdSymbol, Resolution.Second, typeof(TradeBar), 300, true)
        };

        [Test]
        [TestCaseSource(nameof(TestData))]
        public void CanGetHistory(Symbol symbol, Resolution resolution, Type dataType, int period, bool isNotNullResult)
        {
            _coinApiDataQueueHandler.SetUpHistDataLimit(100);

            var nowUtc = DateTime.UtcNow;
            var periodTimeSpan = TimeSpan.FromTicks(resolution.ToTimeSpan().Ticks * period);
            var startTimeUtc = nowUtc.Add(-periodTimeSpan);

            var historyRequests = new[]
            {
                new HistoryRequest(startTimeUtc, nowUtc, dataType, symbol, resolution,
                    SecurityExchangeHours.AlwaysOpen(TimeZones.Utc), TimeZones.Utc,
                    resolution, true, false, DataNormalizationMode.Raw, TickType.Trade)
            };

            var slices = _coinApiDataQueueHandler.GetHistory(historyRequests, TimeZones.Utc)?.ToArray();

            if (isNotNullResult)
            {
                Assert.IsNotNull(slices);
                // For resolution larger than second do more tests
                if (resolution > Resolution.Second)
                {
                    Assert.That(slices.Length, Is.EqualTo(period));

                    var firstSliceTradeBars = slices.First().Bars.Values;

                    Assert.True(firstSliceTradeBars.Select(x => x.Symbol).Contains(symbol));

                    firstSliceTradeBars.DoForEach(tb =>
                    {
                        var resTimeSpan = resolution.ToTimeSpan();
                        Assert.That(tb.Period, Is.EqualTo(resTimeSpan));
                        Assert.That(tb.Time, Is.EqualTo(startTimeUtc.RoundUp(resTimeSpan)));
                    });

                    var lastSliceTradeBars = slices.Last().Bars.Values;

                    lastSliceTradeBars.DoForEach(tb =>
                    {
                        var resTimeSpan = resolution.ToTimeSpan();
                        Assert.That(tb.Period, Is.EqualTo(resTimeSpan));
                        Assert.That(tb.Time, Is.EqualTo(nowUtc.RoundDown(resTimeSpan)));
                    });
                }
                // For res. second data counts, start/end dates may slightly vary from historical request's 
                // Make sure just that resolution is correct and amount is positive numb.
                else
                {
                    Assert.IsTrue(slices.Length > 0);
                    Assert.That(slices.First().Bars.Values.FirstOrDefault()?.Period, Is.EqualTo(resolution.ToTimeSpan()));
                }

                // Slices are ordered by time
                Assert.That(slices, Is.Ordered.By("Time"));
            }
            else
            {
                Assert.IsNull(slices);
            }
        }

        public class CoinApiDataQueueHandlerMock : CoinApiDataProvider
        {
            public new void SetUpHistDataLimit(int limit)
            {
                base.SetUpHistDataLimit(limit);
            }
        }

    }
}
