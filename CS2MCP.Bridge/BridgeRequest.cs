using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace CS2MCP
{
    /// <summary>
    /// A parsed HTTP request handed from a listener thread to the simulation
    /// thread. The listener thread blocks on WaitForResponse until the
    /// simulation thread calls Complete.
    /// </summary>
    public sealed class BridgeRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body;
        public string Host;
        public string Origin;
        public string Token;

        private readonly TaskCompletionSource<BridgeResponse> m_Completion =
            new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryGetInt(string key, out int value)
        {
            value = 0;
            return Query.TryGetValue(key, out string raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetFloat(string key, out float value)
        {
            value = 0f;
            return Query.TryGetValue(key, out string raw)
                && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetBool(string key, out bool value)
        {
            value = false;
            return Query.TryGetValue(key, out string raw) && bool.TryParse(raw, out value);
        }

        public void Complete(BridgeResponse response)
        {
            m_Completion.TrySetResult(response);
        }

        /// <summary>Returns null on timeout.</summary>
        public BridgeResponse WaitForResponse(int timeoutMs)
        {
            return m_Completion.Task.Wait(timeoutMs) ? m_Completion.Task.Result : null;
        }
    }

    public sealed class BridgeResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = Array.Empty<byte>();

        public static BridgeResponse Json(object payload, int status = 200)
        {
            return new BridgeResponse
            {
                Status = status,
                Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload, Formatting.None)),
            };
        }

        public static BridgeResponse Error(int status, string message)
        {
            return Json(new { error = message }, status);
        }

        public static BridgeResponse Png(byte[] png)
        {
            return new BridgeResponse { ContentType = "image/png", Body = png };
        }
    }
}
