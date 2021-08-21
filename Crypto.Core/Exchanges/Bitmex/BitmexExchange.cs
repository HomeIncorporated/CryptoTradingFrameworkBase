﻿using CryptoMarketClient.Common;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using WebSocket4Net;
using SuperSocket.ClientEngine;
using CryptoMarketClient.Helpers;
using Crypto.Core.Exchanges.Base;
using System.Text;
using Crypto.Core.Exchanges.Bitmex;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using Crypto.Core.Helpers;
using System.Threading;

namespace CryptoMarketClient.Exchanges.Bitmex {
    public class BitmexExchange : Exchange {
        static BitmexExchange defaultExchange;
        public static BitmexExchange Default {
            get {
                if(defaultExchange == null)
                    defaultExchange = (BitmexExchange)Exchange.FromFile(ExchangeType.Bitmex, typeof(BitmexExchange));
                return defaultExchange;
            }
        }

        protected override bool ShouldAddKlineListener => false;

        protected internal override HMAC CreateHmac(string secret) {
            return new HMACSHA256(System.Text.ASCIIEncoding.Default.GetBytes(secret));
        }

        public override bool AllowCandleStickIncrementalUpdate => false;

        public override ExchangeType Type => ExchangeType.Bitmex;

        public override string BaseWebSocketAdress => "wss://www.bitmex.com/realtime?subscribe=instrument";

        protected override string GetKlineSocketAddress(Ticker ticker) {
            return string.Empty;
        }

        public override TradingResult BuyLong(AccountInfo account, Ticker ticker, double rate, double amount) {
            string data = string.Format("symbol={0}&orderQty={1}&price={2}&ordType=Limit", ticker.MarketName, amount, rate);
            byte[] bytes = UploadPrivateData(account, "POST", "/api/v1/order", data);
            if(bytes == null)
                return null;
            return OnTradingResult(account, ticker, Encoding.UTF8.GetString(bytes));
        }

        public override TradingResult BuyShort(AccountInfo account, Ticker ticker, double rate, double amount) {
            throw new NotImplementedException();
        }

        public override TradingResult SellShort(AccountInfo account, Ticker ticker, double rate, double amount) {
            throw new NotImplementedException();
        }

        public TradingResult OnTradingResult(AccountInfo account, Ticker ticker, string text) {
            try {
                JObject obj = JsonConvert.DeserializeObject<JObject>(text);
                JObject error = obj.Value<JObject>("error");
                if(error != null) {
                    LogManager.Default.Error(this, "trade", error.Value<string>("message"));
                    return null;
                }
                TradingResult res = new TradingResult();
                res.OrderId = obj.Value<string>("orderID");
                res.Date = Convert.ToDateTime(obj.Value<string>("transactTime"));
                res.Amount = FastValueConverter.Convert(obj.Value<string>("orderQty"));
                res.Type = obj.Value<string>("side")[0] == 'B' ? OrderType.Buy : OrderType.Sell;
                res.Value = FastValueConverter.Convert(obj.Value<string>("price"));
                res.OrderStatus = obj.Value<string>("ordStatus");
                res.Filled = res.OrderStatus == "Filled";
                res.Total = res.Amount;
                return res;
            }
            catch(Exception e) {
                Telemetry.Default.TrackException(e);
                return null;
            }
        }

        public override bool Cancel(AccountInfo account, string orderId) {
            throw new NotImplementedException();
        }

        //public override Form CreateAccountForm() {
        //    return new AccountBalancesForm(this);
        //}

        public override string CreateDeposit(AccountInfo account, string currency) {
            throw new NotImplementedException();
        }

        public override List<CandleStickIntervalInfo> GetAllowedCandleStickIntervals() {
            return new List<CandleStickIntervalInfo>();
        }

        public override bool GetBalance(AccountInfo info, string currency) {
            return true;
        }

        public override bool GetDeposites(AccountInfo account) {
            return true;
        }

