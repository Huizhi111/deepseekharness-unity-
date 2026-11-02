using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityAgentBridge
{
    /// <summary>Minimal HTTP/1.1 request parsed from a socket stream (one request per connection).</summary>
    public sealed class HttpRequest
    {
        const int MaxHeaderBytes = 128 * 1024;

        public string Method = "GET";
        public string Target = "/";
        public string Path = "/";
        public string Query = "";
        public readonly Dictionary<string, string> Headers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string BodyText
        {
            get { return Body.Length == 0 ? "" : Encoding.UTF8.GetString(Body); }
        }

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public string QueryValue(string name)
        {
            if (string.IsNullOrEmpty(Query)) return null;
            string[] pairs = Query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pair = pairs[i];
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? "" : pair.Substring(eq + 1);
                if (Uri.UnescapeDataString(key.Replace("+", " ")) == name)
                    return Uri.UnescapeDataString(value.Replace("+", " "));
            }
            return null;
        }

        /// <summary>Reads one request. Returns null when the peer closed the connection.</summary>
        public static HttpRequest Read(Stream stream)
        {
            var head = new MemoryStream();
            var buffer = new byte[8192];
            int headerEnd = -1;

            while (headerEnd < 0)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return null;
                head.Write(buffer, 0, read);
                headerEnd = FindHeaderEnd(head);
                if (headerEnd < 0 && head.Length > MaxHeaderBytes)
                    throw new InvalidOperationException("请求头超过 " + MaxHeaderBytes + " 字节");
            }

            byte[] all = head.ToArray();
            string headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
            int bodyStart = headerEnd + 4;

            var request = new HttpRequest();
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);

            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length >= 1) request.Method = requestLine[0].ToUpperInvariant();
            if (requestLine.Length >= 2)
            {
                request.Target = requestLine[1];
                int q = request.Target.IndexOf('?');
                if (q >= 0)
                {
                    request.Path = request.Target.Substring(0, q);
                    request.Query = request.Target.Substring(q + 1);
                }
                else
                {
                    request.Path = request.Target;
                }
            }

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrEmpty(line)) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                request.Headers[key] = value;
            }

            int contentLength = 0;
            string lengthHeader = request.Header("Content-Length");
            if (!string.IsNullOrEmpty(lengthHeader)) int.TryParse(lengthHeader, out contentLength);
            if (contentLength < 0) contentLength = 0;
            if (contentLength > 32 * 1024 * 1024)
                throw new InvalidOperationException("请求体超过 32MB 上限");

            // Note: clients that send "Expect: 100-continue" (Windows PowerShell's
            // Invoke-WebRequest does) fall back to sending the body after their own short
            // timeout, so the body is read directly here. Our own clients disable the
            // expectation, which keeps requests instant.

            if (contentLength > 0)
            {
                var body = new byte[contentLength];
                int available = all.Length - bodyStart;
                int copied = Math.Min(Math.Max(available, 0), contentLength);
                if (copied > 0) Buffer.BlockCopy(all, bodyStart, body, 0, copied);

                int total = copied;
                while (total < contentLength)
                {
                    int read = stream.Read(body, total, contentLength - total);
                    if (read <= 0) break;
                    total += read;
                }
                request.Body = body;
            }

            return request;
        }

        static int FindHeaderEnd(MemoryStream head)
        {
            byte[] data = head.GetBuffer();
            int length = (int)head.Length;
            for (int i = Math.Max(0, length - 4); i < length - 3; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }
            return -1;
        }
    }

    public sealed class HttpResponse
    {
        public int Status = 200;
        public string StatusText = "OK";
        public string ContentType = "application/json; charset=utf-8";
        public string Body = "{}";

        public static HttpResponse Json(string body, int status = 200)
        {
            return new HttpResponse
            {
                Status = status,
                StatusText = StatusTextFor(status),
                ContentType = "application/json; charset=utf-8",
                Body = body
            };
        }

        public static HttpResponse Text(string body, int status = 200)
        {
            return new HttpResponse
            {
                Status = status,
                StatusText = StatusTextFor(status),
                ContentType = "text/plain; charset=utf-8",
                Body = body
            };
        }

        static string StatusTextFor(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 413: return "Payload Too Large";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "OK";
            }
        }
    }
}
