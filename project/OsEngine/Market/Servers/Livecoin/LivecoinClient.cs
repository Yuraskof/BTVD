﻿using Newtonsoft.Json;
using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market.Servers.Livecoin.LivecoinEntity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using OsEngine.Market.Servers.Livecoin.LivecoinEntity.protobuf;
using WebSocketSharp;


namespace OsEngine.Market.Servers.Livecoin
{
    public class LivecoinClient
    {
        private string _baseUri = "https://api.livecoin.net/";

        private readonly string _pubKey;
        private readonly string _secKey;

        private readonly string _portfolioName;

        public string GetPortfolioName()
        {
            return _portfolioName;
        }

        /// <summary>
        /// shows whether connection works
        /// работает ли соединение
        /// </summary>
        public bool IsConnected;
        public LivecoinClient(string pubKey, string secKey, string portfolioname)
        {
            _pubKey = pubKey;
            _secKey = secKey;
            _portfolioName = "Portfolio" + portfolioname;
        }

        /// <summary>
        /// connecto to the exchange
        /// установить соединение с биржей 
        /// </summary>
        public void Connect()
        {
            if (string.IsNullOrEmpty(_pubKey) ||
                string.IsNullOrEmpty(_secKey))
            {
                return;
            }

            string endPoint = "exchange/ticker?";
            string param = "currencyPair=BTC/USD";

            _isDisposed = false;

            // check server availability for HTTP communication with it / проверяем доступность сервера для HTTP общения с ним
            Uri uri = new Uri(_baseUri + endPoint + param);
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
                var httpWebRequest = (HttpWebRequest)WebRequest.Create(uri);
                HttpWebResponse httpWebResponse = (HttpWebResponse)httpWebRequest.GetResponse();
            }
            catch (Exception exception)
            {
                SendLogMessage("Сервер не доступен. Отсутствует интернет " + exception.Message, LogMessageType.Error);
                return;
            }

            Thread converter = new Thread(Converter);
            converter.CurrentCulture = new CultureInfo("ru-RU");
            converter.Name = "LivecoinConverterFread_" + _portfolioName;
            converter.IsBackground = true;
            converter.Start();

            CreateNewWebSocket();
        }

        public void Dispose()
        {
            _isDisposed = true;

            if (ws != null)
            {
                ws.Close();

                ws.OnOpen -= Ws_OnOpen;
                ws.OnMessage -= Ws_OnMessage;
                ws.OnError -= Ws_OnError;
                ws.OnClose -= Ws_OnClose;

                ws = null;
            }

        }

        public void GetSecurities()
        {
            string endPoint = "exchange/restrictions";

            var res = SendQuery(false, endPoint);

            RestrictionSecurities restriction = JsonConvert.DeserializeAnonymousType(res, new RestrictionSecurities());

            if (UpdatePairs != null)
            {
                UpdatePairs(restriction);
            }
        }

        public void GetPortfolios()
        {
            string endPoint = "payment/balances";

            var res = SendQuery(true, endPoint, _pubKey, _secKey);

            List<Balance> balances = JsonConvert.DeserializeAnonymousType(res, new List<Balance>());

            BalanceInfo balanceInfo = new BalanceInfo();
            balanceInfo.Balances = balances;
            balanceInfo.Name = _portfolioName;

            NewPortfolio?.Invoke(balanceInfo);
        }