        public override bool GetTickersInfo() {
            string address = "https://www.bitmex.com/api/v1/instrument?columns=typ,symbol,rootSymbol,quoteCurrency,highPrice,lowPrice,bidPrice,askPrice,lastChangePcnt,hasLiquidity,volume,tickSize,takerFee&start=0&count=500";
            string text = string.Empty;
            try {
                text = GetDownloadString(address);

                if(string.IsNullOrEmpty(text))
                    return false;
                Tickers.Clear();
                JArray res = JsonConvert.DeserializeObject<JArray>(text);
                int index = 0;
                foreach(JObject obj in res.Children()) {
                    if(!obj.Value<bool>("hasLiquidity"))
                        continue;
                    string pair = obj.Value<string>("symbol");
                    BitmexTicker t = (BitmexTicker)Tickers.FirstOrDefault(tt => tt.CurrencyPair == pair);
                    if(t == null) t = new BitmexTicker(this);
                    t.Index = index;
                    t.ContractTicker = true;
                    t.ContractValue = 1; // 1 USD
                    t.CurrencyPair = pair;
                    t.MarketCurrency = obj.Value<string>("rootSymbol");
                    t.BaseCurrency = obj.Value<string>("quoteCurrency");
                    t.TickSize = ToDouble(obj, "tickSize");
                    t.Last = ToDouble(obj, "lastPrice");
                    t.Hr24High = ToDouble(obj, "highPrice");
                    t.Hr24Low = ToDouble(obj, "lowPrice");
                    t.HighestBid = ToDouble(obj, "bidPrice");
                    t.LowestAsk = ToDouble(obj, "askPrice");
                    t.Timestamp = Convert.ToDateTime(obj.Value<string>("timestamp"));
                    t.Change = ToDouble(obj, "lastChangePcnt");
                    t.Volume = ToDouble(obj, "volume24h");
                    t.Fee = ToDouble(obj, "takerFee") * 100;
                    Tickers.Add(t);
                    index++;
                }
            }
            catch(Exception) {
                return false;
            }
            IsInitialized = true;
            return true;
        }

        double ToDouble(JObject obj, string name) {
            return obj.Value<string>(name) == null ? 0.0 : obj.Value<double>(name);
        }

        protected string DateToString(DateTime time) {
            return string.Format("{0:D4}-{1:D2}-{2:D2} {3:D2}:{4:D2}:{5:D2}.{6:D3}Z", time.Year, time.Month, time.Day, time.Hour, time.Minute, time.Second, time.Millisecond);
        }

        protected override bool GetTradesCore(ResizeableArray<TradeInfoItem> list, Ticker ticker, DateTime startTime, DateTime endTime) {
            string address = string.Format("https://www.bitmex.com/api/v1/trade?symbol={0}&count=1000&startTime={1}&endTime={2}", ticker.Name, DateToString(startTime), DateToString(endTime));
            string text = string.Empty;

            try {
                text = GetDownloadString(address);

                if(string.IsNullOrEmpty(text))
                    return false;
                if(text[0] == '{') {
                    JObject obj = JsonConvert.DeserializeObject<JObject>(text);
                    LogManager.Default.Add(LogType.Error, this, Type.ToString(), "error in GetTradesCore", obj.Value<string>("message"));
                    return false;
                }
                JArray res = JsonConvert.DeserializeObject<JArray>(text);
                foreach(JObject obj in res.Children()) {
                    TradeInfoItem item = new TradeInfoItem();
                    item.Ticker = ticker;
                    item.RateString = obj.Value<string>("price");
                    item.AmountString = obj.Value<string>("size");
                    item.Type = obj.Value<string>("side")[0] == 'S' ? TradeType.Sell : TradeType.Buy;
                    item.TimeString = obj.Value<string>("timestamp");
                    DateTime time = item.Time;
                    if(list.Last() == null || list.Last().Time <= time)
                        list.Add(item);
                    else
                        break;
                }
            }
            catch(Exception) {
                return false;
            }
            return true;
        }

        public override bool ObtainExchangeSettings() {
            RequestRate = new List<RateLimit>();
            RequestRate.Add(new RateLimit() { Interval = TimeSpan.TicksPerMinute, Limit = 60 });
            return true;
        }

        protected int XRateLimitLimit { get; set; }
        protected int XRateLimitRemain { get; set; }
        protected long XRateLimitReset { get; set; }
        protected override void CheckRequestRateLimits() {
            base.CheckRequestRateLimits();
            if(XRateLimitLimit != 0) { 
                if(XRateLimitRemain == 0) {
                    long now = 0;
                    while((now = ToUnixTimestamp(DateTime.UtcNow)) < XRateLimitReset){
                        long delta = now - XRateLimitReset;
                        if(delta > 60)
                            delta = 60;
                        Thread.Sleep((int)(delta + 5));
                    }
                }
            }
        }

