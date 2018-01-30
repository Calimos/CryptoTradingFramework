﻿using CryptoMarketClient.Common;
using CryptoMarketClient.Poloniex;
using DevExpress.XtraEditors;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reactive.Subjects;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WampSharp.Binding;
using WampSharp.V2;
using WampSharp.V2.Rpc;

namespace CryptoMarketClient {
    public class PoloniexExchange : Exchange {
        const string PoloniexServerAddress = "wss://api.poloniex.com";

        static PoloniexExchange defaultExchange;
        public static PoloniexExchange Default {
            get {
                if(defaultExchange == null) {
                    defaultExchange = new PoloniexExchange();
                    defaultExchange.Load();
                }
                return defaultExchange;
            }
        }

        public override string Name => "Poloniex";
        public override List<CandleStickIntervalInfo> GetAllowedCandleStickIntervals() {
            List<CandleStickIntervalInfo> list = new List<CandleStickIntervalInfo>();
            list.Add(new CandleStickIntervalInfo() { Text = "5 Minutes", Interval = TimeSpan.FromSeconds(300) });
            list.Add(new CandleStickIntervalInfo() { Text = "15 Minutes", Interval = TimeSpan.FromSeconds(300) });
            list.Add(new CandleStickIntervalInfo() { Text = "30 Minutes", Interval = TimeSpan.FromSeconds(1800) });
            list.Add(new CandleStickIntervalInfo() { Text = "2 Hours", Interval = TimeSpan.FromSeconds(7200) });
            list.Add(new CandleStickIntervalInfo() { Text = "4 Hours", Interval = TimeSpan.FromSeconds(14400) });
            list.Add(new CandleStickIntervalInfo() { Text = "1 Day", Interval = TimeSpan.FromSeconds(86400) });

            return list;
        }

        public List<PoloniexCurrencyInfo> Currencies { get; } = new List<PoloniexCurrencyInfo>();

        protected IDisposable TickersSubscriber { get; set; }
        public void Connect() {
            if(TickersSubscriber != null)
                return;
        }

