using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DeviceGuard
{
    class ObsException : Exception
    {
        public readonly int Code;
        public ObsException(string msg, int code) : base(msg) { Code = code; }
    }

    // A minimal obs-websocket v5 client (protocol: Hello → Identify → Identified, then Request/Event)
    class ObsClient : IDisposable
    {
        // General (ExitStarted) + Outputs (ReplayBufferStateChanged/Saved, RecordStateChanged/FileChanged) +
        // Ui (ScreenshotSaved) + InputVolumeMeters (audio levels, ~20 times/s)
        const int EventSubs = 1 | 64 | 1024 | 65536;

        ClientWebSocket ws;
        Thread rx;
        volatile bool open;
        int nextId;
        readonly object sendLock = new object();
        readonly object pendLock = new object();
        readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>();

        public event Action<string, Dictionary<string, object>> EventReceived;

        class Pending
        {
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public Dictionary<string, object> Msg;
        }

        public bool IsOpen { get { return open && ws != null && ws.State == WebSocketState.Open; } }

        public static ObsClient Connect(string host, int port, string password, out string error, out bool authFailed)
        {
            error = null;
            authFailed = false;
            var c = new ObsClient();
            try
            {
                c.ws = new ClientWebSocket();
                c.ws.Options.AddSubProtocol("obswebsocket.json");
                c.ws.Options.Proxy = null;
                using (var cts = new CancellationTokenSource(5000))
                    c.ws.ConnectAsync(new Uri("ws://" + host + ":" + port + "/"), cts.Token).Wait();

                var hello = c.ReceiveJson(5000);
                if (hello == null || Json.GetInt(hello, "op", -1) != 0) throw new IOException(L.T("OBS did not send Hello", "OBS не прислал Hello"));
                var hd = Json.GetObj(hello, "d");
                var identify = Json.D("rpcVersion", 1, "eventSubscriptions", EventSubs);
                var auth = Json.GetObj(hd, "authentication");
                if (auth != null)
                {
                    if (string.IsNullOrEmpty(password))
                    {
                        authFailed = true;
                        error = L.T("OBS requires the WebSocket password", "OBS требует пароль WebSocket");
                        c.Dispose();
                        return null;
                    }
                    identify["authentication"] = AuthString(password, Json.GetStr(auth, "salt"),
                                                            Json.GetStr(auth, "challenge"));
                }
                c.SendJson(Json.D("op", 1, "d", identify), 5000);

                var ident = c.ReceiveJson(5000);
                if (ident == null)
                {
                    if (c.ws.CloseStatus.HasValue && (int)c.ws.CloseStatus.Value == 4009)
                    {
                        authFailed = true;
                        error = L.T("wrong WebSocket password", "неверный пароль WebSocket");
                    }
                    else error = L.T("OBS closed the connection ", "OBS закрыл соединение ") + (c.ws.CloseStatusDescription ?? "");
                    c.Dispose();
                    return null;
                }
                if (Json.GetInt(ident, "op", -1) != 2) throw new IOException(L.T("unexpected OBS response", "неожиданный ответ OBS"));

                c.open = true;
                c.rx = new Thread(c.ReceiveLoop) { IsBackground = true, Name = "obs-ws-rx" };
                c.rx.Start();
                return c;
            }
            catch (Exception ex)
            {
                error = Flatten(ex);
                c.Dispose();
                return null;
            }
        }

        public static string AuthString(string password, string salt, string challenge)
        {
            using (var sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        public static string Flatten(Exception ex)
        {
            while (ex is AggregateException && ex.InnerException != null) ex = ex.InnerException;
            while (ex.InnerException != null && ex is WebSocketException) ex = ex.InnerException;
            return ex.Message;
        }

        // Throws TimeoutException (OBS did not answer), IOException (no link), ObsException (OBS returned an error)
        public Dictionary<string, object> Request(string type, Dictionary<string, object> data, int timeoutMs)
        {
            if (!IsOpen) throw new IOException(L.T("no connection to OBS", "нет соединения с OBS"));
            string id = Interlocked.Increment(ref nextId).ToString();
            var p = new Pending();
            lock (pendLock) pending[id] = p;
            var d = Json.D("requestType", type, "requestId", id);
            if (data != null) d["requestData"] = data;
            try { SendJson(Json.D("op", 6, "d", d), timeoutMs); }
            catch (Exception ex)
            {
                lock (pendLock) pending.Remove(id);
                throw new IOException(L.T("request to OBS not sent: ", "запрос в OBS не отправлен: ") + Flatten(ex));
            }
            if (!p.Done.Wait(timeoutMs))
            {
                lock (pendLock) pending.Remove(id);
                throw new TimeoutException(L.T("OBS did not answer ", "OBS не ответил на ") + type);
            }
            if (p.Msg == null) throw new IOException(L.T("the connection to OBS is closed", "соединение с OBS закрыто"));
            var st = Json.GetObj(p.Msg, "requestStatus");
            if (!Json.GetBool(st, "result", false))
                throw new ObsException(type + ": " + (Json.GetStr(st, "comment") ?? L.T("error", "ошибка")), Json.GetInt(st, "code", 0));
            return Json.GetObj(p.Msg, "responseData") ?? new Dictionary<string, object>();
        }

        void SendJson(Dictionary<string, object> msg, int timeoutMs)
        {
            var bytes = Encoding.UTF8.GetBytes(Json.Write(msg, false));
            lock (sendLock)
                using (var cts = new CancellationTokenSource(timeoutMs))
                    ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token).Wait();
        }

        Dictionary<string, object> ReceiveJson(int timeoutMs)
        {
            string s = ReceiveText(timeoutMs);
            return s == null ? null : Json.Obj(Json.Parse(s));
        }

        string ReceiveText(int timeoutMs)
        {
            var buf = new byte[65536];
            using (var ms = new MemoryStream())
            using (var cts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : new CancellationTokenSource())
            {
                while (true)
                {
                    var t = ws.ReceiveAsync(new ArraySegment<byte>(buf), cts.Token);
                    t.Wait();
                    var r = t.Result;
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    ms.Write(buf, 0, r.Count);
                    if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        void ReceiveLoop()
        {
            try
            {
                while (true)
                {
                    string s = ReceiveText(0);
                    if (s == null) break;
                    Handle(s);
                }
            }
            catch { }
            open = false;
            lock (pendLock)
            {
                foreach (var p in pending.Values) p.Done.Set();
                pending.Clear();
            }
        }

        void Handle(string s)
        {
            Dictionary<string, object> m;
            try { m = Json.Obj(Json.Parse(s)); } catch { return; }
            if (m == null) return;
            int op = Json.GetInt(m, "op", -1);
            var d = Json.GetObj(m, "d");
            if (op == 7)
            {
                string id = Json.GetStr(d, "requestId");
                Pending p = null;
                lock (pendLock)
                    if (id != null && pending.TryGetValue(id, out p)) pending.Remove(id);
                if (p != null) { p.Msg = d; p.Done.Set(); }
            }
            else if (op == 5)
            {
                var h = EventReceived;
                if (h != null)
                    try { h(Json.GetStr(d, "eventType") ?? "", Json.GetObj(d, "eventData") ?? new Dictionary<string, object>()); }
                    catch (Exception ex) { Log.Write("OBS event " + (Json.GetStr(d, "eventType") ?? "?") + ": " + ex.Message); }
            }
        }

        public void Dispose()
        {
            open = false;
            if (ws == null) return;
            try { ws.Abort(); } catch { }
            try { ws.Dispose(); } catch { }
        }
    }
}