        public override void OnAccountRemoved(AccountInfo info) {
            DefaultAccount = Accounts.FirstOrDefault(a => a.Default);
        }

        public override bool ProcessOrderBook(Ticker tickerBase, string text) {
            throw new NotImplementedException();
        }

        public override TradingResult SellLong(AccountInfo account, Ticker ticker, double rate, double amount) {
            string text = DownloadPrivateString(account, "POST",
                string.Format("/api/v1/order?symbol={0}&orderQty={1}&price={2}&ordType=Market&side=Sell", ticker.MarketName, amount, rate));
            if(string.IsNullOrEmpty(text))
                return null;
            return OnTradingResult(account, ticker, text);
        }

        public override bool SupportWebSocket(WebSocketType type) {
            if(type == WebSocketType.Ticker)
                return true;
            if(type == WebSocketType.Tickers)
                return true;
            return false;
        }

        public override bool UpdateAccountTrades(AccountInfo account, Ticker ticker) {
            return true;
        }

        private long GetExpires() {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 36000 + 3600; // set expires one hour in the future
        }

        protected byte[] UploadPrivateData(AccountInfo account, string verb, string path, string data) {
            string expires = GetExpires().ToString();
            MyWebClient client = GetWebClient();

            client.Headers.Clear();
            client.Headers.Add("api-expires", expires);
            string textToSignature = verb + path + expires + data;
            client.Headers.Add("api-signature", account.GetSign(textToSignature));
            client.Headers.Add("api-key", account.ApiKey);
            try {
                return client.UploadData("https://www.bitmex.com" + path, data);
            }
            catch(Exception e) {
                LogManager.Default.Log(e.ToString());
                return null;
            }
        }

        protected byte[] DownloadPrivateData(AccountInfo account, string verb, string path) {
            string address = "https://www.bitmex.com" + path;

            string expires = GetExpires().ToString();
            MyWebClient client = GetWebClient();

            client.Headers.Clear();
            client.Headers.Add("api-expires", expires);
            string textToSignature = verb + path + expires;
            client.Headers.Add("api-signature", account.GetSign(textToSignature));
            client.Headers.Add("api-key", account.ApiKey);

            try {
                return client.DownloadData(address);
            }
            catch(Exception e) {
                LogManager.Default.Log(e.ToString());
                return null;
            }
        }

        protected internal override void OnRequestCompleted(MyWebClient myWebClient) {
            base.OnRequestCompleted(myWebClient);
            if(XRateLimitLimit == 0) {
                IEnumerable<string> limit = myWebClient.ResponseHeaders.GetValues("X-RateLimit-Limit");
                XRateLimitLimit = Convert.ToInt32(limit.First());
            }
            IEnumerable<string> remain = myWebClient.ResponseHeaders.GetValues("X-RateLimit-Remaining");
            XRateLimitRemain = FastValueConverter.ConvertPositiveInteger(remain.First());
            IEnumerable<string> resetTime = myWebClient.ResponseHeaders.GetValues("X-RateLimit-Reset");
            XRateLimitReset = FastValueConverter.ConvertPositiveLong(resetTime.First());
        }

        protected string DownloadPrivateString(AccountInfo account, string verb, string path) {
            string address = "https://www.bitmex.com" + path;

            string expires = GetExpires().ToString();
            MyWebClient client = GetWebClient();

            client.Headers.Clear();
            client.Headers.Add("api-expires", expires);
            string textToSignature = verb + path + expires;
            client.Headers.Add("api-signature", account.GetSign(textToSignature));
            client.Headers.Add("api-key", account.ApiKey);

            try {
                return Encoding.UTF8.GetString(client.DownloadData(address));
            }
            catch(Exception e) {
                LogManager.Default.Log(e.ToString());
                return null;
            }
        }

        public override bool UpdateBalances(AccountInfo account) {
            byte[] data = DownloadPrivateData(account, "GET", "/api/v1/user/wallet");
            if(data == null)
                return false;
            return OnGetBalances(account, data );
        }

