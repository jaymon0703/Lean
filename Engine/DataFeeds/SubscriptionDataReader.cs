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
 *
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Auxiliary;
using QuantConnect.Data.Custom;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// Subscription data reader is a wrapper on the stream reader class to download, unpack and iterate over a data file.
    /// </summary>
    /// <remarks>The class accepts any subscription configuration and automatically makes it availble to enumerate</remarks>
    public class SubscriptionDataReader : IEnumerator<BaseData>
    {
        // Source string to create memory stream:
        private SubscriptionDataSource _source;

        private bool _endOfStream;

        private IEnumerator<BaseData> _subscriptionFactoryEnumerator;

        /// Configuration of the data-reader:
        private readonly SubscriptionDataConfig _config;

        /// true if we can find a scale factor file for the security of the form: ..\Lean\Data\equity\market\factor_files\{SYMBOL}.csv
        private readonly bool _hasScaleFactors;

        // Symbol Mapping:
        private string _mappedSymbol = "";

        // Location of the datafeed - the type of this data.

        // Create a single instance to invoke all Type Methods:
        private readonly BaseData _dataFactory;

        //Start finish times of the backtest:
        private readonly DateTime _periodStart;
        private readonly DateTime _periodFinish;

        private readonly FactorFile _factorFile;
        private readonly MapFile _mapFile;

        // we set the price factor ratio when we encounter a dividend in the factor file
        // and on the next trading day we use this data to produce the dividend instance
        private decimal? _priceFactorRatio;

        // we set the split factor when we encounter a split in the factor file
        // and on the next trading day we use this data to produce the split instance
        private decimal? _splitFactor;

        // we'll use these flags to denote we've already fired off the DelistedType.Warning
        // and a DelistedType.Delisted Delisting object, the _delistingType object is save here
        // since we need to wait for the next trading day before emitting
        private bool _delisted;
        private bool _delistedWarning;

        // true if we're in live mode, false otherwise
        private readonly bool _isLiveMode;
        private readonly bool _includeAuxilliaryData;

        private BaseData _previous;
        private readonly Queue<BaseData> _auxiliaryData;
        private readonly IResultHandler _resultHandler;
        private readonly IEnumerator<DateTime> _tradeableDates;

        // used when emitting aux data from within while loop
        private bool _emittedAuxilliaryData;
        private BaseData _lastInstanceBeforeAuxilliaryData;

        /// <summary>
        /// Last read BaseData object from this type and source
        /// </summary>
        public BaseData Current
        {
            get;
            private set;
        }

        /// <summary>
        /// Explicit Interface Implementation for Current
        /// </summary>
        object IEnumerator.Current
        {
            get { return Current; }
        }

        /// <summary>
        /// Subscription data reader takes a subscription request, loads the type, accepts the data source and enumerate on the results.
        /// </summary>
        /// <param name="config">Subscription configuration object</param>
        /// <param name="periodStart">Start date for the data request/backtest</param>
        /// <param name="periodFinish">Finish date for the data request/backtest</param>
        /// <param name="resultHandler">Result handler used to push error messages and perform sampling on skipped days</param>
        /// <param name="tradeableDates">Defines the dates for which we'll request data, in order</param>
        /// <param name="isLiveMode">True if we're in live mode, false otherwise</param>
        /// <param name="includeAuxilliaryData">True if we want to emit aux data, false to only emit price data</param>
        public SubscriptionDataReader(SubscriptionDataConfig config,
            DateTime periodStart,
            DateTime periodFinish,
            IResultHandler resultHandler,
            IEnumerable<DateTime> tradeableDates,
            bool isLiveMode,
            bool includeAuxilliaryData = true
            )
        {
            //Save configuration of data-subscription:
            _config = config;

            _auxiliaryData = new Queue<BaseData>();

            //Save Start and End Dates:
            _periodStart = periodStart;
            _periodFinish = periodFinish;

            //Save access to securities
            _isLiveMode = isLiveMode;
            _includeAuxilliaryData = includeAuxilliaryData;

            //Save the type of data we'll be getting from the source.

            //Create the dynamic type-activators:
            var objectActivator = ObjectActivator.GetActivator(config.Type);

            _resultHandler = resultHandler;
            _tradeableDates = tradeableDates.GetEnumerator();
            if (objectActivator == null)
            {
                _resultHandler.ErrorMessage("Custom data type '" + config.Type.Name + "' missing parameterless constructor E.g. public " + config.Type.Name + "() { }");
                _endOfStream = true;
                return;
            }

            //Create an instance of the "Type":
            var userObj = objectActivator.Invoke(new object[] { });
            _dataFactory = userObj as BaseData;

            //If its quandl set the access token in data factory:
            var quandl = _dataFactory as Quandl;
            if (quandl != null)
            {
                if (!Quandl.IsAuthCodeSet)
                {
                    Quandl.SetAuthCode(Config.Get("quandl-auth-token"));
                }
            }

            //Load the entire factor and symbol mapping tables into memory, we'll start with some defaults
            _factorFile = new FactorFile(config.Symbol.Permtick, new List<FactorFileRow>());
            _mapFile = new MapFile(config.Symbol.Permtick, new List<MapFileRow>());
            try
            {
                // do we have map/factor tables? -- only applies to equities
                if (!_config.IsCustomData && _config.SecurityType == SecurityType.Equity)
                {
                    // resolve the correct map file as of the date
                    _mapFile = MapFile.Read(config.Symbol.Permtick, config.Market);
                    _hasScaleFactors = FactorFile.HasScalingFactors(_mapFile.Permtick, config.Market);
                    if (_hasScaleFactors)
                    {
                        _factorFile = FactorFile.Read(config.Symbol.Permtick, config.Market);
                    }
                }
            }
            catch (Exception err)
            {
                Log.Error("SubscriptionDataReader(): Fetching Price/Map Factors: " + err.Message);
            }

            _subscriptionFactoryEnumerator = ResolveDataEnumerator(true);
        }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns>
        /// true if the enumerator was successfully advanced to the next element; false if the enumerator has passed the end of the collection.
        /// </returns>
        /// <exception cref="T:System.InvalidOperationException">The collection was modified after the enumerator was created. </exception><filterpriority>2</filterpriority>
        public bool MoveNext()
        {
            if (_endOfStream)
            {
                return false;
            }

            if (Current != null && Current.DataType != MarketDataType.Auxiliary)
            {
                // only save previous price data
                _previous = Current;
            }

            if (_subscriptionFactoryEnumerator == null)
            {
                // in live mode the trade able dates will eventually advance to the next
                if (_isLiveMode)
                {
                    // HACK attack -- we don't want to block in live mode
                    Current = null;
                    return true;
                }

                _endOfStream = true;
                return false;
            }

            do
            {
                // check for aux data first
                if (HasAuxDataBefore(_lastInstanceBeforeAuxilliaryData))
                {
                    // check for any auxilliary data before reading a line, but make sure
                    // it should be going ahead of '_lastInstanceBeforeAuxilliaryData'
                    Current = _auxiliaryData.Dequeue();
                    return true;
                }

                if (_emittedAuxilliaryData)
                {
                    _emittedAuxilliaryData = false;
                    Current = _lastInstanceBeforeAuxilliaryData;
                    _lastInstanceBeforeAuxilliaryData = null;
                    return true;
                }

                // keep enumerating until we find something that is within our time frame
                while (_subscriptionFactoryEnumerator.MoveNext())
                {
                    var instance = _subscriptionFactoryEnumerator.Current;
                    if (instance == null)
                    {
                        // keep reading until we get valid data
                        continue;
                    }

                    if (instance.EndTime < _periodStart)
                    {
                        // keep reading until we get a value on or after the start
                        _previous = instance;
                        continue;
                    }

                    if (instance.Time > _periodFinish)
                    {
                        // stop reading when we get a value after the end
                        _endOfStream = true;
                        return false;
                    }

                    // if we move past our current 'date' then we need to do daily things, such
                    // as updating factors and symbol mapping as well as detecting aux data
                    if (instance.EndTime.Date > _tradeableDates.Current)
                    {
                        // this is fairly hacky and could be solved by removing the aux data from this class
                        // the case is with coarse data files which have many daily sized data points for the
                        // same date,
                        if (!_config.IsInternalFeed)
                        {
                            // this will advance the date enumerator and determine if a new
                            // instance of the subscription enumerator is required
                            _subscriptionFactoryEnumerator = ResolveDataEnumerator(false);
                        }

                        // we produce auxiliary data on date changes, but make sure our current instance
                        // isn't before it in time
                        if (HasAuxDataBefore(instance))
                        {
                            // since we're emitting this here we need to save off the instance for next time
                            Current = _auxiliaryData.Dequeue();
                            _emittedAuxilliaryData = true;
                            _lastInstanceBeforeAuxilliaryData = instance;
                            return true;
                        }
                    }

                    // we've made it past all of our filters, we're withing the requested start/end of the subscription,
                    // we've satisfied user and market hour filters, so this data is good to go as current
                    Current = instance;
                    return true;
                }

                // we've ended the enumerator, time to refresh
                _subscriptionFactoryEnumerator = ResolveDataEnumerator(true);
            }
            while (_subscriptionFactoryEnumerator != null);

            _endOfStream = true;
            return false;
        }

        private bool HasAuxDataBefore(BaseData instance)
        {
            // this function is always used to check for aux data, as such, we'll implement the
            // feature of whether to include or not here so if other aux data is added we won't
            // need to remember this feature. this is mostly here until aux data gets moved into
            // its own subscription class
            if (!_includeAuxilliaryData) _auxiliaryData.Clear();
            if (_auxiliaryData.Count == 0) return false;
            if (instance == null) return true;
            return _auxiliaryData.Peek().EndTime < instance.EndTime;
        }

        /// <summary>
        /// Resolves the next enumerator to be used in <see cref="MoveNext"/>
        /// </summary>
        private IEnumerator<BaseData> ResolveDataEnumerator(bool endOfEnumerator)
        {
            do
            {
                // always advance the date enumerator, this function is intended to be
                // called on date changes, never return null for live mode, we'll always
                // just keep trying to refresh the subscription
                DateTime date;
                if (!TryGetNextDate(out date) && !_isLiveMode)
                {
                    // if we run out of dates then we're finished with this subscription
                    return null;
                }

                // fetch the new source
                var newSource = _dataFactory.GetSource(_config, date, _isLiveMode);

                // check if we should create a new subscription factory
                var sourceChanged = _source != newSource && newSource.Source != "";
                var liveRemoteFile = _isLiveMode && (_source == null || _source.TransportMedium == SubscriptionTransportMedium.RemoteFile);
                if (sourceChanged || liveRemoteFile)
                {
                    // dispose of the current enumerator before creating a new one
                    if (_subscriptionFactoryEnumerator != null)
                    {
                        _subscriptionFactoryEnumerator.Dispose();
                    }

                    // save off for comparison next time
                    _source = newSource;
                    var subscriptionFactory = CreateSubscriptionFactory(newSource);

                    var previousTime = _previous == null ? DateTime.MinValue : _previous.EndTime;
                    return subscriptionFactory.Read(newSource)
                        // prevent the enumerator from emitting data before the last emitted time
                        .Where(instance => instance != null && previousTime < instance.EndTime)
                        .GetEnumerator();
                }

                // if there's still more in the enumerator and we received the same source from the GetSource call
                // above, then just keep using the same enumerator as we were before
                if (!endOfEnumerator) // && !sourceChanged is always true here
                {
                    return _subscriptionFactoryEnumerator;
                }

                // keep churning until we find a new source or run out of tradeable dates
                // in live mode tradeable dates won't advance beyond today's date, but
                // TryGetNextDate will return false if it's already at today
            }
            while (true);
        }

        private ISubscriptionFactory CreateSubscriptionFactory(SubscriptionDataSource source)
        {
            switch (source.Format)
            {
                case FileFormat.Csv:
                    return HandleCsvFileFormat(source);

                case FileFormat.Binary:
                    throw new NotSupportedException("Binary file format is not supported");

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private ISubscriptionFactory HandleCsvFileFormat(SubscriptionDataSource source)
        {
            var factory = new BaseDataSubscriptionFactory(_config, _tradeableDates.Current, _isLiveMode);

            // handle missing files
            factory.InvalidSource += (sender, args) =>
            {
                switch (args.Source.TransportMedium)
                {
                    case SubscriptionTransportMedium.LocalFile:
                        // the local uri doesn't exist, write an error and return null so we we don't try to get data for today
                        Log.Trace(string.Format("SubscriptionDataReader.GetReader(): Could not find QC Data, skipped: {0}", source));
                        _resultHandler.SamplePerformance(_tradeableDates.Current, 0);
                        break;

                    case SubscriptionTransportMedium.RemoteFile:
                        _resultHandler.ErrorMessage(string.Format("Error downloading custom data source file, skipped: {0} Error: {1}", source, args.Exception.Message), args.Exception.StackTrace);
                        _resultHandler.SamplePerformance(_tradeableDates.Current.Date, 0);
                        break;

                    case SubscriptionTransportMedium.Rest:
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }
            };

            // handle empty files/instantiation errors
            factory.CreateStreamReaderError += (sender, args) =>
            {
                Log.Error(string.Format("Failed to get StreamReader for data source({0}), symbol({1}). Skipping date({2}). Reader is null.", args.Source.Source, _mappedSymbol, args.Date.ToShortDateString()));
                if (_config.IsCustomData)
                {
                    _resultHandler.ErrorMessage(string.Format("We could not fetch the requested data. This may not be valid data, or a failed download of custom data. Skipping source ({0}).", args.Source.Source));
                }
            };

            // handle parser errors
            factory.ReaderError += (sender, args) =>
            {
                _resultHandler.RuntimeError(string.Format("Error invoking {0} data reader. Line: {1} Error: {2}", _config.Symbol, args.Line, args.Exception.Message), args.Exception.StackTrace);
            };
            return factory;
        }

        /// <summary>
        /// Iterates the tradeable dates enumerator
        /// </summary>
        /// <param name="date">The next tradeable date</param>
        /// <returns>True if we got a new date from the enumerator, false if it's exhausted, or in live mode if we're already at today</returns>
        private bool TryGetNextDate(out DateTime date)
        {
            if (_isLiveMode && _tradeableDates.Current >= DateTime.Today)
            {
                // special behavior for live mode, don't advance past today
                date = _tradeableDates.Current;
                return false;
            }

            while (_tradeableDates.MoveNext())
            {
                date = _tradeableDates.Current;

                CheckForDelisting(date);

                if (!_mapFile.HasData(date))
                {
                    continue;
                }

                // don't do other checks if we haven't goten data for this date yet
                if (_previous != null && _previous.EndTime > _tradeableDates.Current)
                {
                    continue;
                }

                // check for dividends and split for this security
                CheckForDividend(date);
                CheckForSplit(date);

                // if we have factor files check to see if we need to update the scale factors
                if (_hasScaleFactors)
                {
                    // check to see if the symbol was remapped
                    var newSymbol = _mapFile.GetMappedSymbol(date);
                    if (_mappedSymbol != "" && newSymbol != _mappedSymbol)
                    {
                        var changed = new SymbolChangedEvent(_config.Symbol, date, _mappedSymbol, newSymbol);
                        _auxiliaryData.Enqueue(changed);
                    }
                    _config.MappedSymbol = _mappedSymbol = newSymbol;

                    // update our price scaling factors in light of the normalization mode
                    UpdateScaleFactors(date);
                }

                // we've passed initial checks,now go get data for this date!
                return true;
            }

            // no more tradeable dates, we've exhausted the enumerator
            date = DateTime.MaxValue.Date;
            return false;
        }

        /// <summary>
        /// For backwards adjusted data the price is adjusted by a scale factor which is a combination of splits and dividends. 
        /// This backwards adjusted price is used by default and fed as the current price.
        /// </summary>
        /// <param name="date">Current date of the backtest.</param>
        private void UpdateScaleFactors(DateTime date)
        {
            switch (_config.DataNormalizationMode)
            {
                case DataNormalizationMode.Raw:
                    return;

                case DataNormalizationMode.TotalReturn:
                case DataNormalizationMode.SplitAdjusted:
                    _config.PriceScaleFactor = _factorFile.GetSplitFactor(date);
                    break;

                case DataNormalizationMode.Adjusted:
                    _config.PriceScaleFactor = _factorFile.GetPriceScaleFactor(date);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Reset the IEnumeration
        /// </summary>
        /// <remarks>Not used</remarks>
        public void Reset()
        {
            throw new NotImplementedException("Reset method not implemented. Assumes loop will only be used once.");
        }

        /// <summary>
        /// Check for dividends and emit them into the aux data queue
        /// </summary>
        private void CheckForSplit(DateTime date)
        {
            if (_splitFactor != null)
            {
                var close = GetRawClose();
                var split = new Split(_config.Symbol, date, close, _splitFactor.Value);
                _auxiliaryData.Enqueue(split);
                _splitFactor = null;
            }

            decimal splitFactor;
            if (_factorFile.HasSplitEventOnNextTradingDay(date, out splitFactor))
            {
                _splitFactor = splitFactor;
            }
        }

        /// <summary>
        /// Check for dividends and emit them into the aux data queue
        /// </summary>
        private void CheckForDividend(DateTime date)
        {
            if (_priceFactorRatio != null)
            {
                var close = GetRawClose();
                var dividend = new Dividend(_config.Symbol, date, close, _priceFactorRatio.Value);
                // let the config know about it for normalization
                _config.SumOfDividends += dividend.Distribution;
                _auxiliaryData.Enqueue(dividend);
                _priceFactorRatio = null;
            }

            // check the factor file to see if we have a dividend event tomorrow
            decimal priceFactorRatio;
            if (_factorFile.HasDividendEventOnNextTradingDay(date, out priceFactorRatio))
            {
                _priceFactorRatio = priceFactorRatio;
            }
        }

        /// <summary>
        /// Check for delistings and emit them into the aux data queue
        /// </summary>
        private void CheckForDelisting(DateTime date)
        {
            // these ifs set flags to tell us to produce a delisting instance
            if (!_delistedWarning && date >= _mapFile.DelistingDate)
            {
                _delistedWarning = true;
                var price = _previous != null ? _previous.Price : 0;
                _auxiliaryData.Enqueue(new Delisting(_config.Symbol, date, price, DelistingType.Warning));
            }
            else if (!_delisted && date > _mapFile.DelistingDate)
            {
                _delisted = true;
                var price = _previous != null ? _previous.Price : 0;
                // delisted at EOD
                _auxiliaryData.Enqueue(new Delisting(_config.Symbol, _mapFile.DelistingDate.AddDays(1), price, DelistingType.Delisted));
            }
        }

        /// <summary>
        /// Un-normalizes the Previous.Value
        /// </summary>
        private decimal GetRawClose()
        {
            if (_previous == null) return 0m;

            var close = _previous.Value;

            switch (_config.DataNormalizationMode)
            {
                case DataNormalizationMode.Raw:
                    break;

                case DataNormalizationMode.SplitAdjusted:
                case DataNormalizationMode.Adjusted:
                    // we need to 'unscale' the price
                    close = close / _config.PriceScaleFactor;
                    break;

                case DataNormalizationMode.TotalReturn:
                    // we need to remove the dividends since we've been accumulating them in the price
                    close = (close - _config.SumOfDividends) / _config.PriceScaleFactor;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
            return close;
        }

        /// <summary>
        /// Dispose of the Stream Reader and close out the source stream and file connections.
        /// </summary>
        public void Dispose()
        {
            if (_subscriptionFactoryEnumerator != null)
            {
                _subscriptionFactoryEnumerator.Dispose();
            }
        }
    }
}