        private string SendQuery(bool isAuth, string endPoint, string publicKey = "", string secretKey = "", string data = "")
        {
            string ResponseFromServer = "";
            HttpStatusCode StatusCode;
            string uri;

            if (isAuth)
            {
                uri = _baseUri + endPoint;
            }
            else
            {
                uri = _baseUri + endPoint + data;
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.ContentType = "application/x-www-form-urlencoded";
            Stream dataStream;
            if (isAuth)
            {
                string param = http_build_query(data);
                string Sign = HashHMAC(secretKey, param).ToUpper();
                request.Headers["Api-Key"] = publicKey;
                request.Headers["Sign"] = Sign;
            }
            try
            {
                WebResponse WebResponse = request.GetResponse();
                dataStream = WebResponse.GetResponseStream();
                StreamReader StreamReader = new StreamReader(dataStream);
                ResponseFromServer = StreamReader.ReadToEnd();
                dataStream.Close();
                WebResponse.Close();
                StatusCode = HttpStatusCode.OK;
                return ResponseFromServer;
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    dataStream = ex.Response.GetResponseStream();
                    StreamReader StreamReader = new StreamReader(dataStream);
                    StatusCode = ((HttpWebResponse)ex.Response).StatusCode;
                    ResponseFromServer = ex.Message;
                }
                else
                {
                    StatusCode = HttpStatusCode.ExpectationFailed;
                    ResponseFromServer = "Неизвестная ошибка";
                }
                return ResponseFromServer;
            }
            catch (Exception ex)
            {
                StatusCode = HttpStatusCode.ExpectationFailed;
                ResponseFromServer = "Неизвестная ошибка";
                return ResponseFromServer;
            }
        }

        private string HashHMAC(string key, string message)
        {
            System.Text.UTF8Encoding encoding = new System.Text.UTF8Encoding();
            byte[] keyByte = encoding.GetBytes(key);

            HMACSHA256 hmacsha256 = new HMACSHA256(keyByte);

            byte[] messageBytes = encoding.GetBytes(message);
            byte[] hashmessage = hmacsha256.ComputeHash(messageBytes);
            return ByteArrayToString(hashmessage);
        }

