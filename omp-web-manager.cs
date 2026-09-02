// omp-web-manager.cs
// Standalone WinForms manager for omp-web.
//   GUI:     start (npm start on port 30177), stop (kill every listener on 30177),
//            open browser, refresh status, live server log.
//   Headless: omp-web-manager.exe start | stop | status
// Build:    scripts\windows\build-manager.cmd  (uses the .NET Framework csc.exe shipped with Windows)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OmpWebManager
{
    internal static class Program
    {
        internal const int Port = 30177;
        internal const string Url = "http://127.0.0.1:30177";

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0)
            {
                // Headless mode: reuse the parent console if any, else allocate one.
                // With redirected stdout (pipes) neither is needed - no window flashes.
                if (!Console.IsOutputRedirected)
                {
                    if (!AttachConsole(-1)) AllocConsole();
                }
                switch (args[0].ToLowerInvariant())
                {
                    case "start":
                        return HeadlessStart();
                    case "stop":
                        return HeadlessStop();
                    case "status":
                        bool running = IsPortListening();
                        Console.WriteLine(running ? "running" : "stopped");
                        return running ? 0 : 1;
                    default:
                        Console.WriteLine("usage: omp-web-manager [start|stop|status]");
                        return 2;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\OmpWebManager_Gui_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("omp-web 管理器已在运行。", "omp-web 管理器",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ManagerForm());
            }
            return 0;
        }

        // ------------------------------------------------------------------
        // Port helpers (semantics match Get-NetTCPConnection port-kill)
        // ------------------------------------------------------------------

        internal static List<int> GetListenerPids()
        {
            var result = new List<int>();
            string output;
            try
            {
                var psi = new ProcessStartInfo("netstat.exe", "-ano");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (var p = Process.Start(psi))
                {
                    output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("netstat failed: " + ex.Message);
                return result;
            }

            string needle = ":" + Port;
            foreach (string raw in output.Split('\n'))
            {
                if (raw.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (raw.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string[] parts = raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    int pid;
                    if (int.TryParse(parts[parts.Length - 1], out pid) && !result.Contains(pid))
                    {
                        result.Add(pid);
                    }
                }
            }
            return result;
        }

        internal static bool IsPortListening()
        {
            return GetListenerPids().Count > 0;
        }

        internal static List<int> KillListeners()
        {
            var killed = new List<int>();
            foreach (int pid in GetListenerPids())
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        p.Kill(); // Stop-Process -Force
                    }
                    killed.Add(pid);
                }
                catch (Exception)
                {
                    // already gone or access denied
                }
            }
            return killed;
        }

        // ------------------------------------------------------------------
        // Headless commands
        // ------------------------------------------------------------------

        private static int HeadlessStart()
        {
            if (IsPortListening())
            {
                Console.WriteLine("already running: " + Url);
                return 0;
            }
            string repo = AppDomain.CurrentDomain.BaseDirectory;
            if (!File.Exists(Path.Combine(repo, "package.json")))
            {
                Console.Error.WriteLine("error: package.json not found next to this exe; place it in the omp-web project root");
                return 3;
            }
            if (!Directory.Exists(Path.Combine(repo, ".next")))
            {
                Console.Error.WriteLine("error: .next not found; run 'npm run build' first");
                return 3;
            }
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c npm start");
                psi.WorkingDirectory = repo;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                var p = Process.Start(psi);
                Console.WriteLine("started (pid " + p.Id + "): " + Url);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                return 4;
            }
        }

        private static int HeadlessStop()
        {
            var killed = KillListeners();
            if (killed.Count == 0)
            {
                Console.WriteLine("not running");
                return 0;
            }
            var strs = new List<string>();
            foreach (int id in killed) strs.Add(id.ToString());
            Console.WriteLine("stopped: " + string.Join(", ", strs.ToArray()));
            return 0;
        }
    }

    // ----------------------------------------------------------------------
    // GUI
    // ----------------------------------------------------------------------

    internal class ManagerForm : Form
    {
        private readonly Label _statusLabel;
        private readonly Button _btnStart;
        private readonly Button _btnStop;
        private readonly Button _btnOpen;
        private readonly Button _btnRefresh;
        private readonly CheckBox _chkAutoOpen;
        private readonly TextBox _logBox;
        private readonly System.Windows.Forms.Timer _statusTimer;
        private Process _child;   // server process started by this GUI
        private bool _lastRunning;
        private bool _closing;

        internal ManagerForm()
        {
            Text = "omp-web 管理器";
            ClientSize = new Size(640, 400);
            MinimumSize = new Size(520, 300);
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            var top = new FlowLayoutPanel();
            top.Dock = DockStyle.Top;
            top.Height = 38;
            top.Padding = new Padding(8, 8, 8, 0);
            top.FlowDirection = FlowDirection.LeftToRight;
            top.WrapContents = false;
            top.AutoScroll = true;

            _statusLabel = new Label();
            _statusLabel.AutoSize = true;
            _statusLabel.Margin = new Padding(0, 4, 12, 0);
            _statusLabel.Text = "● 已停止";
            _statusLabel.ForeColor = Color.Firebrick;
            top.Controls.Add(_statusLabel);

            _btnStart = new Button();
            _btnStart.Text = "启动服务";
            _btnStart.AutoSize = true;
            _btnStart.Margin = new Padding(0, 2, 6, 0);
            _btnStart.Click += delegate { DoStart(); };
            top.Controls.Add(_btnStart);

            _btnStop = new Button();
            _btnStop.Text = "停止服务";
            _btnStop.AutoSize = true;
            _btnStop.Margin = new Padding(0, 2, 6, 0);
            _btnStop.Click += delegate { DoStop(); };
            top.Controls.Add(_btnStop);

            _btnOpen = new Button();
            _btnOpen.Text = "打开页面";
            _btnOpen.AutoSize = true;
            _btnOpen.Margin = new Padding(0, 2, 6, 0);
            _btnOpen.Click += delegate { OpenBrowser(); };
            top.Controls.Add(_btnOpen);

            _btnRefresh = new Button();
            _btnRefresh.Text = "刷新";
            _btnRefresh.AutoSize = true;
            _btnRefresh.Margin = new Padding(0, 2, 6, 0);
            _btnRefresh.Click += delegate { RefreshStatus(); };
            top.Controls.Add(_btnRefresh);

            _chkAutoOpen = new CheckBox();
            _chkAutoOpen.Text = "启动后自动打开浏览器";
            _chkAutoOpen.AutoSize = true;
            _chkAutoOpen.Checked = true;
            _chkAutoOpen.Margin = new Padding(10, 5, 0, 0);
            top.Controls.Add(_chkAutoOpen);

            _logBox = new TextBox();
            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = Color.FromArgb(30, 30, 30);
            _logBox.ForeColor = Color.FromArgb(220, 220, 220);
            _logBox.Font = new Font("Consolas", 9F);
            _logBox.BorderStyle = BorderStyle.None;

            Controls.Add(_logBox);
            Controls.Add(top);

            _statusTimer = new System.Windows.Forms.Timer();
            _statusTimer.Interval = 3000;
            _statusTimer.Tick += delegate { RefreshStatus(); };
            _statusTimer.Start();

            Shown += delegate { RefreshStatus(); };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_closing && (Program.IsPortListening() || (_child != null && !_child.HasExited)))
            {
                var r = MessageBox.Show(this,
                    "服务正在运行,关闭窗口将停止服务。\n确定要关闭吗?",
                    "omp-web 管理器",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button1);
                if (r != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
                _closing = true;
                DoStop();
            }
            base.OnFormClosing(e);
        }

        private void DoStart()
        {
            if (Program.IsPortListening())
            {
                AppendLog("已在运行: " + Program.Url);
                RefreshStatus();
                OpenBrowser();
                return;
            }
            if (_child != null && !_child.HasExited)
            {
                AppendLog("服务正在启动中,请稍候...");
                return;
            }
            string repo = AppDomain.CurrentDomain.BaseDirectory;
            if (!File.Exists(Path.Combine(repo, "package.json")))
            {
                MessageBox.Show(this,
                    "未找到 package.json。\n请把本程序放到 omp-web 项目根目录(与 package.json 同级)。",
                    "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("错误: 未找到 package.json,请把本程序放到 omp-web 项目根目录");
                return;
            }
            if (!Directory.Exists(Path.Combine(repo, ".next")))
            {
                MessageBox.Show(this,
                    "未找到 .next 目录。\n请先在项目目录运行: npm run build",
                    "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("错误: 缺少 .next 目录,请先运行 npm run build");
                return;
            }
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c npm start");
                psi.WorkingDirectory = repo;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                var p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) AppendLog(e.Data);
                };
                p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) AppendLog(e.Data);
                };
                p.Exited += delegate
                {
                    AppendLog("服务器进程已退出 (code " + p.ExitCode + ")");
                    _child = null;
                    RefreshStatus();
                };
                if (!p.Start())
                {
                    AppendLog("启动失败: Process.Start 返回 false");
                    return;
                }
                _child = p;
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                AppendLog("正在启动: npm start (端口 " + Program.Port + ") ...");
                ThreadPool.QueueUserWorkItem(delegate { WaitForReady(); });
            }
            catch (Exception ex)
            {
                AppendLog("启动异常: " + ex.Message);
            }
        }

        private void WaitForReady()
        {
            for (int i = 0; i < 120; i++) // up to 60s
            {
                if (Program.IsPortListening()) break;
                if (_child != null && _child.HasExited) return;
                Thread.Sleep(500);
            }
            RefreshStatus();
            if (Program.IsPortListening())
            {
                AppendLog("就绪: " + Program.Url);
                if (_chkAutoOpen.Checked) OpenBrowser();
            }
            else
            {
                AppendLog("错误: 端口 " + Program.Port + " 未在预期时间内就绪");
            }
        }

        private void DoStop()
        {
            var pids = Program.GetListenerPids();
            if (pids.Count == 0 && (_child == null || _child.HasExited))
            {
                AppendLog("未在运行(端口 " + Program.Port + " 无监听)");
                RefreshStatus();
                return;
            }
            var strs = new List<string>();
            foreach (int id in pids) strs.Add(id.ToString());
            AppendLog("正在停止: " + (pids.Count > 0
                ? "终止监听进程 " + string.Join(", ", strs.ToArray())
                : "终止子进程"));
            var killed = Program.KillListeners();
            Process child = _child;
            if (child != null && !child.HasExited)
            {
                try
                {
                    child.Kill();
                    AppendLog("已终止子进程 " + child.Id);
                }
                catch (Exception ex)
                {
                    AppendLog("终止子进程失败: " + ex.Message);
                }
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(800);
                RefreshStatus();
                if (!Program.IsPortListening()) AppendLog("已停止");
                else AppendLog("警告: 端口 " + Program.Port + " 仍有监听");
            });
        }

        private void OpenBrowser()
        {
            try
            {
                Process.Start(Program.Url);
            }
            catch (Exception ex)
            {
                AppendLog("打开浏览器失败: " + ex.Message);
            }
        }

        private void RefreshStatus()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(RefreshStatus));
                return;
            }
            bool running = Program.IsPortListening();
            _statusLabel.Text = running ? "● 运行中" : "● 已停止";
            _statusLabel.ForeColor = running ? Color.SeaGreen : Color.Firebrick;
            if (running != _lastRunning)
            {
                _lastRunning = running;
                AppendLog(running ? "状态: 运行中 (" + Program.Url + ")"
                                  : "状态: 已停止");
            }
        }

        private void AppendLog(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), line);
                return;
            }
            _logBox.AppendText(line + Environment.NewLine);
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.ScrollToCaret();
        }
    }
}
