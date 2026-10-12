using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace HonQueueNotifier
{
    // =====================================================================================
    // Phone push (ntfy)
    // =====================================================================================
    sealed class PushRequest
    {
        public string Url;
        public string ContentType;
        public string Body;
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
    }

    static class Pusher
    {
        static bool _tlsReady;

        static bool Blank(string s) { return s == null || s.Trim().Length == 0; }

        static string Ascii(string s)
        {
            StringBuilder b = new StringBuilder(s.Length);
            foreach (char c in s) b.Append(c >= 32 && c < 127 ? c : '?');
            return b.ToString();
        }

        public static PushRequest Build(Settings s, string title, string message, out string error)
        {
            error = null;
            string provider = (s.PhoneProvider ?? "").Trim().ToLowerInvariant();
            PushRequest r = new PushRequest();
            if (provider == "ntfy")
            {
                if (Blank(s.NtfyTopic))
                {
                    error = "NtfyTopic must be set";
                    return null;
                }
                string server = Blank(s.NtfyServer) ? "https://ntfy.sh" : s.NtfyServer.Trim().TrimEnd('/');
                r.Url = server + "/" + Uri.EscapeDataString(s.NtfyTopic.Trim());
                r.ContentType = "text/plain; charset=utf-8";
                r.Body = message;
                r.Headers.Add(new KeyValuePair<string, string>("Title", Ascii(title)));
                if (!Blank(s.NtfyPriority)) r.Headers.Add(new KeyValuePair<string, string>("Priority", Ascii(s.NtfyPriority.Trim())));
                r.Headers.Add(new KeyValuePair<string, string>("Tags", "video_game"));
                return r;
            }
            error = "Provider is '" + s.PhoneProvider + "' (use ntfy)";
            return null;
        }

        static void EnsureTls()
        {
            if (_tlsReady) return;
            _tlsReady = true;
            try
            {
                // ntfy.sh needs TLS 1.2. A program for .NET Framework 4.8 lets Windows choose (0, which
                // includes TLS 1.2); this only matters where .NET is set to a fixed, older list.
                SecurityProtocolType current = ServicePointManager.SecurityProtocol;
                if ((int)current != 0) ServicePointManager.SecurityProtocol = current | (SecurityProtocolType)3072;
            }
            catch (Exception) { }
        }

        public static string Send(PushRequest r)
        {
            EnsureTls();
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(r.Url);
            req.Method = "POST";
            req.Timeout = 10000;
            req.ReadWriteTimeout = 10000;
            req.UserAgent = "HonQueueNotifier/" + Program.Version;
            req.ContentType = r.ContentType;
            req.KeepAlive = false;
            for (int i = 0; i < r.Headers.Count; i++) req.Headers[r.Headers[i].Key] = r.Headers[i].Value;
            byte[] body = Encoding.UTF8.GetBytes(r.Body ?? "");
            req.ContentLength = body.Length;
            using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                return ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + resp.StatusDescription;
        }

        static string Describe(WebException ex, out bool transient)
        {
            HttpWebResponse resp = ex.Response as HttpWebResponse;
            if (resp == null) { transient = true; return ex.Status + ": " + ex.Message; }
            int code = (int)resp.StatusCode;
            transient = code >= 500;
            string detail = "";
            try
            {
                using (Stream s = resp.GetResponseStream())
                using (StreamReader rd = new StreamReader(s, Encoding.UTF8))
                {
                    detail = rd.ReadToEnd();
                    if (detail.Length > 300) detail = detail.Substring(0, 300);
                }
            }
            catch (Exception) { }
            return code.ToString(CultureInfo.InvariantCulture) + " " + resp.StatusDescription + (detail.Length > 0 ? " - " + detail : "");
        }

        // The first web request a .NET program makes can stall for seconds while Windows looks for a proxy.
        // Doing that lookup at start-up, in the background, keeps it from delaying the first real push.
        public static void WarmUp(Settings s)
        {
            string error;
            PushRequest req = Build(s, "warm-up", "warm-up", out error);
            if (req == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    EnsureTls();
                    IWebProxy proxy = WebRequest.DefaultWebProxy;
                    if (proxy != null) proxy.GetProxy(new Uri(req.Url));
                }
                catch (Exception) { }
            });
        }

        // Sends on a worker thread. A rejected request (4xx) is not retried; no answer or a 5xx is, twice.
        public static void SendAsync(Settings s, string title, string message, Action<bool, string> done)
        {
            string error;
            PushRequest req = Build(s, title, message, out error);
            if (req == null)
            {
                AppLog.Write("phone push skipped: {0}", error);
                if (done != null) done(false, error);
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                string result = "";
                for (int attempt = 1; attempt <= 3 && !ok; attempt++)
                {
                    try
                    {
                        result = Send(req);
                        ok = true;
                    }
                    catch (WebException ex)
                    {
                        bool transient;
                        result = Describe(ex, out transient);
                        if (!transient || attempt == 3) break;
                        Thread.Sleep(5000);
                    }
                    catch (Exception ex)
                    {
                        result = ex.Message;
                        break;
                    }
                }
                AppLog.Write("phone push \"{0}\" {1}: {2}", title, ok ? "sent" : "FAILED", result);
                if (done != null) { try { done(ok, result); } catch (Exception) { } }
            });
        }
    }
}