        public string ByteArrayToString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);
            foreach (byte b in ba)
                hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }
        public string http_build_query(String formdata)
        {
            string str = formdata.Replace("/", "%2F");
            str = str.Replace("@", "%40");
            str = str.Replace(";", "%3B");
            return str;
        }


        /// <summary>
        /// adress for web-socket connection
        /// адрес для подключения к вебсокетам
        /// </summary>
        private string _wsUrl = "wss://ws.api.livecoin.net/ws/beta2";

        private WebSocket ws;

        /// <summary>
        /// create a new web-socket connection
        /// создать новое подключение по сокетам
        /// </summary>
        private void CreateNewWebSocket()
        {
            ws = new WebSocket(_wsUrl);
            Run();
        }

        private void Run()
        {
            ws.OnOpen += Ws_OnOpen;
            ws.OnMessage += Ws_OnMessage;
            ws.OnError += Ws_OnError;
            ws.OnClose += Ws_OnClose;
            ws.ConnectAsync();//Connect();
        }

        private void Ws_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            SendLogMessage("ошибка из сокета", LogMessageType.Error);
        }

        private void Ws_OnMessage(object sender, MessageEventArgs e)
        {
            if (e.IsBinary)
            {
                ProcessMessage(e.RawData);
            }
        }

        private void Ws_OnOpen(object sender, EventArgs e)
        {
            WsClient_Login(_pubKey, _secKey, 30000);
        }

        private void Ws_OnClose(object sender, CloseEventArgs e)
        {
            _isDisposed = true;

            Disconnected?.Invoke();
        }

        /// <summary>
        /// queue of new messages from the exchange server
        /// очередь новых сообщений, пришедших с сервера биржи
        /// </summary>
        private ConcurrentQueue<WsResponse> _newMessage = new ConcurrentQueue<WsResponse>();


        private void WsClient_Login(String key, String secret, int ttl)
        {
            byte[] msg;

            LoginRequest message = new LoginRequest
            {
                ExpireControl = new RequestExpired
                {
                    Now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Ttl = ttl
                },
                ApiKey = key
            };

            using (MemoryStream msgStream = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(msgStream, message);
                msg = msgStream.ToArray();
            }
            byte[] sign = ComputeHash(secret, msg);
            var meta = new WsRequestMetaData
            {
                RequestType = WsRequestMetaData.WsRequestMsgType.Login,
                Token = "Login request",
                Sign = sign
            };

            WsRequest request = new WsRequest
            {
                Meta = meta,
                Msg = msg
            };

            SendRequest(request);
        }

        public void Subscribe(string security)
        {
            security = security.Replace("_", "/");

            var orderBookMessage = new SubscribeOrderBookChannelRequest
            {
                CurrencyPair = security,
            };

            string token = "OrderBook_" + security;

            WsClient_Subscribe(token, WsRequestMetaData.WsRequestMsgType.SubscribeOrderBook, orderBookMessage);


            var tradesMessage = new SubscribeTradeChannelRequest
            {
                CurrencyPair = security,
            };

            token = "Trades_" + security;

            WsClient_Subscribe(token, WsRequestMetaData.WsRequestMsgType.SubscribeTrade, tradesMessage);

            SubscribePrivateChannel(_secKey, _portfolioName);
        }

        private void SubscribePrivateChannel(string privateKey, string portfolio)
        {
            var subscribeOrdersEvent = new PrivateSubscribeOrderRawChannelRequest
            {
                ExpireControl = new RequestExpired
                {
                    Now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Ttl = ttl
                },
                subscribe_type = PrivateSubscribeOrderRawChannelRequest.SubscribeType.OnlyEvents,
            };

            string token = "PrivateSubscribeOrderRawChannelRequest_" + portfolio;

            SendAuthMessage(privateKey, token, WsRequestMetaData.WsRequestMsgType.PrivateSubscribeOrderRaw, subscribeOrdersEvent);


            var subscribeTradesEvent = new PrivateSubscribeTradeChannelRequest
            {
                ExpireControl = new RequestExpired
                {
                    Now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Ttl = ttl
                },
            };

            token = "PrivateSubscribeTradeChannelRequest_" + portfolio;

            SendAuthMessage(privateKey, token, WsRequestMetaData.WsRequestMsgType.PrivateSubscribeTrade, subscribeTradesEvent);

            var balanceMessage = new PrivateSubscribeBalanceChangeChannelRequest()
            {
                ExpireControl = new RequestExpired
                {
                    Now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Ttl = ttl
                }
            };

            token = "First";

            SendAuthMessage(privateKey, token, WsRequestMetaData.WsRequestMsgType.SubscribeBalanceChange, balanceMessage);
        }

        public void SendOrder(OsEngine.Entity.Order order)
        {
            var newOrder = new PutLimitOrderRequest
            {
                CurrencyPair = order.SecurityNameCode.Replace("_","/"),
                Amount = order.Volume.ToString(),
                order_type = order.Side == Side.Sell ? PutLimitOrderRequest.OrderType.Ask : PutLimitOrderRequest.OrderType.Bid,
                Price = order.Price.ToString(),
                ExpireControl = new RequestExpired
                {
                    Now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Ttl = ttl
                }
            };

            string token = "NewOrder" + "_" + order.NumberUser.ToString();

            SendAuthMessage(_secKey, token, WsRequestMetaData.WsRequestMsgType.PutLimitOrder, newOrder);
        }

        public void CancelAllOrders(List<string> secName)
        {
            var cancelOrders = new CancelOrdersRequest
            {
                ExpireControl = new RequestExpired
                {
                    Now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Ttl = 30000
                },
            };

            string token = "CancelAllOrders";

            SendAuthMessage(_secKey, token, WsRequestMetaData.WsRequestMsgType.CancelOrders, cancelOrders);
        }

        private int ttl = 30000;

        public void CancelLimitOrder(OsEngine.Entity.Order order)
        {
            var cancelOrders = new CancelLimitOrderRequest
            {
                ExpireControl = new RequestExpired
                {
                    Now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Ttl = ttl
                },
                CurrencyPair = order.SecurityNameCode.Replace("_", "/"),
                Id = Convert.ToInt64(order.NumberMarket)
            };

            string token = "CancelOrder_" + order.NumberUser;

            SendAuthMessage(_secKey, token, WsRequestMetaData.WsRequestMsgType.CancelLimitOrder, cancelOrders);
        }

        private void SendAuthMessage<T>(string secret, string token, WsRequestMetaData.WsRequestMsgType requestType, T message)
        {
            WsRequestMetaData meta;

            byte[] msg;

            using (MemoryStream msgStream = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(msgStream, message);
                msg = msgStream.ToArray();
            }

            byte[] sign = ComputeHash(secret, msg);

            meta = new WsRequestMetaData
            {
                RequestType = requestType,
                Token = token,
                Sign = sign
            };

            WsRequest request = new WsRequest
            {
                Meta = meta,
                Msg = msg
            };

            SendRequest(request);
        }

        private void WsClient_Subscribe<T>(string token, WsRequestMetaData.WsRequestMsgType requestType, T message)
        {
            WsRequestMetaData meta;

            byte[] msg;

            using (MemoryStream msgStream = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(msgStream, message);
                msg = msgStream.ToArray();
            }

            meta = new WsRequestMetaData
            {
                RequestType = requestType,
                Token = token
            };

            WsRequest request = new WsRequest
            {
                Meta = meta,
                Msg = msg
            };

            SendRequest(request);
        }

        private void SendRequest(WsRequest request)
        {
            using (var requestStream = new MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(requestStream, request);
                //ws.Send(requestStream.ToArray());
                ws.SendAsync(requestStream.ToArray(), b => { });
            }
        }

        private byte[] ComputeHash(string secret, byte[] message)
        {
            var key = Encoding.UTF8.GetBytes(secret);
            byte[] hash = null;
            using (var hmac = new HMACSHA256(key))
            {
                hash = hmac.ComputeHash(message);
            }

            return hash;
        }

        private void ProcessMessage(byte[] receivedData)
        {
            // parsing response
            using (MemoryStream responseStream = new MemoryStream(receivedData))
            {
                WsResponse response = ProtoBuf.Serializer.Deserialize<WsResponse>(responseStream);

                _newMessage.Enqueue(response);
            }
        }

        private bool _isDisposed = false;

        /// <summary>
        /// takes messages from the common queue, converts them to C # classes and sends them to up
        /// берет сообщения из общей очереди, конвертирует их в классы C# и отправляет на верх
        /// </summary>
        public void Converter()
        {
            while (true)
            {
                try
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    if (!_newMessage.IsEmpty)
                    {
                        WsResponse response;

                        if (_newMessage.TryDequeue(out response))
                        {
                            if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.TradeChannelSubscribed)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    TradeChannelSubscribedResponse message = ProtoBuf.Serializer.Deserialize<TradeChannelSubscribedResponse>(messageStream);
                                    SendLogMessage("Успешная подписка на все сделки", LogMessageType.System);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.TradeNotify)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    TradeNotification message = ProtoBuf.Serializer.Deserialize<TradeNotification>(messageStream);

                                    NewTradesEvent?.Invoke(message);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.Error)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    ErrorResponse message = ProtoBuf.Serializer.Deserialize<ErrorResponse>(messageStream);

                                    var token = response.Meta.Token;

                                    if (message.Message.StartsWith("Channel already subscribed"))
                                    {
                                        continue;
                                    }

                                    if (token.StartsWith("NewOrder"))
                                    {
                                        var order = new PrivateOrderRawEvent();
                                        order.Id = -1;
                                        MyOrderEvent?.Invoke(Convert.ToInt32(token.Split('_')[1]), GetPortfolioName(), order);
                                    }

                                    if (message.Message.StartsWith("insufficient funds"))
                                    {
                                        continue;
                                    }

                                    SendLogMessage("WsClient error : " + message.Message, LogMessageType.Error);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.ChannelUnsubscribed)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    ChannelUnsubscribedResponse message = ProtoBuf.Serializer.Deserialize<ChannelUnsubscribedResponse>(messageStream);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.LoginResponse)
                            {
                                IsConnected = true;

                                if (Connected != null)
                                {
                                    Connected();
                                }

                                // SendLogMessage("Соединение через вебсокет успешно установлено", LogMessageType.System);
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.BalanceChangeChannelSubscribed)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    PrivateSubscribeBalanceChangeChannelRequest message = ProtoBuf.Serializer.Deserialize<PrivateSubscribeBalanceChangeChannelRequest>(messageStream);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.BalanceChangeNotify)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    PrivateChangeBalanceNotification message = ProtoBuf.Serializer.Deserialize<PrivateChangeBalanceNotification>(messageStream);

                                    if (UpdatePortfolio != null)
                                    {
                                        UpdatePortfolio(GetPortfolioName(), message);
                                    }
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.OrderBookNotify)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    OrderBookNotification message = ProtoBuf.Serializer.Deserialize<OrderBookNotification>(messageStream);

                                    if (UpdateMarketDepth != null)
                                    {
                                        UpdateMarketDepth(message);
                                    }
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.OrderBookChannelSubscribed)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    OrderBookChannelSubscribedResponse message = ProtoBuf.Serializer.Deserialize<OrderBookChannelSubscribedResponse>(messageStream);

                                    // SendLogMessage("Успешная подписка на стакан котировок", LogMessageType.System);

                                    if (NewMarketDepth != null)
                                    {
                                        NewMarketDepth(message);
                                    }
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.PrivateOrderRawChannelSubscribed)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    //SendLogMessage("Успешная подписка на мои ордера", LogMessageType.System);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.PrivateOrderRawNotify)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    PrivateOrderRawNotification message = ProtoBuf.Serializer.Deserialize<PrivateOrderRawNotification>(messageStream);

                                    foreach (var ev in message.Datas)
                                    {
                                        if (!_myOrders.ContainsValue(ev.Id))
                                        {
                                            _orderEvents.Add(ev);
                                        }
                                        else
                                        {
                                            var needNumberUser = _myOrders.First(o => o.Value == ev.Id);

                                            MyOrderEvent?.Invoke(needNumberUser.Key, GetPortfolioName(), ev);
                                        }
                                    }

                                    //SendLogMessage("Пришла информацияпо ордеру", LogMessageType.System);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.PrivateTradeChannelSubscribed)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    //SendLogMessage("Успешная подписка на мои сделки", LogMessageType.System);
                                }
                            }
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.PrivateTradeNotify)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    PrivateTradeNotification message = ProtoBuf.Serializer.Deserialize<PrivateTradeNotification>(messageStream);

                                    //SendLogMessage("Пришла моя сделка", LogMessageType.System);

                                    foreach (var t in message.Datas)
                                    {
                                        if (!_myOrders.ContainsValue(t.OrderBuyId))
                                        {
                                            _queueMyTradeEvents.Add(t);
                                        }
                                        else
                                        {
                                            MyTradeEvent?.Invoke(t.OrderBuyId.ToString(), t);
                                        }


                                        if (!_myOrders.ContainsValue(t.OrderSellId))
                                        {
                                            _queueMyTradeEvents.Add(t);
                                        }
                                        else
                                        {
                                            MyTradeEvent?.Invoke(t.OrderSellId.ToString(), t);
                                        }
                                    }

                                }
                            }//PUT_LIMIT_ORDER_RESPONSE
                            else if (response.Meta.ResponseType == WsResponseMetaData.WsResponseMsgType.PutLimitOrderResponse)
                            {
                                using (MemoryStream messageStream = new MemoryStream(response.Msg))
                                {
                                    PutLimitOrderResponse message = ProtoBuf.Serializer.Deserialize<PutLimitOrderResponse>(messageStream);

                                    var orderData = response.Meta.Token.Split('_');

                                    int id = Convert.ToInt32(orderData[1]);

                                    _myOrders.Add(id, message.OrderId);

                                    HandleAllNeedEvents(message.OrderId);
                                }
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }

                catch (Exception exception)
                {
                    SendLogMessage(exception.Message, LogMessageType.Error);
                }
            }
        }

        private void HandleAllNeedEvents(long id)
        {
            foreach (var ev in _orderEvents)
            {
                if (ev.Id == id)
                {
                    var needNumberUser = _myOrders.First(o => o.Value == id);

                    MyOrderEvent?.Invoke(needNumberUser.Key, GetPortfolioName(), ev);

                    _handledOrderEvents.Add(ev);
                }
            }

            foreach (var tr in _queueMyTradeEvents)
            {
                if (tr.OrderBuyId == id)
                {
                    MyTradeEvent?.Invoke(tr.OrderBuyId.ToString(), tr);

                    _handledMyTradeEvents.Add(tr);
                }
                if (tr.OrderSellId == id)
                {
                    MyTradeEvent?.Invoke(tr.OrderSellId.ToString(), tr);

                    _handledMyTradeEvents.Add(tr);
                }
            }

            CheckAndDelOrders();
            CheckAndDelTrades();
        }

        private void CheckAndDelOrders()
        {
            if (_orderEvents.Count > 3)
            {
                foreach (var privateOrderRawEvent in _handledOrderEvents)
                {
                    _orderEvents.Remove(privateOrderRawEvent);
                }
                _handledOrderEvents = new List<PrivateOrderRawEvent>();
            }


        }

        private void CheckAndDelTrades()
        {
            if (_queueMyTradeEvents.Count > 3)
            {
                foreach (var handledMyTradeEvent in _handledMyTradeEvents)
                {
                    _queueMyTradeEvents.Remove(handledMyTradeEvent);
                }
                _handledMyTradeEvents = new List<PrivateTradeEvent>();
            }


        }

        private Dictionary<int, long> _myOrders = new Dictionary<int, long>();

        private List<PrivateOrderRawEvent> _orderEvents = new List<PrivateOrderRawEvent>();

        private List<PrivateOrderRawEvent> _handledOrderEvents = new List<PrivateOrderRawEvent>();

        private List<PrivateTradeEvent> _queueMyTradeEvents = new List<PrivateTradeEvent>();

        private List<PrivateTradeEvent> _handledMyTradeEvents = new List<PrivateTradeEvent>();

        #region outgoing events / исходящие события

        /// <summary>
        /// my new orders
        /// новые мои ордера
        /// </summary>
        public event Action<int, string, PrivateOrderRawEvent> MyOrderEvent;

        /// <summary>
        /// my new trades
        /// новые мои сделки
        /// </summary>
        public event Action<string, PrivateTradeEvent> MyTradeEvent;

        /// <summary>
        /// new portfolios
        /// новые портфели
        /// </summary>
        public event Action<BalanceInfo> NewPortfolio;

        /// <summary>
        /// portfolios updated
        /// обновились портфели
        /// </summary>
        public event Action<string, PrivateChangeBalanceNotification> UpdatePortfolio;

        /// <summary>
        /// new security in the system
        /// новые бумаги в системе
        /// </summary>
        public event Action<RestrictionSecurities> UpdatePairs;

        /// <summary>
        /// new depth
        /// новый стакан
        /// </summary>
        public event Action<OrderBookChannelSubscribedResponse> NewMarketDepth;

        /// <summary>
        /// depth updated
        /// обновился стакан
        /// </summary>
        public event Action<OrderBookNotification> UpdateMarketDepth;

        /// <summary>
        /// ticks updated
        /// обновились тики
        /// </summary>
        public event Action<TradeNotification> NewTradesEvent;

        /// <summary>
        /// API connection established
        /// соединение с API установлено
        /// </summary>
        public event Action Connected;

        /// <summary>
        /// API connection lost
        /// соединение с API разорвано
        /// </summary>
        public event Action Disconnected;

        #endregion

        #region log messages / сообщения для лога

        /// <summary>
        /// add a new log message
        /// добавить в лог новое сообщение
        /// </summary>
        private void SendLogMessage(string message, LogMessageType type)
        {
            if (LogMessageEvent != null)
            {
                LogMessageEvent(message, type);
            }
        }

        /// <summary>
        /// send exeptions
        /// отправляет исключения
        /// </summary>
        public event Action<string, LogMessageType> LogMessageEvent;

        #endregion
    }
}