        public bool OnGetBalances(AccountInfo account, byte[] data) {
            string text = UTF8Encoding.Default.GetString(data);
            JObject obj = (JObject)JsonConvert.DeserializeObject(text);
            account.Balances.Clear();
            BitmexAccountBalanceInfo b = new BitmexAccountBalanceInfo(account);
            b.Currency = obj.Value<string>("currency");
            b.Available = obj.Value<double>("amount");
            b.OnOrders = 0;// obj.Value<double>("locked");
            account.Balances.Add(b);
            return true;
        }

        public override bool UpdateCurrencies() {
            return true;
        }

        public override bool UpdateOpenedOrders(AccountInfo account, Ticker ticker) {
            return UpdateOrdersCore(account, ticker, false);
        }

        protected virtual bool UpdateOrdersCore(AccountInfo account, Ticker ticker, bool onlyOpened) {
            if(account == null)
                return false;

            string path = "/api/v1/order?reverse=true";
            if(onlyOpened)
                path = "/api/v1/order?reverse=true&filter=%7B%22open%22%3A%22true%22%7D"; //{"open":"true"}
            byte[] data = DownloadPrivateData(account, "GET", path);
            if(data == null)
                return false;
            OnUpdateOpenedOrders(account, ticker, data);
            return true;
        }

        private void OnUpdateOpenedOrders(AccountInfo account, Ticker ticker, byte[] data) {
            string text = UTF8Encoding.Default.GetString(data);
            JArray items = (JArray)JsonConvert.DeserializeObject(text);
            List<OpenedOrderInfo> openedOrders = ticker == null ? account.OpenedOrders : ticker.OpenedOrders;
            lock(openedOrders) {
                openedOrders.Clear();

                foreach(JObject item in items) {
                    string symbol = item.Value<string>("symbol");
                    OpenedOrderInfo info = new OpenedOrderInfo(account, ticker);
                    info.OrderId = item.Value<string>("orderId");
                    info.Type = item.Value<string>("side")[0] == 'B' ? OrderType.Buy : OrderType.Sell;
                    info.ValueString = item.Value<string>("price");
                    info.AmountString = item.Value<string>("orderQty");
                    info.TotalString = info.AmountString;
                    info.DateString = item.Value<string>("transactTime");
                    info.TickerName = symbol;
                    openedOrders.Add(info);
                }
            }
            if(ticker != null)
                ticker.RaiseOpenedOrdersChanged();
        }
        
        string GetOrderBookString(Ticker ticker, int depth) {
            return string.Format("https://www.bitmex.com/api/v1/orderBook/L2?symbol={0}&depth=0", ticker.CurrencyPair);
        }
        public override bool UpdateOrderBook(Ticker ticker, int depth) {
            string address = GetOrderBookString(ticker, depth);
            byte[] data = GetDownloadBytes(address);
            if(data == null)
                return false;
            return OnUpdateOrderBook(ticker, data);
        }
        public override bool UpdateOrderBook(Ticker ticker) {
            string address = GetOrderBookString(ticker, OrderBook.Depth);
            byte[] data = GetDownloadBytes(address);
            if(data == null)
                return false;
            return OnUpdateOrderBook(ticker, data);
        }
        public override void UpdateOrderBookAsync(Ticker ticker, int depth, Action<OperationResultEventArgs> onOrderBookUpdated) {
            string address = GetOrderBookString(ticker, OrderBook.Depth);
            GetDownloadBytesAsync(address, t => {
                OnUpdateOrderBook(ticker, t.Result);
                onOrderBookUpdated(new OperationResultEventArgs() { Ticker = ticker, Result = t.Result != null });
            });
        }

        protected string[] OrderBookItems { get; } = new string[] { "symbol", "id", "side", "size", "price" };

