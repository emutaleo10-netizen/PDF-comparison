using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PDFDiffStudio
{
    public class ProgressState
    {
        public string Stage = "";
        public string Message = "";
        public double? Percent = null;
        public string Timestamp = "";
        public string ServerPath = "";
        public string LocalPath = "";
    }

    public class DataClasysNativeManager
    {
        public string AppId = "ks_project_tool";
        public string AppDisplayName = "KS工程管理ツール";
        public string VendorName = "KinkiSharyo";
        public string ClsuClientPath = @"C:\Program Files\DataClasys\UserClient\CLSUClient.exe";
        public int DecryptTimeoutSec = 120;
        public int DecryptPollIntervalMs = 500;
        public bool AllowPlainOpenWithoutClient = true;
        public bool DirectOpenIfPlain = true;

        private readonly object _sync = new object();
        public ProgressState LatestProgress = new ProgressState();

        public bool IsDataClasysAvailable()
        {
            return File.Exists(ClsuClientPath);
        }

        public string GetAppRoot()
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    string root = Path.Combine(localAppData, VendorName, "Workspaces", AppId);
                    Directory.CreateDirectory(Path.Combine(root, "sessions"));
                    Directory.CreateDirectory(Path.Combine(root, "recovery"));
                    return root;
                }
            }
            catch { }

            string fallbackRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".dataclasys_workspace", AppId);
            Directory.CreateDirectory(Path.Combine(fallbackRoot, "sessions"));
            Directory.CreateDirectory(Path.Combine(fallbackRoot, "recovery"));
            return fallbackRoot;
        }

        public void CleanupStaleSessions(int staleHours)
        {
            try
            {
                string sessionsRoot = Path.Combine(GetAppRoot(), "sessions");
                if (!Directory.Exists(sessionsRoot)) return;
                DateTime cutoff = DateTime.UtcNow.AddHours(-staleHours);
                foreach (string dir in Directory.GetDirectories(sessionsRoot))
                {
                    try
                    {
                        if (Directory.GetLastWriteTimeUtc(dir) <= cutoff)
                        {
                            Directory.Delete(dir, true);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void EmitProgress(string logsDir, string stage, string message, string sessionId, string serverPath, string localPath, double? percent)
        {
            string nowIso = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            lock (_sync)
            {
                LatestProgress = new ProgressState
                {
                    Stage = stage,
                    Message = message,
                    Percent = percent,
                    Timestamp = nowIso,
                    ServerPath = serverPath ?? "",
                    LocalPath = localPath ?? ""
                };
            }
            if (!string.IsNullOrEmpty(logsDir))
            {
                LogLine(Path.Combine(logsDir, "events.log"), stage + ": " + message);
            }
        }

        private static void LogLine(string path, string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, message), Encoding.UTF8);
            }
            catch { }
        }

        public static bool LooksLikePdf(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 5) return false;
                    byte[] header = new byte[5];
                    int read = fs.Read(header, 0, 5);
                    if (read < 5) return false;
                    return header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46 && header[4] == 0x2D; // %PDF-
                }
            }
            catch
            {
                return false;
            }
        }

        private static string QuickHash(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[65536];
                int n = fs.Read(buf, 0, buf.Length);
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(buf, 0, Math.Max(0, n));
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        public byte[] OpenAndReadServerPdf(string serverPath, out string workspaceMode, out string fileName)
        {
            if (!File.Exists(serverPath))
            {
                throw new FileNotFoundException("指定されたファイルが見つかりません: " + serverPath);
            }

            fileName = Path.GetFileName(serverPath);
            string sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            bool directOpen = DirectOpenIfPlain && LooksLikePdf(serverPath);

            if (directOpen)
            {
                EmitProgress(null, "direct_open", "平文PDFのため直接読み込みます", sessionId, serverPath, serverPath, 0.90);
                workspaceMode = "direct";
                byte[] directBytes = File.ReadAllBytes(serverPath);
                EmitProgress(null, "ready", "読み込みが完了しました", sessionId, serverPath, serverPath, 1.0);
                return directBytes;
            }

            string sessionRoot = Path.Combine(GetAppRoot(), "sessions", sessionId);
            string sourceDir = Path.Combine(sessionRoot, "source");
            string workDir = Path.Combine(sessionRoot, "work");
            string logsDir = Path.Combine(sessionRoot, "logs");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(logsDir);

            try
            {
                EmitProgress(logsDir, "prepare", "サーバーファイルを準備しています", sessionId, serverPath, "", 0.05);

                string sourceFile = Path.Combine(sourceDir, fileName);
                string workFile = Path.Combine(workDir, fileName);

                EmitProgress(logsDir, "copy", "暗号化ファイルをローカルワークスペースへコピーしています", sessionId, serverPath, sourceFile, 0.20);
                File.Copy(serverPath, sourceFile, true);
                File.Copy(sourceFile, workFile, true);

                workspaceMode = "dataclasys";
                if (IsDataClasysAvailable())
                {
                    DecryptFile(workFile, logsDir, sessionId);
                    WaitUntilDecrypted(workFile, logsDir, sessionId);
                }
                else if (AllowPlainOpenWithoutClient)
                {
                    workspaceMode = "plain";
                    EmitProgress(logsDir, "plain_open", "CLSUClient.exeが見つからないため平文として開きます", sessionId, serverPath, workFile, 0.90);
                }
                else
                {
                    throw new InvalidOperationException("CLSUClient.exe が見つかりません: " + ClsuClientPath);
                }

                byte[] resultBytes = File.ReadAllBytes(workFile);
                EmitProgress(logsDir, "ready", "ワークスペースの復号が完了しました", sessionId, serverPath, workFile, 1.0);
                return resultBytes;
            }
            finally
            {
                // 閲覧用バイト配列取得後、ローカルの復号済み一時ファイルを確実に削除
                try
                {
                    if (Directory.Exists(sessionRoot))
                    {
                        Directory.Delete(sessionRoot, true);
                    }
                }
                catch { }
            }
        }

        private void DecryptFile(string targetPath, string logsDir, string sessionId)
        {
            EmitProgress(logsDir, "decrypt", "DataClasys復号コマンド (CLSUClient.exe /d) を実行しています", sessionId, "", targetPath, 0.45);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ClsuClientPath,
                Arguments = "/d \"" + targetPath + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            Stopwatch sw = Stopwatch.StartNew();
            using (Process proc = Process.Start(psi))
            {
                bool exited = proc.WaitForExit(DecryptTimeoutSec * 1000);
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    throw new TimeoutException("DataClasys復号コマンドがタイムアウトしました: " + targetPath);
                }
                sw.Stop();
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();

                LogLine(Path.Combine(logsDir, "decrypt_command.log"), "COMMAND: " + psi.FileName + " " + psi.Arguments);
                LogLine(Path.Combine(logsDir, "decrypt_command.log"), "RETURN CODE: " + proc.ExitCode);
                if (!string.IsNullOrEmpty(stdout)) LogLine(Path.Combine(logsDir, "decrypt_stdout.log"), stdout);
                if (!string.IsNullOrEmpty(stderr)) LogLine(Path.Combine(logsDir, "decrypt_stderr.log"), stderr);
                LogLine(Path.Combine(logsDir, "events.log"), string.Format("Decrypt command finished in {0:F2}s", sw.Elapsed.TotalSeconds));

                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException(string.Format("DataClasys復号に失敗しました (rc={0}): {1}", proc.ExitCode, stderr.Trim()));
                }
            }
        }

        private void WaitUntilDecrypted(string path, string logsDir, string sessionId)
        {
            EmitProgress(logsDir, "decrypt_wait", "復号ファイルが安定するまで待機しています", sessionId, "", path, 0.70);
            Stopwatch sw = Stopwatch.StartNew();
            int stableCount = 0;
            long? lastSize = null;
            string lastHash = null;

            while (sw.Elapsed.TotalSeconds < DecryptTimeoutSec)
            {
                if (!File.Exists(path))
                {
                    Thread.Sleep(DecryptPollIntervalMs);
                    continue;
                }
                long currentSize;
                string currentHash;
                try
                {
                    currentSize = new FileInfo(path).Length;
                    currentHash = QuickHash(path);
                }
                catch
                {
                    Thread.Sleep(DecryptPollIntervalMs);
                    continue;
                }

                if (lastSize.HasValue && currentSize == lastSize.Value && currentHash == lastHash)
                {
                    stableCount++;
                }
                else
                {
                    stableCount = 0;
                }

                if (stableCount >= 1 && LooksLikePdf(path))
                {
                    EmitProgress(logsDir, "decrypt_ready", "復号ファイルの準備が完了しました", sessionId, "", path, 0.90);
                    return;
                }

                lastSize = currentSize;
                lastHash = currentHash;
                Thread.Sleep(DecryptPollIntervalMs);
            }

            throw new TimeoutException("復号完了状態の待機がタイムアウトしました: " + path);
        }
    }

    internal static class Program
    {
        private static readonly DataClasysNativeManager Manager = new DataClasysNativeManager();
        private static DateTime LastHeartbeat = DateTime.UtcNow;
        private static volatile bool Running = true;
        private static TcpListener Listener;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Manager.CleanupStaleSessions(24);

            bool selfTest = false;
            foreach (string a in args)
            {
                if (a == "--self-test") selfTest = true;
            }

            if (selfTest)
            {
                try
                {
                    byte[] htmlBytes = LoadIndexHtmlBytes();
                    if (htmlBytes == null || htmlBytes.Length < 1000) return 2;

                    string testDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_selftest_tmp");
                    Directory.CreateDirectory(testDir);
                    try
                    {
                        string dummyPlain = Path.Combine(testDir, "plain.pdf");
                        File.WriteAllBytes(dummyPlain, Encoding.ASCII.GetBytes("%PDF-1.4 dummy"));
                        string mode1, name1;
                        byte[] res1 = Manager.OpenAndReadServerPdf(dummyPlain, out mode1, out name1);
                        if (mode1 != "direct" || res1.Length < 5) return 3;

                        string dummyEnc = Path.Combine(testDir, "encrypted.pdf");
                        File.WriteAllBytes(dummyEnc, Encoding.ASCII.GetBytes("ENCRYPTED_BINARY_HEADER"));
                        string mode2, name2;
                        byte[] res2 = Manager.OpenAndReadServerPdf(dummyEnc, out mode2, out name2);
                        if (res2.Length < 5) return 4;
                    }
                    finally
                    {
                        try { Directory.Delete(testDir, true); } catch { }
                    }
                    return 0;
                }
                catch
                {
                    return 1;
                }
            }

            Listener = new TcpListener(IPAddress.Loopback, 0);
            Listener.Start();
            int port = ((IPEndPoint)Listener.LocalEndpoint).Port;
            string url = "http://127.0.0.1:" + port + "/";

            Thread serverThread = new Thread(ServerLoop) { IsBackground = true };
            serverThread.Start();

            LaunchAppWindow(url);

            // クライアントからのハートビートまたは終了通知を監視
            DateTime started = DateTime.UtcNow;
            while (Running)
            {
                Thread.Sleep(1000);
                double sinceStart = (DateTime.UtcNow - started).TotalSeconds;
                double sinceBeat = (DateTime.UtcNow - LastHeartbeat).TotalSeconds;
                if (sinceStart > 25 && sinceBeat > 20)
                {
                    // ブラウザウィンドウが閉じられたと判断して終了
                    break;
                }
            }

            try { Listener.Stop(); } catch { }
            return 0;
        }

        private static void LaunchAppWindow(string url)
        {
            string[] edgeCandidates = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"
            };

            foreach (string edge in edgeCandidates)
            {
                if (File.Exists(edge))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = edge,
                            Arguments = "--app=\"" + url + "\" --window-size=1500,940",
                            UseShellExecute = false
                        });
                        return;
                    }
                    catch { }
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private static byte[] LoadIndexHtmlBytes()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string externalHtml = Path.Combine(exeDir, "index.html");
            if (File.Exists(externalHtml))
            {
                return File.ReadAllBytes(externalHtml);
            }

            Assembly asm = Assembly.GetExecutingAssembly();
            using (Stream s = asm.GetManifestResourceStream("index.html"))
            {
                if (s != null)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            return Encoding.UTF8.GetBytes("<h1>index.html not found</h1>");
        }

        private static void ServerLoop()
        {
            while (Running)
            {
                try
                {
                    TcpClient client = Listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch
                {
                    if (!Running) break;
                }
            }
        }

        private static void HandleClient(object state)
        {
            using (TcpClient client = (TcpClient)state)
            using (NetworkStream ns = client.GetStream())
            {
                ns.ReadTimeout = 30000;
                ns.WriteTimeout = 30000;
                try
                {
                    string requestLine = ReadLineAscii(ns);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    string[] parts = requestLine.Split(' ');
                    if (parts.Length < 2) return;
                    string method = parts[0].ToUpperInvariant();
                    string rawTarget = parts[1];

                    int contentLength = 0;
                    while (true)
                    {
                        string headerLine = ReadLineAscii(ns);
                        if (string.IsNullOrEmpty(headerLine)) break;
                        int colon = headerLine.IndexOf(':');
                        if (colon > 0)
                        {
                            string hName = headerLine.Substring(0, colon).Trim();
                            string hVal = headerLine.Substring(colon + 1).Trim();
                            if (hName.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                            {
                                int.TryParse(hVal, out contentLength);
                            }
                        }
                    }

                    byte[] bodyBytes = new byte[contentLength];
                    int readTotal = 0;
                    while (readTotal < contentLength)
                    {
                        int n = ns.Read(bodyBytes, readTotal, contentLength - readTotal);
                        if (n <= 0) break;
                        readTotal += n;
                    }

                    string pathOnly = rawTarget;
                    string query = "";
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        pathOnly = rawTarget.Substring(0, qIdx);
                        query = rawTarget.Substring(qIdx + 1);
                    }

                    LastHeartbeat = DateTime.UtcNow;

                    if (method == "GET" && (pathOnly == "/" || pathOnly == "/index.html"))
                    {
                        byte[] html = LoadIndexHtmlBytes();
                        WriteResponse(ns, "200 OK", "text/html; charset=utf-8", html);
                        return;
                    }

                    if (method == "GET" && pathOnly == "/api/status")
                    {
                        string json = "{"
                            + "\"ok\":true,"
                            + "\"dataclasys_available\":" + (Manager.IsDataClasysAvailable() ? "true" : "false") + ","
                            + "\"clsu_client_path\":" + JsonQuote(Manager.ClsuClientPath) + ","
                            + "\"app_id\":" + JsonQuote(Manager.AppId)
                            + "}";
                        WriteJson(ns, "200 OK", json);
                        return;
                    }

                    if (method == "GET" && pathOnly == "/api/progress")
                    {
                        ProgressState p = Manager.LatestProgress;
                        string pctStr = p.Percent.HasValue ? p.Percent.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null";
                        string json = "{"
                            + "\"stage\":" + JsonQuote(p.Stage) + ","
                            + "\"message\":" + JsonQuote(p.Message) + ","
                            + "\"percent\":" + pctStr + ","
                            + "\"timestamp\":" + JsonQuote(p.Timestamp) + ","
                            + "\"server_path\":" + JsonQuote(p.ServerPath) + ","
                            + "\"local_path\":" + JsonQuote(p.LocalPath)
                            + "}";
                        WriteJson(ns, "200 OK", json);
                        return;
                    }

                    if (method == "POST" && pathOnly == "/api/heartbeat")
                    {
                        WriteJson(ns, "200 OK", "{\"ok\":true}");
                        return;
                    }

                    if (method == "POST" && pathOnly == "/api/shutdown")
                    {
                        WriteJson(ns, "200 OK", "{\"ok\":true}");
                        Running = false;
                        return;
                    }

                    if (method == "POST" && pathOnly == "/api/select-file")
                    {
                        string kind = GetQueryParam(query, "kind") ?? "old";
                        string title = kind == "old" ? "旧版PDFファイルを選択（DataClasys復号対応）" : "新版PDFファイルを選択（DataClasys復号対応）";
                        string selectedPath = ShowOpenFileDialogSta(title);
                        if (string.IsNullOrEmpty(selectedPath))
                        {
                            WriteJson(ns, "200 OK", "{\"cancelled\":true}");
                            return;
                        }
                        try
                        {
                            string mode, fileName;
                            byte[] pdfBytes = Manager.OpenAndReadServerPdf(selectedPath, out mode, out fileName);
                            string b64 = Convert.ToBase64String(pdfBytes);
                            string json = "{"
                                + "\"file_name\":" + JsonQuote(fileName) + ","
                                + "\"file_size\":" + pdfBytes.Length + ","
                                + "\"workspace_mode\":" + JsonQuote(mode) + ","
                                + "\"server_path\":" + JsonQuote(selectedPath) + ","
                                + "\"pdf_base64\":\"" + b64 + "\""
                                + "}";
                            WriteJson(ns, "200 OK", json);
                        }
                        catch (Exception ex)
                        {
                            WriteJson(ns, "400 Bad Request", "{\"error\":" + JsonQuote(ex.Message) + "}");
                        }
                        return;
                    }

                    if (method == "POST" && pathOnly == "/api/open-path")
                    {
                        string bodyStr = Encoding.UTF8.GetString(bodyBytes, 0, readTotal);
                        string inputPath = ExtractJsonStringField(bodyStr, "path");
                        if (string.IsNullOrEmpty(inputPath))
                        {
                            WriteJson(ns, "400 Bad Request", "{\"error\":\"ファイルパスが空です\"}");
                            return;
                        }
                        inputPath = inputPath.Trim().Trim('"');
                        try
                        {
                            string mode, fileName;
                            byte[] pdfBytes = Manager.OpenAndReadServerPdf(inputPath, out mode, out fileName);
                            string b64 = Convert.ToBase64String(pdfBytes);
                            string json = "{"
                                + "\"file_name\":" + JsonQuote(fileName) + ","
                                + "\"file_size\":" + pdfBytes.Length + ","
                                + "\"workspace_mode\":" + JsonQuote(mode) + ","
                                + "\"server_path\":" + JsonQuote(inputPath) + ","
                                + "\"pdf_base64\":\"" + b64 + "\""
                                + "}";
                            WriteJson(ns, "200 OK", json);
                        }
                        catch (Exception ex)
                        {
                            WriteJson(ns, "400 Bad Request", "{\"error\":" + JsonQuote(ex.Message) + "}");
                        }
                        return;
                    }

                    if (method == "POST" && pathOnly == "/api/decrypt-dropped")
                    {
                        string name = GetQueryParam(query, "name") ?? "dropped.pdf";
                        string safeName = Path.GetFileName(name);
                        if (string.IsNullOrEmpty(safeName)) safeName = "dropped.pdf";
                        if (!safeName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) safeName += ".pdf";

                        string tempDir = Path.Combine(Path.GetTempPath(), "dcl_drop_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                        Directory.CreateDirectory(tempDir);
                        string tempFile = Path.Combine(tempDir, safeName);
                        try
                        {
                            File.WriteAllBytes(tempFile, bodyBytes);
                            string mode, outName;
                            byte[] pdfBytes = Manager.OpenAndReadServerPdf(tempFile, out mode, out outName);
                            string b64 = Convert.ToBase64String(pdfBytes);
                            string json = "{"
                                + "\"file_name\":" + JsonQuote(safeName) + ","
                                + "\"file_size\":" + pdfBytes.Length + ","
                                + "\"workspace_mode\":" + JsonQuote(mode) + ","
                                + "\"server_path\":" + JsonQuote(safeName) + ","
                                + "\"pdf_base64\":\"" + b64 + "\""
                                + "}";
                            WriteJson(ns, "200 OK", json);
                        }
                        catch (Exception ex)
                        {
                            WriteJson(ns, "400 Bad Request", "{\"error\":" + JsonQuote(ex.Message) + "}");
                        }
                        finally
                        {
                            try { Directory.Delete(tempDir, true); } catch { }
                        }
                        return;
                    }

                    WriteJson(ns, "404 Not Found", "{\"error\":\"Not found\"}");
                }
                catch { }
            }
        }

        private static string ShowOpenFileDialogSta(string title)
        {
            string selected = null;
            Thread sta = new Thread(() =>
            {
                using (Form topForm = new Form())
                {
                    topForm.TopMost = true;
                    topForm.ShowInTaskbar = false;
                    topForm.FormBorderStyle = FormBorderStyle.None;
                    topForm.StartPosition = FormStartPosition.Manual;
                    topForm.Location = new System.Drawing.Point(-2000, -2000);
                    topForm.Size = new System.Drawing.Size(1, 1);
                    topForm.Show();
                    topForm.Activate();

                    using (OpenFileDialog ofd = new OpenFileDialog())
                    {
                        ofd.Title = title;
                        ofd.Filter = "PDFファイル (*.pdf)|*.pdf|すべてのファイル (*.*)|*.*";
                        ofd.RestoreDirectory = true;
                        ofd.DereferenceLinks = false;
                        if (ofd.ShowDialog(topForm) == DialogResult.OK)
                        {
                            selected = ofd.FileName;
                        }
                    }
                }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
            return selected;
        }

        private static string ReadLineAscii(NetworkStream ns)
        {
            List<byte> bytes = new List<byte>();
            while (true)
            {
                int b = ns.ReadByte();
                if (b < 0)
                {
                    if (bytes.Count == 0) return null;
                    break;
                }
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static void WriteJson(NetworkStream ns, string status, string json)
        {
            WriteResponse(ns, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        private static void WriteResponse(NetworkStream ns, string status, string contentType, byte[] body)
        {
            string header = "HTTP/1.1 " + status + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + body.Length + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Connection: close\r\n\r\n";
            byte[] hBytes = Encoding.ASCII.GetBytes(header);
            ns.Write(hBytes, 0, hBytes.Length);
            ns.Write(body, 0, body.Length);
            ns.Flush();
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string[] pairs = query.Split('&');
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    string k = Uri.UnescapeDataString(pair.Substring(0, eq));
                    if (k == key)
                    {
                        return Uri.UnescapeDataString(pair.Substring(eq + 1));
                    }
                }
            }
            return null;
        }

        private static string ExtractJsonStringField(string json, string field)
        {
            string pattern = "\"" + field + "\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            int colon = json.IndexOf(':', idx + pattern.Length);
            if (colon < 0) return "";
            int q1 = json.IndexOf('"', colon + 1);
            if (q1 < 0) return "";
            StringBuilder sb = new StringBuilder();
            for (int i = q1 + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[++i];
                    if (next == '"' || next == '\\' || next == '/') sb.Append(next);
                    else if (next == 'n') sb.Append('\n');
                    else if (next == 'r') sb.Append('\r');
                    else if (next == 't') sb.Append('\t');
                    else sb.Append(next);
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string JsonQuote(string s)
        {
            if (s == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }
    }
}