        public event TickerUpdateEventHandler TickerUpdate;
        protected void RaiseTickerUpdate(PoloniexTicker t) {
            TickerUpdateEventArgs e = new TickerUpdateEventArgs() { Ticker = t };
            if(TickerUpdate != null)
                TickerUpdate(this, e);
            t.RaiseChanged();
        }
        public IDisposable ConnectOrderBook(PoloniexTicker ticker) {
            return null;
        }
        string ByteArray2String(byte[] bytes, int index, int length) {
            unsafe
            {
                fixed (byte* bytes2 = &bytes[0]) {
                    if(bytes[index] == '"')
                        return new string((sbyte*)bytes2, index + 1, length - 2);
                    return new string((sbyte*)bytes2, index, length);
                }
            }
        }
        List<string[]> DeserializeArrayOfObjects(byte[] bytes, ref int startIndex, string[] str) {
            return DeserializeArrayOfObjects(bytes, ref startIndex, str, null);
        }
        List<string[]> DeserializeArrayOfObjects(byte[] bytes, ref int startIndex, string[] str, IfDelegate2 shouldContinue) {
            int index = startIndex;
            if(!FindChar(bytes, '[', ref index))
                return null;
            List<string[]> items = new List<string[]>();
            while(index != -1) {
                if(!FindChar(bytes, '{', ref index))
                    break;
                string[] props = new string[str.Length];
                for(int itemIndex = 0; itemIndex < str.Length; itemIndex++) {
                    if(bytes[index + 1 + 2 + str[itemIndex].Length] == ':')
                        index = index + 1 + 2 + str[itemIndex].Length + 1;
                    else {
                        if(!FindChar(bytes, ':', ref index)) {
                            startIndex = index;
                            return items;
                        }
                    }
                    int length = index;
                    if(bytes[index] == '"')
                        ReadString(bytes, ref length);
                    else
                        FindChar(bytes, itemIndex == str.Length - 1 ? '}' : ',', ref length);
                    length -= index;
                    props[itemIndex] = ByteArray2String(bytes, index, length);
                    index += length;
                    if(shouldContinue != null && !shouldContinue(itemIndex, props[itemIndex]))
                        return items;
                }
                items.Add(props);
                if(index == -1)
                    break;
                index += 2; // skip ,
            }
            startIndex = index;
            return items;
        }
        List<string[]> DeserializeArrayOfArrays(byte[] bytes, ref int startIndex, int subArrayItemsCount) {
            int index = startIndex;
            if(!FindChar(bytes, '[', ref index))
                return null;
            List<string[]> list = new List<string[]>();
            index++;
            while(index != -1) {
                if(!FindChar(bytes, '[', ref index))
                    break;
                string[] items = new string[subArrayItemsCount];
                list.Add(items);
                for(int i = 0; i < subArrayItemsCount; i++) {
                    index++;
                    int length = index;
                    char separator = i == subArrayItemsCount - 1 ? ']' : ',';
                    FindChar(bytes, separator, ref length);
                    length -= index;
                    items[i] = ByteArray2String(bytes, index, length);
                    index += length;
                }
                index += 2; // skip ],
            }
            startIndex = index;
            return list; 
        }
        bool ReadString(byte[] bytes, ref int startIndex) {
            startIndex++;
            for(int i = startIndex; i < bytes.Length; i++) {
                if(bytes[i] == '"') {
                    startIndex = i + 1;
                    return true;
                }
            }
            startIndex = bytes.Length;
            return false;
        }
        bool FindCharWithoutStop(byte[] bytes, char symbol, ref int startIndex) {
            for(int i = startIndex; i < bytes.Length; i++) {
                byte c = bytes[i];
                if(c == symbol) {
                    startIndex = i;
                    return true;
                }
            }
            startIndex = bytes.Length;
            return false;
        }
        bool FindChar(byte[] bytes, char symbol, ref int startIndex) {
            for(int i = startIndex; i < bytes.Length; i++) {
                byte c = bytes[i];
                if(c == symbol) {
                    startIndex = i;
                    return true;
                }
                if(c == ',' || c == ']' || c == '}' || c == ':') {
                    startIndex = i;
                    return false;
                }
            }
            startIndex = bytes.Length;
            return false;
        }
        public override BindingList<CandleStickData> GetCandleStickData(TickerBase ticker, int candleStickPeriodMin, DateTime start, long periodInSeconds) {
            long startSec = (long)(start.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
            long end = startSec + periodInSeconds;

            string address = string.Format("https://poloniex.com/public?command=returnChartData&currencyPair={0}&period={1}&start={2}&end={3}",
                Uri.EscapeDataString(ticker.CurrencyPair), candleStickPeriodMin * 60, startSec, end);
            byte[] bytes = null;
            try {
                bytes = GetDownloadBytes(address);
            }
            catch(Exception) {
                return null;
            }
            if(bytes == null || bytes.Length == 0)
                return null;

            DateTime startTime = new DateTime(1970, 1, 1);

            BindingList<CandleStickData> list = new BindingList<CandleStickData>();
            int startIndex = 0;
            List<string[]> res = DeserializeArrayOfObjects(bytes, ref startIndex, new string[] { "date", "high", "low", "open", "close", "volume", "quoteVolume", "weightedAverage" }); 
            foreach(string[] item in res) {
                CandleStickData data = new CandleStickData();
                data.Time = startTime.AddSeconds(long.Parse(item[0]));
                data.High = FastDoubleConverter.Convert(item[1]);
                data.Low = FastDoubleConverter.Convert(item[2]);
                data.Open = FastDoubleConverter.Convert(item[3]);
                data.Close = FastDoubleConverter.Convert(item[4]);
                data.Volume = FastDoubleConverter.Convert(item[5]);
                data.QuoteVolume = FastDoubleConverter.Convert(item[6]);
                data.WeightedAverage = FastDoubleConverter.Convert(item[7]);
                list.Add(data);
            }

            //JArray res = (JArray)JsonConvert.DeserializeObject(text);
            //foreach(JObject item in res.Children()) {
            //    CandleStickData data = new CandleStickData();
            //    data.Time = startTime.AddSeconds(item.Value<long>("date"));
            //    data.Open = item.Value<double>("open");
            //    data.Close = item.Value<double>("close");
            //    data.High = item.Value<double>("high");
            //    data.Low = item.Value<double>("low");
            //    data.Volume = item.Value<double>("volume");
            //    data.QuoteVolume = item.Value<double>("quoteVolume");
            //    data.WeightedAverage = item.Value<double>("weightedAverage");
            //    list.Add(data);
            //}
            return list;
        }

        public override bool UpdateCurrencies() {
            string address = "https://poloniex.com/public?command=returnCurrencies";
            string text = string.Empty;
            try {
                text = GetDownloadString(address);
            }
            catch(Exception) {
                return false;
            }
            if(string.IsNullOrEmpty(text))
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            foreach(JProperty prop in res.Children()) {
                string currency = prop.Name;
                JObject obj = (JObject)prop.Value;
                PoloniexCurrencyInfo c = Currencies.FirstOrDefault(curr => curr.Currency == currency);
                if(c == null) {
                    c = new PoloniexCurrencyInfo();
                    c.Currency = currency;
                    c.MaxDailyWithdrawal = obj.Value<double>("maxDailyWithdrawal");
                    c.TxFee = obj.Value<double>("txFee");
                    c.MinConfirmation = obj.Value<double>("minConf");
                    Currencies.Add(c);
                }
                c.Disabled = obj.Value<int>("disabled") != 0;
            }
            return true;
        }

        public override bool GetTickersInfo() {
            string address = "https://poloniex.com/public?command=returnTicker";
            string text = string.Empty;
            try {
                text = GetDownloadString(address);
            }
            catch(Exception) {
                return false;
            }
            if(string.IsNullOrEmpty(text))
                return false;
            Tickers.Clear();
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            int index = 0;
            foreach(JProperty prop in res.Children()) {
                PoloniexTicker t = new PoloniexTicker(this);
                t.Index = index;
                t.CurrencyPair = prop.Name;
                JObject obj = (JObject)prop.Value;
                t.Id = obj.Value<int>("id");
                t.Last = obj.Value<double>("last");
                t.LowestAsk = obj.Value<double>("lowestAsk");
                t.HighestBid = obj.Value<double>("highestBid");
                //t.Change = obj.Value<double>("percentChange");
                t.BaseVolume = obj.Value<double>("baseVolume");
                t.Volume = obj.Value<double>("quoteVolume");
                t.IsFrozen = obj.Value<int>("isFrozen") != 0;
                t.Hr24High = obj.Value<double>("high24hr");
                t.Hr24Low = obj.Value<double>("low24hr");
                Tickers.Add(t);
                index++;
            }
            return true;
        }
        public override bool UpdateTickersInfo() {
            if(Tickers.Count == 0)
                return false;
            string address = "https://poloniex.com/public?command=returnTicker";
            string text = string.Empty;
            try {
                text = GetDownloadString(address);
            }
            catch(Exception) {
                return false;
            }
            if(string.IsNullOrEmpty(text))
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            foreach(JProperty prop in res.Children()) {
                PoloniexTicker t = (PoloniexTicker)Tickers.FirstOrDefault((i) => i.CurrencyPair == prop.Name);
                if(t == null)
                    continue;
                JObject obj = (JObject)prop.Value;
                t.Last = obj.Value<double>("last");
                t.LowestAsk = obj.Value<double>("lowestAsk");
                t.HighestBid = obj.Value<double>("highestBid");
                //t.Change = obj.Value<double>("percentChange");
                t.BaseVolume = obj.Value<double>("baseVolume");
                t.Volume = obj.Value<double>("quoteVolume");
                t.IsFrozen = obj.Value<int>("isFrozen") != 0;
                t.Hr24High = obj.Value<double>("high24hr");
                t.Hr24Low = obj.Value<double>("low24hr");
            }
            return true;
        }
        public bool UpdateArbitrageOrderBook(PoloniexTicker ticker, int depth) {
            string address = GetOrderBookString(ticker, depth);
            string text = ((TickerBase)ticker).DownloadString(address);
            return OnUpdateArbitrageOrderBook(ticker, text);
        }
        public override bool UpdateTicker(TickerBase tickerBase) {
            return true;
        }
        public bool OnUpdateOrderBook(TickerBase ticker, byte[] bytes) {
            if(bytes == null)
                return false;

            int startIndex = 1; // skip {
            if(!FindChar(bytes, ':', ref startIndex))
                return false;
            startIndex++;
            List<string[]> asks = DeserializeArrayOfArrays(bytes, ref startIndex, 2);
            if(!FindChar(bytes, ',', ref startIndex))
                return false;
            startIndex++;
            if(!FindChar(bytes, ':', ref startIndex))
                return false;
            startIndex++;
            List<string[]> bids = DeserializeArrayOfArrays(bytes, ref startIndex, 2);

            ticker.OrderBook.GetNewBidAsks();
            int index = 0;
            OrderBookEntry[] list = ticker.OrderBook.Bids;
            foreach(string[] item in bids) {
                OrderBookEntry entry = list[index];
                entry.ValueString = item[0];
                entry.AmountString = item[1];
                index++;
                if(index >= list.Length)
                    break;
            }
            index = 0;
            list = ticker.OrderBook.Asks;
            foreach(string[] item in asks) {
                OrderBookEntry entry = list[index];
                entry.ValueString = item[0];
                entry.AmountString = item[1];
                index++;
                if(index >= list.Length)
                    break;
            }

            ticker.OrderBook.UpdateEntries();
            ticker.OrderBook.RaiseOnChanged(new OrderBookUpdateInfo() { Action = OrderBookUpdateType.RefreshAll });
            return true;
        }
        public bool OnUpdateArbitrageOrderBook(TickerBase ticker, string text) {
            if(string.IsNullOrEmpty(text))
                return false;

            Dictionary<string, object> res2 = null;
            lock(JsonParser) {
                res2 = JsonParser.Parse(text) as Dictionary<string, object>;
                if(res2 == null)
                    return false;
            }

            List<object> bids = (List<object>)res2["bids"];
            List<object> asks = (List<object>)res2["asks"];

            ticker.OrderBook.GetNewBidAsks();
            int index = 0;
            OrderBookEntry[] list = ticker.OrderBook.Bids;
            foreach(List<object> item in bids) {
                OrderBookEntry entry = list[index];
                entry.ValueString = (string)item.First();
                entry.AmountString = (string)item.Last();
                index++;
                if(index >= list.Length)
                    break;
            }
            index = 0;
            list = ticker.OrderBook.Asks;
            foreach(List<object> item in asks) {
                OrderBookEntry entry = list[index];
                entry.ValueString = (string)item.First();
                entry.AmountString = (string)item.Last();
                index++;
                if(index >= list.Length)
                    break;
            }

            ticker.OrderBook.UpdateEntries();
            ticker.OrderBook.RaiseOnChanged(new OrderBookUpdateInfo() { Action = OrderBookUpdateType.RefreshAll });
            return true;
        }

        public string GetOrderBookString(TickerBase ticker, int depth) {
            return string.Format("https://poloniex.com/public?command=returnOrderBook&currencyPair={0}&depth={1}",
                Uri.EscapeDataString(ticker.CurrencyPair), depth);
        }
        public override bool UpdateOrderBook(TickerBase ticker) {
            return GetOrderBook(ticker, OrderBook.Depth);
        }
        public override bool ProcessOrderBook(TickerBase tickerBase, string text) {
            UpdateOrderBook(tickerBase, text);
            return true;
        }
        public void UpdateOrderBook(TickerBase ticker, string text) {
            OnUpdateArbitrageOrderBook(ticker, text);
            ticker.OrderBook.RaiseOnChanged(new OrderBookUpdateInfo() { Action = OrderBookUpdateType.RefreshAll });
        }
        public bool GetOrderBook(TickerBase ticker, int depth) {
            string address = string.Format("https://poloniex.com/public?command=returnOrderBook&currencyPair={0}&depth={1}",
                Uri.EscapeDataString(ticker.CurrencyPair), depth);
            byte[] bytes = ((TickerBase)ticker).DownloadBytes(address);
            if(bytes == null || bytes.Length == 0)
                return false;
            OnUpdateOrderBook(ticker, bytes);
            return true;
        }

        public override List<TradeHistoryItem> GetTrades(TickerBase ticker, DateTime starTime) {
            string address = string.Format("https://poloniex.com/public?command=returnTradeHistory&currencyPair={0}", Uri.EscapeDataString(ticker.CurrencyPair));
            string text = GetDownloadString(address);
            if(string.IsNullOrEmpty(text))
                return null;
            JArray trades = (JArray)JsonConvert.DeserializeObject(text);
            if(trades.Count == 0)
                return null;

            List<TradeHistoryItem> list = new List<TradeHistoryItem>();

            int index = 0;
            foreach(JObject obj in trades) {
                DateTime time = obj.Value<DateTime>("date");
                int tradeId = obj.Value<int>("tradeID");
                if(time < starTime)
                    break;

                TradeHistoryItem item = new TradeHistoryItem();
                bool isBuy = obj.Value<string>("type").Length == 3;
                item.AmountString = obj.Value<string>("amount");
                item.Time = time;
                item.Type = isBuy ? TradeType.Buy : TradeType.Sell;
                item.RateString = obj.Value<string>("rate");
                item.Id = tradeId;
                double price = item.Rate;
                double amount = item.Amount;
                item.Total = price * amount;
                list.Insert(index, item);
                index++;
            }
            return list;
        }

        protected List<TradeHistoryItem> UpdateList { get; } = new List<TradeHistoryItem>(100);
        //public override bool UpdateTrades(TickerBase ticker) {
        //    string address = string.Format("https://poloniex.com/public?command=returnTradeHistory&currencyPair={0}", Uri.EscapeDataString(ticker.CurrencyPair));
        //    string text = GetDownloadString(address);
        //    if(string.IsNullOrEmpty(text))
        //        return true;
        //    JArray trades = (JArray)JsonConvert.DeserializeObject(text);
        //    if(trades.Count == 0)
        //        return true;

        //    int lastTradeId = trades.First().Value<int>("tradeID");
        //    long lastGotTradeId = ticker.TradeHistory.Count > 0 ? ticker.TradeHistory.First().Id : 0;
        //    if(lastGotTradeId == lastTradeId) {
        //        ticker.TradeStatistic.Add(new TradeStatisticsItem() { Time = DateTime.UtcNow });
        //        if(ticker.TradeStatistic.Count > 5000) {
        //            for(int i = 0; i < 100; i++)
        //                ticker.TradeStatistic.RemoveAt(0);
        //        }
        //        return true;
        //    }
        //    TradeStatisticsItem st = new TradeStatisticsItem();
        //    st.MinBuyPrice = double.MaxValue;
        //    st.MinSellPrice = double.MaxValue;
        //    st.Time = DateTime.UtcNow;

        //    int index = 0;
        //    foreach(JObject obj in trades) {
        //        DateTime time = obj.Value<DateTime>("date");
        //        int tradeId = obj.Value<int>("tradeID");
        //        if(lastGotTradeId == tradeId)
        //            break;

        //        TradeHistoryItem item = new TradeHistoryItem();

        //        bool isBuy = obj.Value<string>("type").Length == 3;
        //        item.AmountString = obj.Value<string>("amount");
        //        item.Time = time;
        //        item.Type = isBuy ? TradeType.Buy : TradeType.Sell;
        //        item.RateString = obj.Value<string>("rate");
        //        item.Id = tradeId;
        //        double price = item.Rate;
        //        double amount = item.Amount;
        //        item.Total = price * amount;
        //        if(isBuy) {
        //            st.BuyAmount += amount;
        //            st.MinBuyPrice = Math.Min(st.MinBuyPrice, price);
        //            st.MaxBuyPrice = Math.Max(st.MaxBuyPrice, price);
        //            st.BuyVolume += amount * price;
        //        }
        //        else {
        //            st.SellAmount += amount;
        //            st.MinSellPrice = Math.Min(st.MinSellPrice, price);
        //            st.MaxSellPrice = Math.Max(st.MaxSellPrice, price);
        //            st.SellVolume += amount * price;
        //        }
        //        ticker.TradeHistory.Insert(index, item);
        //        index++;
        //    }
        //    if(st.MinSellPrice == double.MaxValue)
        //        st.MinSellPrice = 0;
        //    if(st.MinBuyPrice == double.MaxValue)
        //        st.MinBuyPrice = 0;
        //    ticker.LastTradeStatisticTime = DateTime.UtcNow;
        //    ticker.TradeStatistic.Add(st);
        //    if(ticker.TradeStatistic.Count > 5000) {
        //        for(int i = 0; i < 100; i++)
        //            ticker.TradeStatistic.RemoveAt(0);
        //    }
        //    return true;
        //}

        public override bool UpdateTrades(TickerBase ticker) {
            string address = string.Format("https://poloniex.com/public?command=returnTradeHistory&currencyPair={0}", Uri.EscapeDataString(ticker.CurrencyPair));
            byte[] bytes = GetDownloadBytes(address);
            if(bytes == null)
                return true;

            string lastGotTradeId = ticker.TradeHistory.Count > 0 ? ticker.TradeHistory.First().IdString : null;

            int startIndex = 0;
            List<string[]> trades = DeserializeArrayOfObjects(bytes, ref startIndex, 
                new string[] { "globalTradeID", "tradeID", "date", "type", "rate", "amount" ,"total" }, 
                (paramIndex, value) => { return paramIndex != 1 || value != lastGotTradeId; });

            
            TradeStatisticsItem st = new TradeStatisticsItem();
            st.MinBuyPrice = double.MaxValue;
            st.MinSellPrice = double.MaxValue;
            st.Time = DateTime.UtcNow;

            int index = 0;
            foreach(string[] obj in trades) {
                TradeHistoryItem item = new TradeHistoryItem();
                item.IdString = obj[1];
                item.TimeString = obj[2];

                bool isBuy = obj[3].Length == 3;
                item.AmountString = obj[5];
                item.Type = isBuy ? TradeType.Buy : TradeType.Sell;
                item.RateString = obj[4];
                double price = item.Rate;
                double amount = item.Amount;
                item.Total = price * amount;
                if(isBuy) {
                    st.BuyAmount += amount;
                    st.MinBuyPrice = Math.Min(st.MinBuyPrice, price);
                    st.MaxBuyPrice = Math.Max(st.MaxBuyPrice, price);
                    st.BuyVolume += item.Total;
                }
                else {
                    st.SellAmount += amount;
                    st.MinSellPrice = Math.Min(st.MinSellPrice, price);
                    st.MaxSellPrice = Math.Max(st.MaxSellPrice, price);
                    st.SellVolume += item.Total;
                }
                ticker.TradeHistory.Insert(index, item);
                index++;
            }
            if(st.MinSellPrice == double.MaxValue)
                st.MinSellPrice = 0;
            if(st.MinBuyPrice == double.MaxValue)
                st.MinBuyPrice = 0;
            ticker.LastTradeStatisticTime = DateTime.UtcNow;
            ticker.TradeStatistic.Add(st);
            return true;
        }

        public override bool UpdateMyTrades(TickerBase ticker) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("nonce", GetNonce());
            coll.Add("command", "returnTradeHistory");
            coll.Add("currencyPair", ticker.MarketName);

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                byte[] data = client.UploadValues(address, coll);
                return OnUpdateMyTrades(ticker, data);
            }
            catch(Exception e) {
                Debug.WriteLine("get my trade history exception: " + e.ToString());
                return false;
            }
        }
        bool OnUpdateMyTrades(TickerBase ticker, byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return false;
            if(text == "[]") {
                ticker.MyTradeHistory.Clear();
                return true;
            }
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            lock(ticker.MyTradeHistory) {
                ticker.MyTradeHistory.Clear();
                foreach(JProperty prop in res.Children()) {
                    if(prop.Name == "error") {
                        Debug.WriteLine("OnUpdateMyTrades fails: " + prop.Value<string>());
                        return false;
                    }
                    JArray array = (JArray)prop.Value;
                    ticker.MyTradeHistory.Clear();
                    foreach(JObject obj in array) {
                        TradeHistoryItem info = new TradeHistoryItem();
                        info.OrderNumber = obj.Value<int>("orderNumber");
                        info.Time = obj.Value<DateTime>("date");
                        info.Type = obj.Value<string>("type") == "sell" ? TradeType.Sell : TradeType.Buy;
                        info.RateString = obj.Value<string>("rate");
                        info.AmountString = obj.Value<string>("amount");
                        info.Total = obj.Value<double>("total");
                        info.Fee = obj.Value<double>("fee");
                        ticker.MyTradeHistory.Add(info);
                    }
                }
            }
            return true;
        }
        public string ToQueryString(NameValueCollection nvc) {
            StringBuilder sb = new StringBuilder();

            foreach(string key in nvc.Keys) {
                if(string.IsNullOrEmpty(key)) continue;

                string[] values = nvc.GetValues(key);
                if(values == null) continue;

                foreach(string value in values) {
                    if(sb.Length > 0) sb.Append("&");
                    sb.AppendFormat("{0}={1}", Uri.EscapeDataString(key), Uri.EscapeDataString(value));
                }
            }

            return sb.ToString();
        }
        public override bool UpdateBalances() {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "returnCompleteBalances");
            coll.Add("nonce", GetNonce());

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                return OnGetBalances(client.UploadValues(address, coll));
            }
            catch(Exception) {
                return false;
            }
        }
        public Task<byte[]> GetBalancesAsync() {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "returnCompleteBalances");
            coll.Add("nonce", GetNonce());

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            return client.UploadValuesTaskAsync(address, coll);
        }
        public bool OnGetBalances(byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            lock(Balances) {
                foreach(JProperty prop in res.Children()) {
                    if(prop.Name == "error") {
                        Debug.WriteLine("OnGetBalances fails: " + prop.Value<string>());
                        return false;
                    }
                    JObject obj = (JObject)prop.Value;
                    PoloniexAccountBalanceInfo info = (PoloniexAccountBalanceInfo)Balances.FirstOrDefault(b => b.Currency == prop.Name);
                    if(info == null) {
                        info = new PoloniexAccountBalanceInfo();
                        info.Currency = prop.Name;
                        Balances.Add(info);
                    }
                    info.Currency = prop.Name;
                    info.LastAvailable = info.Available;
                    info.Available = obj.Value<double>("available");
                    info.OnOrders = obj.Value<double>("onOrders");
                    info.BtcValue = obj.Value<double>("btcValue");
                }
            }
            return true;
        }

        public bool GetDeposites() {
            Task<byte[]> task = GetDepositesAsync();
            task.Wait();
            return OnGetDeposites(task.Result);
        }
        public Task<byte[]> GetDepositesAsync() {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "returnDepositAddresses");
            coll.Add("nonce", GetNonce());

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                return client.UploadValuesTaskAsync(address, coll);
            }
            catch(Exception e) {
                Debug.WriteLine("GetDeposites failed:" + e.ToString());
                return null;
            }
        }
        public bool OnGetDeposites(byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text) || text == "[]")
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            lock(Balances) {
                foreach(JProperty prop in res.Children()) {
                    if(prop.Name == "error") {
                        Debug.WriteLine("OnGetDeposites fails: " + prop.Value<string>());
                        return false;
                    }
                    PoloniexAccountBalanceInfo info = (PoloniexAccountBalanceInfo)Balances.FirstOrDefault((a) => a.Currency == prop.Name);
                    if(info == null)
                        continue;
                    info.DepositAddress = (string)prop.Value;
                }
            }
            return true;
        }
        string GetNonce() {
            return ((long)((DateTime.UtcNow - new DateTime(1, 1, 1)).TotalMilliseconds * 10000)).ToString();
        }
        public override bool UpdateOpenedOrders(TickerBase ticker) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("nonce", GetNonce());
            coll.Add("command", "returnOpenOrders");
            coll.Add("currencyPair", ticker.MarketName);

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                byte[] data = client.UploadValues(address, coll);
                return OnGetOpenedOrders(ticker, data);
            }
            catch(Exception) {
                return false;
            }
        }
        public Task<byte[]> GetOpenedOrders() {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("nonce", GetNonce());
            coll.Add("command", "returnOpenOrders");
            coll.Add("currencyPair", "all");

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            return client.UploadValuesTaskAsync(address, coll);
        }

        public bool OnGetOpenedOrders(TickerBase ticker, byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return false;
            if(text == "[]") {
                ticker.OpenedOrders.Clear();
                return true;
            }
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            lock(ticker.OpenedOrders) {
                ticker.OpenedOrders.Clear();
                foreach(JProperty prop in res.Children()) {
                    if(prop.Name == "error") {
                        Debug.WriteLine("OnGetOpenedOrders fails: " + prop.Value<string>());
                        return false;
                    }
                    JArray array = (JArray)prop.Value;
                    foreach(JObject obj in array) {
                        OpenedOrderInfo info = new OpenedOrderInfo();
                        info.Market = prop.Name;
                        info.OrderNumber = obj.Value<int>("orderNumber");
                        info.Type = obj.Value<string>("type") == "sell" ? OrderType.Sell : OrderType.Buy;
                        info.Value = obj.Value<double>("rate");
                        info.Amount = obj.Value<double>("amount");
                        info.Total = obj.Value<double>("total");
                        ticker.OpenedOrders.Add(info);
                    }
                }
            }
            return true;
        }

        public bool OnGetOpenedOrders(byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            lock(OpenedOrders) {
                OpenedOrders.Clear();
                foreach(JProperty prop in res.Children()) {
                    if(prop.Name == "error") {
                        Debug.WriteLine("OnGetOpenedOrders fails: " + prop.Value<string>());
                        return false;
                    }
                    JArray array = (JArray)prop.Value;
                    foreach(JObject obj in array) {
                        OpenedOrderInfo info = new OpenedOrderInfo();
                        info.Market = prop.Name;
                        info.OrderNumber = obj.Value<int>("orderNumber");
                        info.Type = obj.Value<string>("type") == "sell" ? OrderType.Sell : OrderType.Buy;
                        info.Value = obj.Value<double>("rate");
                        info.Amount = obj.Value<double>("amount");
                        info.Total = obj.Value<double>("total");
                        OpenedOrders.Add(info);
                    }
                }
            }
            return true;
        }

        public bool BuyLimit(PoloniexTicker ticker, double rate, double amount) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "buy");
            coll.Add("nonce", GetNonce());
            coll.Add("currencyPair", ticker.CurrencyPair);
            coll.Add("rate", rate.ToString("0.########"));
            coll.Add("amount", amount.ToString("0.########"));

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                byte[] data = client.UploadValues(address, coll);
                return OnBuyLimit(ticker, data);
            }
            catch(Exception e) {
                Debug.WriteLine(e.ToString());
                return false;
            }
        }

        public long SellLimit(PoloniexTicker ticker, double rate, double amount) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "sell");
            coll.Add("nonce", GetNonce());
            coll.Add("currencyPair", ticker.CurrencyPair);
            coll.Add("rate", rate.ToString("0.########"));
            coll.Add("amount", amount.ToString("0.########"));

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                byte[] data = client.UploadValues(address, coll);
                return OnSellLimit(data);
            }
            catch(Exception e) {
                Debug.WriteLine(e.ToString());
                return -1;
            }
            
        }

        public bool OnBuyLimit(TickerBase ticker, byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            TradingResult tr = new TradingResult();
            tr.OrderNumber = res.Value<long>("orderNumber");
            tr.Type = OrderType.Buy;
            JArray array = res.Value<JArray>("resultingTrades");
            foreach(JObject trade in array) {
                TradeEntry e = new TradeEntry();
                e.Amount = trade.Value<double>("amount");
                e.Date = trade.Value<DateTime>("date");
                e.Rate = trade.Value<double>("rate");
                e.Total = trade.Value<double>("total");
                e.Id = trade.Value<long>("tradeID");
                e.Type = trade.Value<string>("type").Length == 3 ? OrderType.Buy : OrderType.Sell;
                tr.Trades.Add(e);
            }
            tr.Calculate();
            ticker.Trades.Add(tr);
            return true;
        }

        public string OnCreateDeposit(string currency, byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return null;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            if(res.Value<int>("success") != 1)
                return null;
            string deposit = res.Value<string>("response");
            PoloniexAccountBalanceInfo info = (PoloniexAccountBalanceInfo)Balances.FirstOrDefault(b => b.Currency == currency);
            info.DepositAddress = deposit;
            return deposit;
        }

        public long OnSellLimit(byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return -1;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            return res.Value<long>("orderNumber");
        }

        public Task<byte[]> CancelOrder(ulong orderId) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "cancelOrder");
            coll.Add("nonce", GetNonce());
            coll.Add("orderNumber", orderId.ToString());

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            return client.UploadValuesTaskAsync(address, coll);
        }

        public bool OnCancel(byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            return res.Value<int>("success") == 1;
        }

        public Task<byte[]> WithdrawAsync(string currency, double amount, string addr, string paymentId) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "withdraw");
            coll.Add("nonce", GetNonce());
            coll.Add("currency", currency);
            coll.Add("amount", amount.ToString("0.########"));
            coll.Add("address", address);
            if(!string.IsNullOrEmpty(paymentId))
                coll.Add("paymentId", paymentId);

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            return client.UploadValuesTaskAsync(address, coll);
        }

        public bool Withdraw(string currency, double amount, string addr, string paymentId) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "withdraw");
            coll.Add("nonce", GetNonce());
            coll.Add("currency", currency);
            coll.Add("amount", amount.ToString("0.########"));
            coll.Add("address", address);
            if(!string.IsNullOrEmpty(paymentId))
                coll.Add("paymentId", paymentId);

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                byte[] data = client.UploadValues(address, coll);
                return OnWithdraw(data);
            }
            catch(Exception) {
                return false;
            }
        }

        public bool OnWithdraw(byte[] data) {
            string text = System.Text.Encoding.ASCII.GetString(data);
            if(string.IsNullOrEmpty(text))
                return false;
            JObject res = (JObject)JsonConvert.DeserializeObject(text);
            return !string.IsNullOrEmpty(res.Value<string>("responce"));
        }
        public bool GetBalance(string str) {
            return UpdateBalances();
        }
        public string CreateDeposit(string currency) {
            string address = string.Format("https://poloniex.com/tradingApi");

            NameValueCollection coll = new NameValueCollection();
            coll.Add("command", "generateNewAddress");
            coll.Add("nonce", GetNonce());
            coll.Add("currency", currency);

            WebClient client = GetWebClient();
            client.Headers.Clear();
            client.Headers.Add("Sign", GetSign(ToQueryString(coll)));
            client.Headers.Add("Key", ApiKey);
            try {
                byte[] data = client.UploadValues(address, coll);
                return OnCreateDeposit(currency, data);
            }
            catch(Exception e) {
                Debug.WriteLine(e.ToString());
                return null;
            }
        }

    }

    public delegate void TickerUpdateEventHandler(object sender, TickerUpdateEventArgs e);
    public delegate bool IfDelegate2(int paramIndex, string value);
    public class TickerUpdateEventArgs : EventArgs {
        public PoloniexTicker Ticker { get; set; }
    }
 }