        bool OnUpdateOrderBook(Ticker ticker, byte[] bytes) {
            if(bytes == null)
                return false;

            int startIndex = 0; // skip {

            List<string[]> items = JSonHelper.Default.DeserializeArrayOfObjects(bytes, ref startIndex, OrderBookItems);

            ticker.OrderBook.BeginUpdate();
            try {
                List<OrderBookEntry> bids = ticker.OrderBook.Bids;
                List<OrderBookEntry> asks = ticker.OrderBook.Asks;
                List<OrderBookEntry> iasks = ticker.OrderBook.AsksInverted;
                for(int i = 0; i < items.Count; i++) {
                    string[] item = items[i];
                    OrderBookEntry entry = new OrderBookEntry();
                    entry.Id = FastValueConverter.ConvertPositiveLong(item[1]);
                    entry.ValueString = item[4];
                    entry.AmountString = item[3];
                    if(item[2][0] == 'S') {
                        if(iasks != null)
                            iasks.Add(entry);
                        asks.Insert(0, entry);
                    }
                    else
                        bids.Add(entry);
                }
            }
            finally {
                ticker.OrderBook.IsDirty = false;
                ticker.OrderBook.EndUpdate();
            }
            return true;
        }

        public override bool UpdateTicker(Ticker tickerBase) {
            return true;
        }

        Stopwatch UpdateTickersTimer { get; } = new Stopwatch();
        public override bool UpdateTickersInfo() {
            if(!UpdateTickersTimer.IsRunning)
                UpdateTickersTimer.Start();
            if(UpdateTickersTimer.ElapsedMilliseconds < 5000)
                return true;
            bool res = GetTickersInfo();
            UpdateTickersTimer.Restart();
            return res;
        }

        public override bool UpdateTrades(Ticker tickerBase) {
            return true;
        }

        public override bool Withdraw(AccountInfo account, string currency, string adress, string paymentId, double amount) {
            throw new NotImplementedException();
        }

        protected internal override IIncrementalUpdateDataProvider CreateIncrementalUpdateDataProvider() {
            return new BitmexIncDataProvider(this);
        }

        public override void StartListenTickerStream(Ticker ticker) {
            base.StartListenTickerStream(ticker);
            StartListenOrderBook(ticker);
        }



        protected override string GetOrderBookSocketAddress(Ticker ticker) {
            return string.Format("wss://www.bitmex.com/realtime?subscribe=orderBookL2:{0}", ticker.CurrencyPair);
        }

        protected override string GetTradeSocketAddress(Ticker ticker) {
            return string.Format("wss://www.bitmex.com/realtime?subscribe=trade:{0}", ticker.CurrencyPair);
        }

        OrderBookUpdateType String2UpdateType(string action) {
            if(action == "insert")
                return OrderBookUpdateType.Add;
            if(action == "delete")
                return OrderBookUpdateType.Remove;
            if(action == "update")
                return OrderBookUpdateType.Modify;
            if(action == "partial")
                return OrderBookUpdateType.RefreshAll;
            return OrderBookUpdateType.Modify;
        }

        protected override int WebSocketAllowedDelayInterval { get { return 15000; } }

        protected internal override void OnTradeHistorySocketMessageReceived(object sender, MessageReceivedEventArgs e) {
            base.OnTradeHistorySocketMessageReceived(sender, e);
            LastWebSocketRecvTime = DateTime.Now;
            SocketConnectionInfo info = TradeHistorySockets.FirstOrDefault(c => c.Key == sender);
            if(info == null)
                return;
            Ticker t = info.Ticker;

            if(t.CaptureData)
                t.CaptureDataCore(CaptureStreamType.TradeHistory, CaptureMessageType.Incremental, e.Message);

            JObject obj = JsonConvert.DeserializeObject<JObject>(e.Message);
            JArray items = obj.Value<JArray>("data");
            if(items == null)
                return;
            foreach(JObject item in items) {
                TradeInfoItem ti = new TradeInfoItem(null, t);
                ti.TimeString = item.Value<string>("timestamp");
                ti.Type = item.Value<string>("side")[0] == 'S' ? TradeType.Sell : TradeType.Buy;
                ti.AmountString = item.Value<string>("size");
                ti.RateString = item.Value<string>("price");
                t.TradeHistory.Insert(0, ti);
            }

            if(t.HasTradeHistorySubscribers) {
                TradeHistoryChangedEventArgs ee = new TradeHistoryChangedEventArgs() { NewItem = t.TradeHistory[0] };
                t.RaiseTradeHistoryChanged(ee);
            }
        }

        protected int RemainingConnectionCount { get; set; } = int.MaxValue;
        protected void ProcessInfoMessage(JObject obj) {
            JObject limit = obj.Value<JObject>("limit");
            if(limit != null)
                RemainingConnectionCount = FastValueConverter.ConvertPositiveInteger(limit.Value<string>("remaining"));
        }

