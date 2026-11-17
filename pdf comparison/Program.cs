using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
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

    public static class DebugLogger
    {
        private static readonly object _lock = new object();
        private static readonly List<string> _entries = new List<string>();
        private static string _logFilePath = null;
        public static Action OnLogAdded;

        public static string LogFilePath
        {
            get
            {
                if (_logFilePath == null)
                {
                    try
                    {
                        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                        _logFilePath = Path.Combine(baseDir, "debug_trace.log");
                    }
                    catch
                    {
                        _logFilePath = Path.Combine(Path.GetTempPath(), "pdf_diff_debug_trace.log");
                    }
                }
                return _logFilePath;
            }
        }

        public static void Log(string category, string message)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}", DateTime.Now, category, message);
            lock (_lock)
            {
                _entries.Add(line);
                if (_entries.Count > 1000)
                {
                    _entries.RemoveAt(0);
                }
                try
                {
                    File.AppendAllText(LogFilePath, line + "\r\n", Encoding.UTF8);
                }
                catch { }
            }
            try
            {
                Action cb = OnLogAdded;
                if (cb != null) cb();
            }
            catch { }
        }

        public static List<string> GetEntries()
        {
            lock (_lock)
            {
                return new List<string>(_entries);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
            }
            try
            {
                Action cb = OnLogAdded;
                if (cb != null) cb();
            }
            catch { }
        }
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
        public Action OnProgressChanged;

        public DataClasysNativeManager()
        {
            if (!File.Exists(ClsuClientPath))
            {
                string x86Candidate = @"C:\Program Files (x86)\DataClasys\UserClient\CLSUClient.exe";
                if (File.Exists(x86Candidate))
                {
                    ClsuClientPath = x86Candidate;
                }
            }
            DebugLogger.Log("INIT", "DataClasysNativeManager initialized. ClsuClientPath=" + ClsuClientPath + " (exists=" + File.Exists(ClsuClientPath) + ")");
        }

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
            catch (Exception ex)
            {
                DebugLogger.Log("WARN", "GetAppRoot LocalApplicationData failed: " + ex.Message);
            }

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
            DebugLogger.Log("STAGE:" + stage, message + (string.IsNullOrEmpty(localPath) ? "" : " [local=" + localPath + "]"));
            if (!string.IsNullOrEmpty(logsDir))
            {
                LogLine(Path.Combine(logsDir, "events.log"), stage + ": " + message);
            }
            try
            {
                Action cb = OnProgressChanged;
                if (cb != null) cb();
            }
            catch { }
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

        public static string InspectHeaderHex(string path, int maxBytes = 16)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int len = (int)Math.Min(maxBytes, fs.Length);
                    byte[] buf = new byte[len];
                    int read = fs.Read(buf, 0, len);
                    StringBuilder hex = new StringBuilder();
                    StringBuilder ascii = new StringBuilder();
                    for (int i = 0; i < read; i++)
                    {
                        if (i > 0) hex.Append(' ');
                        hex.AppendFormat("{0:X2}", buf[i]);
                        char c = (char)buf[i];
                        ascii.Append(c >= 32 && c < 127 ? c : '.');
                    }
                    return string.Format("size={0} bytes, hex=[{1}], ascii=[{2}]", fs.Length, hex.ToString(), ascii.ToString());
                }
            }
            catch (Exception ex)
            {
                return "InspectHeader error: " + ex.Message;
            }
        }

        public static bool LooksLikePdf(string path)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < 5) return false;
                    int checkLen = (int)Math.Min(1024, fs.Length);
                    byte[] buf = new byte[checkLen];
                    int read = fs.Read(buf, 0, checkLen);
                    for (int i = 0; i <= read - 5; i++)
                    {
                        if (buf[i] == 0x25 && buf[i + 1] == 0x50 && buf[i + 2] == 0x44 && buf[i + 3] == 0x46 && buf[i + 4] == 0x2D)
                        {
                            return true; // %PDF-
                        }
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string QuickHash(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
            DebugLogger.Log("OPEN", "OpenAndReadServerPdf called: serverPath=" + serverPath);
            if (!File.Exists(serverPath))
            {
                string msg = "指定されたファイルが見つかりません: " + serverPath;
                DebugLogger.Log("ERROR", msg);
                throw new FileNotFoundException(msg);
            }

            fileName = Path.GetFileName(serverPath);
            string sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string initialHeader = InspectHeaderHex(serverPath);
            DebugLogger.Log("INSPECT", "Source file header: " + initialHeader);

            bool isPlainPdf = LooksLikePdf(serverPath);
            bool directOpen = DirectOpenIfPlain && isPlainPdf;

            if (directOpen)
            {
                EmitProgress(null, "direct_open", "平文PDF (%PDF- 検出) のため直接読み込みます", sessionId, serverPath, serverPath, 0.90);
                workspaceMode = "direct";
                byte[] directBytes = ReadAllBytesShared(serverPath);
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
                EmitProgress(logsDir, "prepare", "ローカルワークスペースを準備しています (" + sessionRoot + ")", sessionId, serverPath, "", 0.05);

                string sourceFile = Path.Combine(sourceDir, fileName);
                string workFile = Path.Combine(workDir, fileName);

                EmitProgress(logsDir, "copy", "暗号化ファイルをローカルワークスペースへコピーしています", sessionId, serverPath, sourceFile, 0.20);
                CopyFileShared(serverPath, sourceFile);
                File.Copy(sourceFile, workFile, true);
                try
                {
                    File.SetAttributes(workFile, FileAttributes.Normal);
                }
                catch (Exception attrEx)
                {
                    DebugLogger.Log("WARN", "SetAttributes failed: " + attrEx.Message);
                }

                DebugLogger.Log("COPY_DONE", "Copied to workFile=" + workFile + " | " + InspectHeaderHex(workFile));

                workspaceMode = "dataclasys";
                if (IsDataClasysAvailable())
                {
                    DecryptFile(workFile, logsDir, sessionId);
                    WaitUntilDecrypted(workFile, logsDir, sessionId);
                }
                else if (AllowPlainOpenWithoutClient)
                {
                    workspaceMode = "plain";
                    EmitProgress(logsDir, "plain_open", "CLSUClient.exeが見つからないため復号なしで開きます (" + ClsuClientPath + ")", sessionId, serverPath, workFile, 0.90);
                    if (!LooksLikePdf(workFile))
                    {
                        string errMsg = "ファイルは暗号化（または非PDF形式）されていますが、DataClasysクライアント (" + ClsuClientPath + ") が見つからないため復号できませんでした。先頭バイト: " + InspectHeaderHex(workFile);
                        DebugLogger.Log("ERROR", errMsg);
                        throw new InvalidOperationException(errMsg);
                    }
                }
                else
                {
                    throw new InvalidOperationException("CLSUClient.exe が見つかりません: " + ClsuClientPath);
                }

                DebugLogger.Log("READ_WORK", "Reading decrypted workFile: " + InspectHeaderHex(workFile));
                byte[] resultBytes = ReadAllBytesShared(workFile);
                EmitProgress(logsDir, "ready", "ワークスペースの復号と読み込みが完了しました", sessionId, serverPath, workFile, 1.0);
                return resultBytes;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(sessionRoot))
                    {
                        Directory.Delete(sessionRoot, true);
                        DebugLogger.Log("CLEANUP", "Cleaned up sessionRoot: " + sessionRoot);
                    }
                }
                catch (Exception cleanEx)
                {
                    DebugLogger.Log("WARN", "Cleanup failed for " + sessionRoot + ": " + cleanEx.Message);
                }
            }
        }

        private static void CopyFileShared(string src, string dst)
        {
            using (FileStream inStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (FileStream outStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                inStream.CopyTo(outStream);
            }
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private void DecryptFile(string targetPath, string logsDir, string sessionId)
        {
            EmitProgress(logsDir, "decrypt", "DataClasys復号コマンド (CLSUClient.exe /d) を実行しています", sessionId, "", targetPath, 0.45);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ClsuClientPath,
                Arguments = "/d \"" + targetPath + "\"",
                WorkingDirectory = Path.GetDirectoryName(targetPath),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            DebugLogger.Log("EXEC", "Running: \"" + psi.FileName + "\" " + psi.Arguments);
            Stopwatch sw = Stopwatch.StartNew();
            using (Process proc = Process.Start(psi))
            {
                bool exited = proc.WaitForExit(DecryptTimeoutSec * 1000);
                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    string tMsg = "DataClasys復号コマンドがタイムアウトしました (" + DecryptTimeoutSec + "秒): " + targetPath;
                    DebugLogger.Log("ERROR", tMsg);
                    throw new TimeoutException(tMsg);
                }
                sw.Stop();
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();

                DebugLogger.Log("EXEC_DONE", string.Format("CLSUClient.exe exited with code={0} in {1:F2}s | stdout={2} | stderr={3}", proc.ExitCode, sw.Elapsed.TotalSeconds, stdout.Trim(), stderr.Trim()));

                LogLine(Path.Combine(logsDir, "decrypt_command.log"), "COMMAND: " + psi.FileName + " " + psi.Arguments);
                LogLine(Path.Combine(logsDir, "decrypt_command.log"), "RETURN CODE: " + proc.ExitCode);
                if (!string.IsNullOrEmpty(stdout)) LogLine(Path.Combine(logsDir, "decrypt_stdout.log"), stdout);
                if (!string.IsNullOrEmpty(stderr)) LogLine(Path.Combine(logsDir, "decrypt_stderr.log"), stderr);
                LogLine(Path.Combine(logsDir, "events.log"), string.Format("Decrypt command finished in {0:F2}s", sw.Elapsed.TotalSeconds));

                if (proc.ExitCode != 0)
                {
                    string errMsg = string.Format("DataClasys復号コマンドがエラー終了しました (rc={0}) target={1} stdout={2} stderr={3}", proc.ExitCode, targetPath, stdout.Trim(), stderr.Trim());
                    DebugLogger.Log("ERROR", errMsg);
                    throw new InvalidOperationException(errMsg);
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
            int pollIter = 0;

            while (sw.Elapsed.TotalSeconds < DecryptTimeoutSec)
            {
                pollIter++;
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
                catch (Exception pollEx)
                {
                    if (pollIter <= 5 || pollIter % 10 == 0)
                    {
                        DebugLogger.Log("WAIT_POLL", "Poll #" + pollIter + " file locked/busy: " + pollEx.Message);
                    }
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

                bool isPdf = LooksLikePdf(path);
                if (pollIter <= 5 || pollIter % 10 == 0 || isPdf)
                {
                    DebugLogger.Log("WAIT_POLL", string.Format("Poll #{0}: stableCount={1}, isPdf={2}, {3}", pollIter, stableCount, isPdf, InspectHeaderHex(path)));
                }

                if (stableCount >= 1 && isPdf)
                {
                    EmitProgress(logsDir, "decrypt_ready", "復号ファイルの準備が完了しました", sessionId, "", path, 0.90);
                    return;
                }

                lastSize = currentSize;
                lastHash = currentHash;
                Thread.Sleep(DecryptPollIntervalMs);
            }

            string timeoutDetail = "復号完了状態の待機がタイムアウトしました (" + DecryptTimeoutSec + "秒): " + path + " | 最終状態: " + InspectHeaderHex(path);
            DebugLogger.Log("ERROR", timeoutDetail);
            throw new TimeoutException(timeoutDetail);
        }
    }

    internal static class Program
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        private static readonly DataClasysNativeManager Manager = new DataClasysNativeManager();
        private static volatile bool Running = true;
        private static readonly object BridgeLock = new object();
        private static string BridgeDir = "";
        private static string LastSelectedDir = "";
        private static string LastAckCmdId = "";
        private static int StatusSeq = 1;
        private static int OldPayloadSeq = 0;
        private static int NewPayloadSeq = 0;
        private static int ErrorSeq = 0;
        private static string LastErrorMsg = "";
        private static volatile bool IsBusy = false;

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            DebugLogger.Log("STARTUP", "PDFDiffStudio starting (Zero-HTTP File Bridge mode). BaseDir=" + baseDir);
            Manager.CleanupStaleSessions(24);

            bool selfTest = false;
            foreach (string a in args)
            {
                if (a == "--self-test") selfTest = true;
            }

            string htmlPath = EnsureHtmlAndBridgeDir(baseDir);
            DebugLogger.Log("BRIDGE", "HTML path=" + htmlPath + " | BridgeDir=" + BridgeDir);

            Manager.OnProgressChanged = WriteBridgeStatus;
            DebugLogger.OnLogAdded = WriteBridgeStatus;
            WriteBridgeStatus();

            if (selfTest)
            {
                try
                {
                    if (!File.Exists(htmlPath)) return 2;
                    if (!File.Exists(Path.Combine(BridgeDir, "status.js"))) return 3;

                    string testDir = Path.Combine(baseDir, "_selftest_tmp");
                    Directory.CreateDirectory(testDir);
                    try
                    {
                        string dummyPlain = Path.Combine(testDir, "plain.pdf");
                        File.WriteAllBytes(dummyPlain, Encoding.ASCII.GetBytes("%PDF-1.4 dummy"));
                        string mode1, name1;
                        byte[] res1 = Manager.OpenAndReadServerPdf(dummyPlain, out mode1, out name1);
                        if (mode1 != "direct" || res1.Length < 5) return 4;
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

            LaunchAppWindowFileMode(htmlPath);

            // Win32 ウィンドウタイトル監視ループ（HTTP通信を一切使わずにコマンド受信＆生存確認）
            DateTime started = DateTime.UtcNow;
            DateTime lastSeenWindow = DateTime.UtcNow;
            bool everSawWindow = false;
            HashSet<string> processedCmds = new HashSet<string>();

            while (Running)
            {
                Thread.Sleep(120);
                List<string> titles = GetPdfDiffWindowTitles();
                if (titles.Count > 0)
                {
                    everSawWindow = true;
                    lastSeenWindow = DateTime.UtcNow;
                }
                else
                {
                    double sinceStart = (DateTime.UtcNow - started).TotalSeconds;
                    double sinceSeen = (DateTime.UtcNow - lastSeenWindow).TotalSeconds;
                    if (everSawWindow && sinceSeen > 5.0)
                    {
                        DebugLogger.Log("SHUTDOWN", "Viewer window closed by user. Exiting.");
                        break;
                    }
                    if (!everSawWindow && sinceStart > 60.0)
                    {
                        DebugLogger.Log("SHUTDOWN", "Viewer window never detected within 60s. Exiting.");
                        break;
                    }
                }

                foreach (string title in titles)
                {
                    int cmdStart = title.IndexOf("__CMD__:", StringComparison.Ordinal);
                    int cmdEnd = title.IndexOf(":__END__", StringComparison.Ordinal);
                    if (cmdStart >= 0 && cmdEnd > cmdStart + 8)
                    {
                        string payload = title.Substring(cmdStart + 8, cmdEnd - (cmdStart + 8));
                        // フォーマット: cmdId:cmdType:kind:size:argBase64
                        string[] parts = payload.Split(new char[] { ':' }, 5);
                        if (parts.Length >= 5)
                        {
                            string cmdId = parts[0];
                            string cmdType = parts[1];
                            string kind = parts[2];
                            long expectedSize = 0;
                            long.TryParse(parts[3], out expectedSize);
                            string argB64 = parts[4];

                            if (!processedCmds.Contains(cmdId))
                            {
                                processedCmds.Add(cmdId);
                                LastAckCmdId = cmdId;
                                WriteBridgeStatus();
                                ThreadPool.QueueUserWorkItem(_ => ExecuteBridgeCommand(cmdId, cmdType, kind, expectedSize, argB64));
                            }
                        }
                    }
                }
            }

            CleanupBridgeFiles();
            return 0;
        }

        private static string EnsureHtmlAndBridgeDir(string baseDir)
        {
            string htmlPath = Path.Combine(baseDir, "index.html");
            try
            {
                if (!File.Exists(htmlPath))
                {
                    byte[] embedded = LoadEmbeddedHtmlBytes();
                    File.WriteAllBytes(htmlPath, embedded);
                }
                BridgeDir = Path.Combine(baseDir, ".bridge");
                Directory.CreateDirectory(BridgeDir);
                return htmlPath;
            }
            catch (Exception ex)
            {
                DebugLogger.Log("WARN", "BaseDir not writable, using AppRoot for HTML & Bridge: " + ex.Message);
                string fallbackDir = Path.Combine(Manager.GetAppRoot(), "ui_host");
                Directory.CreateDirectory(fallbackDir);
                htmlPath = Path.Combine(fallbackDir, "index.html");
                File.WriteAllBytes(htmlPath, LoadEmbeddedHtmlBytes());
                BridgeDir = Path.Combine(fallbackDir, ".bridge");
                Directory.CreateDirectory(BridgeDir);
                return htmlPath;
            }
        }

        private static void CleanupBridgeFiles()
        {
            try
            {
                string pOld = Path.Combine(BridgeDir, "payload_old.js");
                string pNew = Path.Combine(BridgeDir, "payload_new.js");
                if (File.Exists(pOld)) File.Delete(pOld);
                if (File.Exists(pNew)) File.Delete(pNew);
            }
            catch { }
        }

        private static void ExecuteBridgeCommand(string cmdId, string cmdType, string kind, long expectedSize, string argB64)
        {
            string argText = "";
            if (!string.IsNullOrEmpty(argB64))
            {
                try
                {
                    argText = Encoding.UTF8.GetString(Convert.FromBase64String(argB64));
                }
                catch { argText = argB64; }
            }

            DebugLogger.Log("CMD", string.Format("Received command id={0} type={1} kind={2} size={3} arg={4}", cmdId, cmdType, kind, expectedSize, argText));

            if (cmdType == "CLEARLOGS")
            {
                DebugLogger.Clear();
                WriteBridgeStatus();
                return;
            }

            if (IsBusy)
            {
                DebugLogger.Log("WARN", "Command ignored because another file operation is currently busy.");
                return;
            }

            IsBusy = true;
            WriteBridgeStatus();

            try
            {
                if (cmdType == "SELECT")
                {
                    string title = kind == "old" ? "旧版PDFファイルを選択" : "新版PDFファイルを選択";
                    string selectedPath = ShowOpenFileDialogSta(title, null);
                    if (string.IsNullOrEmpty(selectedPath))
                    {
                        DebugLogger.Log("DIALOG", "File selection cancelled by user (kind=" + kind + ")");
                        return;
                    }
                    LastSelectedDir = Path.GetDirectoryName(selectedPath);
                    ProcessAndPublishPdf(kind, selectedPath);
                }
                else if (cmdType == "OPENPATH")
                {
                    string inputPath = (argText ?? "").Trim().Trim('"');
                    if (string.IsNullOrEmpty(inputPath))
                    {
                        throw new InvalidOperationException("ファイルパスが空です。");
                    }
                    LastSelectedDir = Path.GetDirectoryName(inputPath);
                    ProcessAndPublishPdf(kind, inputPath);
                }
                else if (cmdType == "DROP")
                {
                    string fileName = Path.GetFileName(argText);
                    DebugLogger.Log("DROP_CMD", "Attempting to locate dropped encrypted file: name=" + fileName + ", expectedSize=" + expectedSize);
                    string resolvedPath = ResolveDroppedFilePath(fileName, expectedSize);
                    if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        DebugLogger.Log("DROP_RESOLVED", "Automatically located dropped file at: " + resolvedPath);
                    }
                    else
                    {
                        DebugLogger.Log("DROP_DIALOG", "Could not auto-locate folder from Explorer; prompting OpenFileDialog for: " + fileName);
                        string title = (kind == "old" ? "【旧版】" : "【新版】") + "復号のためドロップされたファイルを選択してください: " + fileName;
                        resolvedPath = ShowOpenFileDialogSta(title, fileName);
                    }

                    if (string.IsNullOrEmpty(resolvedPath))
                    {
                        DebugLogger.Log("DROP_CANCEL", "User cancelled file location dialog for dropped file.");
                        return;
                    }
                    LastSelectedDir = Path.GetDirectoryName(resolvedPath);
                    ProcessAndPublishPdf(kind, resolvedPath);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log("EXCEPTION", ex.ToString());
                LastErrorMsg = ex.Message;
                ErrorSeq++;
            }
            finally
            {
                IsBusy = false;
                WriteBridgeStatus();
            }
        }

        private static void ProcessAndPublishPdf(string kind, string fullPath)
        {
            string mode, fileName;
            byte[] pdfBytes = Manager.OpenAndReadServerPdf(fullPath, out mode, out fileName);
            string b64 = Convert.ToBase64String(pdfBytes);

            lock (BridgeLock)
            {
                int nextSeq = (kind == "old") ? (++OldPayloadSeq) : (++NewPayloadSeq);
                string payloadFile = Path.Combine(BridgeDir, kind == "old" ? "payload_old.js" : "payload_new.js");
                string sb = "window.__PDF_DIFF_BRIDGE && window.__PDF_DIFF_BRIDGE.onPayload("
                    + JsonQuote(kind) + ",{"
                    + "\"seq\":" + nextSeq + ","
                    + "\"file_name\":" + JsonQuote(fileName) + ","
                    + "\"file_size\":" + pdfBytes.Length + ","
                    + "\"workspace_mode\":" + JsonQuote(mode) + ","
                    + "\"server_path\":" + JsonQuote(fullPath) + ","
                    + "\"pdf_base64\":\"" + b64 + "\""
                    + "});\r\n";

                WriteFileAtomic(payloadFile, sb);
                DebugLogger.Log("PUBLISH", string.Format("Published {0} payload (seq={1}, bytes={2}, mode={3})", kind, nextSeq, pdfBytes.Length, mode));
            }
            WriteBridgeStatus();
        }

        // 開いているエクスプローラーウィンドウや主要フォルダからドロップ元ファイルのフルパスを自動特定
        private static string ResolveDroppedFilePath(string fileName, long expectedSize)
        {
            if (string.IsNullOrEmpty(fileName)) return null;
            List<string> candidateDirs = new List<string>();

            // 1. 現在開いているWindowsエクスプローラーのフォルダパスを取得
            try
            {
                Type shellAppType = Type.GetTypeFromProgID("Shell.Application");
                if (shellAppType != null)
                {
                    object shell = Activator.CreateInstance(shellAppType);
                    object windows = shellAppType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null);
                    IEnumerable enumerable = windows as IEnumerable;
                    if (enumerable != null)
                    {
                        foreach (object win in enumerable)
                        {
                            try
                            {
                                object locUrlObj = win.GetType().InvokeMember("LocationURL", BindingFlags.GetProperty, null, win, null);
                                string locUrl = locUrlObj as string;
                                if (!string.IsNullOrEmpty(locUrl))
                                {
                                    Uri u;
                                    if (Uri.TryCreate(locUrl, UriKind.Absolute, out u) && u.IsFile)
                                    {
                                        string dirPath = u.LocalPath;
                                        if (!string.IsNullOrEmpty(dirPath) && Directory.Exists(dirPath))
                                        {
                                            candidateDirs.Add(dirPath);
                                            DebugLogger.Log("EXPLORER_DIR", "Detected open Explorer folder: " + dirPath);
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log("WARN", "Shell.Application Explorer query failed: " + ex.Message);
            }

            // 2. 直前に選択したフォルダ・実行フォルダ・デスクトップ・ダウンロード・ドキュメント
            if (!string.IsNullOrEmpty(LastSelectedDir)) candidateDirs.Add(LastSelectedDir);
            candidateDirs.Add(AppDomain.CurrentDomain.BaseDirectory);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                candidateDirs.Add(Path.Combine(userProfile, "Downloads"));
                candidateDirs.Add(Path.Combine(userProfile, "Desktop"));
                candidateDirs.Add(Path.Combine(userProfile, "Documents"));
            }

            // 完全一致（ファイル名＋サイズ）を優先検索
            foreach (string dir in candidateDirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        if (expectedSize <= 0 || new FileInfo(candidate).Length == expectedSize)
                        {
                            return candidate;
                        }
                    }
                }
                catch { }
            }

            // サイズ条件なしでファイル名一致を再確認
            foreach (string dir in candidateDirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    string candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch { }
            }

            return null;
        }

        private static void WriteBridgeStatus()
        {
            if (string.IsNullOrEmpty(BridgeDir)) return;
            lock (BridgeLock)
            {
                try
                {
                    StatusSeq++;
                    ProgressState p = Manager.LatestProgress;
                    string pctStr = p.Percent.HasValue ? p.Percent.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "null";
                    List<string> logs = DebugLogger.GetEntries();
                    StringBuilder logJson = new StringBuilder("[");
                    int start = Math.Max(0, logs.Count - 200);
                    for (int i = start; i < logs.Count; i++)
                    {
                        if (i > start) logJson.Append(',');
                        logJson.Append(JsonQuote(logs[i]));
                    }
                    logJson.Append("]");

                    string js = "window.__PDF_DIFF_BRIDGE && window.__PDF_DIFF_BRIDGE.onStatus({"
                        + "\"seq\":" + StatusSeq + ","
                        + "\"busy\":" + (IsBusy ? "true" : "false") + ","
                        + "\"ack_cmd\":" + JsonQuote(LastAckCmdId) + ","
                        + "\"old_seq\":" + OldPayloadSeq + ","
                        + "\"new_seq\":" + NewPayloadSeq + ","
                        + "\"error_seq\":" + ErrorSeq + ","
                        + "\"last_error\":" + JsonQuote(LastErrorMsg) + ","
                        + "\"stage\":" + JsonQuote(p.Stage) + ","
                        + "\"message\":" + JsonQuote(p.Message) + ","
                        + "\"percent\":" + pctStr + ","
                        + "\"dataclasys_available\":" + (Manager.IsDataClasysAvailable() ? "true" : "false") + ","
                        + "\"clsu_client_path\":" + JsonQuote(Manager.ClsuClientPath) + ","
                        + "\"workspace_root\":" + JsonQuote(Manager.GetAppRoot()) + ","
                        + "\"log_file_path\":" + JsonQuote(DebugLogger.LogFilePath) + ","
                        + "\"logs\":" + logJson.ToString()
                        + "});\r\n";

                    string statusFile = Path.Combine(BridgeDir, "status.js");
                    WriteFileAtomic(statusFile, js);
                }
                catch { }
            }
        }

        private static void WriteFileAtomic(string targetPath, string content)
        {
            string tmpPath = targetPath + ".tmp";
            File.WriteAllText(tmpPath, content, Encoding.UTF8);
            File.Copy(tmpPath, targetPath, true);
            try { File.Delete(tmpPath); } catch { }
        }

        private static List<string> GetPdfDiffWindowTitles()
        {
            List<string> results = new List<string>();
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    int len = GetWindowTextLength(hWnd);
                    if (len > 0 && len < 4096)
                    {
                        StringBuilder sb = new StringBuilder(len + 2);
                        GetWindowText(hWnd, sb, sb.Capacity);
                        string t = sb.ToString();
                        if (t.IndexOf("PDF Diff Studio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            t.IndexOf("PDFDiffStudio", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            results.Add(t);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }
            return results;
        }

        private static void LaunchAppWindowFileMode(string htmlPath)
        {
            string fileUri = new Uri(htmlPath).AbsoluteUri;
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
                        string edgeArgs = "--app=\"" + fileUri + "\" --window-size=1500,940";
                        DebugLogger.Log("LAUNCH", "Starting Edge in file:// mode (Zero-HTTP, bypasses i-FILTER): " + edge + " " + edgeArgs);
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = edge,
                            Arguments = edgeArgs,
                            UseShellExecute = false
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log("WARN", "Edge launch failed: " + ex.Message);
                    }
                }
            }

            try
            {
                DebugLogger.Log("LAUNCH", "Fallback Process.Start: " + htmlPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = htmlPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DebugLogger.Log("ERROR", "Fallback launch failed: " + ex.Message);
            }
        }

        private static byte[] LoadEmbeddedHtmlBytes()
        {
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

        private static string ShowOpenFileDialogSta(string title, string initialFileName)
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
                        if (!string.IsNullOrEmpty(LastSelectedDir) && Directory.Exists(LastSelectedDir))
                        {
                            ofd.InitialDirectory = LastSelectedDir;
                        }
                        if (!string.IsNullOrEmpty(initialFileName))
                        {
                            ofd.FileName = initialFileName;
                        }
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
