using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Game
{
    /// <summary>Debug-mode NDJSON sink for music 叠播 hypotheses (session 7cfcba).</summary>
    static class MusicOverlapDebugLog
    {
        const string SessionId = "7cfcba";
        const string IngestUrl =
            "http://127.0.0.1:7904/ingest/fbebcf98-b429-4e2b-9451-7a7b3c8d1d4e";
        const string HostLogPath =
            "/Users/sika/Desktop/code/BoluoProjectNew/.cursor/debug-7cfcba.log";

        // #region agent log
        public static void Write(string hypothesisId, string location, string message, string dataJson = "{}")
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string line =
                "{\"sessionId\":\"" + SessionId
                + "\",\"hypothesisId\":\"" + Escape(hypothesisId)
                + "\",\"location\":\"" + Escape(location)
                + "\",\"message\":\"" + Escape(message)
                + "\",\"data\":" + (string.IsNullOrEmpty(dataJson) ? "{}" : dataJson)
                + ",\"timestamp\":" + ts
                + ",\"runId\":\"pre-fix\"}";
            try
            {
                Debug.Log("[DBG7cfcba] " + line);
            }
            catch
            {
                // ignore
            }

            try
            {
#if UNITY_EDITOR
                AppendFile(HostLogPath, line);
#else
                string path = Path.Combine(Application.persistentDataPath, "debug-7cfcba.log");
                AppendFile(path, line);
#endif
            }
            catch
            {
                // ignore
            }

            try
            {
                PostIngest(line);
            }
            catch
            {
                // ignore
            }
        }

        static void AppendFile(string path, string line)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.AppendAllText(path, line + "\n", Encoding.UTF8);
        }

        static void PostIngest(string line)
        {
            var req = new UnityEngine.Networking.UnityWebRequest(IngestUrl, "POST");
            byte[] body = Encoding.UTF8.GetBytes(line);
            req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body);
            req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("X-Debug-Session-Id", SessionId);
            req.SendWebRequest();
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }
        // #endregion
    }
}