        protected bool CheckProcessError(SocketConnectionInfo info) {
            if(info.LastError.Contains("Too Many Requests")) {
                info.ReconnectAfter(30);
                return true;
            }
            else if(info.LastError.Contains("Forbidden")) {
                return true;
            }
            return false;
        }
        protected internal override void OnOrderBookSocketError(object sender, ErrorEventArgs e) {
            SocketConnectionInfo info = OrderBookSockets.FirstOrDefault(c => c.Key == sender);
            if(info != null) {
                info.Ticker.OrderBook.IsDirty = true;
                if(CheckProcessError(info))
                    return;
            }
            base.OnOrderBookSocketError(sender, e);
        }
        protected internal override void OnTradeHistorySocketError(object sender, ErrorEventArgs e) {
            SocketConnectionInfo info = TradeHistorySockets.FirstOrDefault(c => c.Key == sender);
            if(info != null) {
                if(CheckProcessError(info))
                    return;
            }
            base.OnTradeHistorySocketError(sender, e);
        }

        protected string[] UpdateItems = new string[] { "symbol", "id", "side", "size" };
        protected string[] InsertItems = new string[] { "timestamp", "symbol", "side", "size", "price", "tickDirection", "trdMatchID", "grossValue", "homeNotional", "foreignNotional" };
        protected string[] DeleteItems = new string[] { "symbol", "id", "side" };
        protected internal override void OnOrderBookSocketMessageReceived(object sender, MessageReceivedEventArgs e) {
            base.OnOrderBookSocketMessageReceived(sender, e);
            LastWebSocketRecvTime = DateTime.Now;
            SocketConnectionInfo info = OrderBookSockets.FirstOrDefault(c => c.Key == sender);
            if(info == null)
                return;
            Ticker t = info.Ticker;
            if(t.CaptureData)
                t.CaptureDataCore(CaptureStreamType.OrderBook, CaptureMessageType.Incremental, e.Message);

            const string incrementalUpdateStartString = "{\"table\":\"orderBookL2\",\"action\":\"";
            if(e.Message.StartsWith(incrementalUpdateStartString)) {
                int index = incrementalUpdateStartString.Length;
                if(e.Message[index] == 'u') { // update
                    index = e.Message.IndexOf("[{");
                    List<string[]> jsItems = JSonHelper.Default.DeserializeArrayOfObjects(Encoding.ASCII.GetBytes(e.Message), ref index, UpdateItems);
                    foreach(string[] item in jsItems) {
                        OrderBookEntryType entryType = item[2][0] == 'S' ? OrderBookEntryType.Ask : OrderBookEntryType.Bid;
                        t.OrderBook.ApplyIncrementalUpdate(entryType, OrderBookUpdateType.Modify, FastValueConverter.ConvertPositiveLong(item[1]), null, item[3]);
                    }
                    return;
                }
                //else if(e.Message[index] == 'i') { // insert
                //    index = e.Message.IndexOf("[{");
                //    List<string[]> jsItems = JSonHelper.Default.DeserializeArrayOfObjects(Encoding.ASCII.GetBytes(e.Message), ref index, InsertItems);
                //    foreach(string[] item in jsItems) {
                //        OrderBookEntryType entryType = item[2][0] == 'S' ? OrderBookEntryType.Ask : OrderBookEntryType.Bid;
                //        t.OrderBook.ApplyIncrementalUpdate(entryType, OrderBookUpdateType.Add, FastValueConverter.ConvertPositiveLong(item[1]), item[4], item[3]);
                //    }
                //    return;
                //}
                else if(e.Message[index] == 'd') { // delete
                    index = e.Message.IndexOf("[{");
                    List<string[]> jsItems = JSonHelper.Default.DeserializeArrayOfObjects(Encoding.ASCII.GetBytes(e.Message), ref index, DeleteItems);
                    foreach(string[] item in jsItems) {
                        OrderBookEntryType entryType = item[2][0] == 'S' ? OrderBookEntryType.Ask : OrderBookEntryType.Bid;
                        t.OrderBook.ApplyIncrementalUpdate(entryType, OrderBookUpdateType.Remove, FastValueConverter.ConvertPositiveLong(item[1]), null, null);
                    }
                    return;
                }
            }
            JObject obj = JsonConvert.DeserializeObject<JObject>(e.Message);
            JArray items = obj.Value<JArray>("data");
            if(items == null)
                return;

            OrderBookUpdateType type = String2UpdateType(obj.Value<string>("action"));
            lock(t.OrderBook.Bids) {
                lock(t.OrderBook.Asks) {
                    if(type == OrderBookUpdateType.RefreshAll) {
                        t.OrderBook.Clear();
                        t.OrderBook.BeginUpdate();
                    }
                    for(int i = 0; i < items.Count; i++) {
                        JObject item = (JObject)items[i];

                        OrderBookEntryType entryType = item.Value<string>("side")[0] == 'S' ? OrderBookEntryType.Ask : OrderBookEntryType.Bid;
                        string rate = null;
                        if(type == OrderBookUpdateType.Add || type == OrderBookUpdateType.RefreshAll)
                            rate = item.Value<string>("price");
                        string size = null;
                        if(type != OrderBookUpdateType.Remove)
                            size = item.Value<string>("size");
                        t.OrderBook.ApplyIncrementalUpdate(entryType, type, item.Value<long>("id"), rate, size);
                    }
                }
            }
            if(type == OrderBookUpdateType.RefreshAll) {
                t.OrderBook.EndUpdate();
            }
        }

        protected internal override void OnTickersSocketMessageReceived(object sender, MessageReceivedEventArgs e) {
            base.OnTickersSocketMessageReceived(sender, e);

            JObject res = JsonConvert.DeserializeObject<JObject>(e.Message, 
                new JsonSerializerSettings() {
                    Converters = new List<JsonConverter>(),
                    FloatParseHandling = FloatParseHandling.Double });
            if(res.Value<string>("table") == "instrument")
                OnTickersInfoRecv(res);
        }

        protected void OnTickersInfoRecv(JObject jObject) {
            JArray items = jObject.Value<JArray>("data");
            if(items == null)
                return;
            for(int i = 0; i < items.Count; i++) {
                JObject item = (JObject) items[i];
                string tickerName = item.Value<string>("symbol");
                Ticker first = null;
                for(int index = 0; index < Tickers.Count; index++) {
                    Ticker tt = Tickers[index];
                    if(tt.CurrencyPair == tickerName) {
                        first = tt;
                        break;
                    }
                }
                BitmexTicker t = (BitmexTicker) first;
                if(t == null)
                    continue;
                JEnumerable<JToken> props = item.Children();
                foreach(JProperty prop in props) {
                    string name = prop.Name;
                    string value = prop.Value == null ? null : prop.Value.ToString();
                    switch(name) {
                        case "lastPrice":
                            t.Last = FastValueConverter.Convert(value);
                            break;
                        case "highPrice":
                            t.Hr24High = FastValueConverter.Convert(value);
                            break;
                        case "lowPrice":
                            t.Hr24Low = FastValueConverter.Convert(value);
                            break;
                        case "bidPrice":
                            t.HighestBid = FastValueConverter.Convert(value);
                            break;
                        case "askPrice":
                            t.LowestAsk = FastValueConverter.Convert(value);
                            break;
                        case "timestamp":
                            t.Timestamp = t.Time = Convert.ToDateTime(value);
                            break;
                        case "lastChangePcnt":
                            t.Change = FastValueConverter.Convert(value);
                            break;
                        case "volume24h":
                            t.Volume = FastValueConverter.Convert(value);
                            break;
                    }
                }
                t.UpdateTrailings();
                lock(t) {
                    RaiseTickerChanged(t);
                }
            }
        }

        protected void OnTickerOrderBookRecv(JObject jObject) {
            
        }

        protected internal override void ApplyCapturedEvent(Ticker ticker, TickerCaptureDataInfo info) {
            if(info.StreamType == CaptureStreamType.OrderBook)
                OnOrderBookSocketMessageReceived(OrderBookSockets.FirstOrDefault(s => s.Ticker == ticker).Socket, new MessageReceivedEventArgs(info.Message));
            else if(info.StreamType == CaptureStreamType.TradeHistory)
                OnTradeHistorySocketMessageReceived(TradeHistorySockets.FirstOrDefault(s => s.Ticker == ticker).Socket, new MessageReceivedEventArgs(info.Message));
        }
    }
}
