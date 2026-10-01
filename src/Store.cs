using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayHider
{
    public class AppSettings
    {
        public bool HotkeyEnabled = true;
        public string HotkeyHide = "Ctrl+Alt+H";
        public string HotkeyRestore = "Ctrl+Alt+Shift+H";
        public bool PerWindowIcon = true;
        public bool KeepHiddenDefault = true;
        public bool AutoStart = false;
    }

    /// <summary>自动收进托盘的规则。</summary>
    public class Rule
    {
        public string Kind = "exe";      // exe | title
        public string Value = "";
        public string LaunchPath = "";
        public string LaunchArgs = "";
        public bool Keep = true;

        public bool Matches(WindowInfo info)
        {
            if (info == null || string.IsNullOrEmpty(Value)) { return false; }
            if (Kind == "title")
            {
                return info.Title != null && info.Title.IndexOf(Value, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            string name = info.ProcessName == null ? "" : info.ProcessName;
            string path = info.ProcessPath == null ? "" : info.ProcessPath;
            if (string.Equals(name, Value, StringComparison.OrdinalIgnoreCase)) { return true; }
            if (string.Equals(name + ".exe", Value, StringComparison.OrdinalIgnoreCase)) { return true; }
            try
            {
                if (path.Length > 0 && string.Equals(Path.GetFileName(path), Value, StringComparison.OrdinalIgnoreCase)) { return true; }
            }
            catch { }
            return false;
        }

        public string Describe()
        {
            return (Kind == "title" ? "标题包含「" : "程序「") + Value + "」";
        }
    }

    /// <summary>数据目录、配置、规则、命令队列、开机自启。</summary>
    public static class Store
    {
        public static string DataDir = "";
        public static string ExePath = "";

        public static string QueueDir { get { return Path.Combine(DataDir, "queue"); } }
        public static string RulesFile { get { return Path.Combine(DataDir, "rules.txt"); } }
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.ini"); } }
        public static string StateFile { get { return Path.Combine(DataDir, "hidden.state"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "trayhider.log"); } }

        public static void Init(string overrideDir)
        {
            ExePath = Application.ExecutablePath;
            string dir = overrideDir;
            if (string.IsNullOrEmpty(dir))
            {
                string exeDir = Path.GetDirectoryName(ExePath);
                if (File.Exists(Path.Combine(exeDir, "portable.txt")))
                {
                    dir = Path.Combine(exeDir, "data");
                }
                else
                {
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayHider");
                }
            }
            DataDir = dir;
            try
            {
                Directory.CreateDirectory(DataDir);
                Directory.CreateDirectory(QueueDir);
            }
            catch { }
        }

        public static void Log(string format, params object[] args)
        {
            try
            {
                string text = args == null || args.Length == 0 ? format : string.Format(format, args);
                FileInfo fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 1024 * 1024) { File.Delete(LogFile); }
                File.AppendAllText(LogFile,
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, text), Encoding.UTF8);
            }
            catch { }
        }

        // ---------------- 设置 ----------------

        public static AppSettings LoadSettings()
        {
            AppSettings s = new AppSettings();
            try
            {
                if (!File.Exists(SettingsFile)) { return s; }
                foreach (string raw in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) { continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) { continue; }
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "hotkeyenabled": s.HotkeyEnabled = val == "1" || val.ToLowerInvariant() == "true"; break;
                        case "hotkeyhide": if (val.Length > 0) { s.HotkeyHide = val; } break;
                        case "hotkeyrestore": if (val.Length > 0) { s.HotkeyRestore = val; } break;
                        case "perwindowicon": s.PerWindowIcon = val == "1" || val.ToLowerInvariant() == "true"; break;
                        case "keephiddendefault": s.KeepHiddenDefault = val == "1" || val.ToLowerInvariant() == "true"; break;
                        case "autostart": s.AutoStart = val == "1" || val.ToLowerInvariant() == "true"; break;
                    }
                }
            }
            catch (Exception ex) { Log("读取设置失败: {0}", ex.Message); }
            return s;
        }

        public static void SaveSettings(AppSettings s)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# TrayHider 设置");
                sb.AppendLine("hotkeyEnabled=" + (s.HotkeyEnabled ? "1" : "0"));
                sb.AppendLine("hotkeyHide=" + s.HotkeyHide);
                sb.AppendLine("hotkeyRestore=" + s.HotkeyRestore);
                sb.AppendLine("perWindowIcon=" + (s.PerWindowIcon ? "1" : "0"));
                sb.AppendLine("keepHiddenDefault=" + (s.KeepHiddenDefault ? "1" : "0"));
                sb.AppendLine("autoStart=" + (s.AutoStart ? "1" : "0"));
                File.WriteAllText(SettingsFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log("保存设置失败: {0}", ex.Message); }
        }

        // ---------------- 规则 ----------------

        public static List<Rule> LoadRules()
        {
            List<Rule> list = new List<Rule>();
            try
            {
                if (!File.Exists(RulesFile)) { return list; }
                foreach (string raw in File.ReadAllLines(RulesFile, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) { continue; }
                    string[] p = line.Split('\t');
                    Rule r = new Rule();
                    if (p.Length > 0) { r.Kind = p[0].Trim().ToLowerInvariant(); }
                    if (p.Length > 1) { r.Value = p[1].Trim(); }
                    if (p.Length > 2) { r.LaunchPath = p[2].Trim(); }
                    if (p.Length > 3) { r.LaunchArgs = p[3].Trim(); }
                    if (p.Length > 4) { r.Keep = p[4].Trim() != "0"; }
                    if (r.Kind != "title") { r.Kind = "exe"; }
                    if (r.Value.Length > 0) { list.Add(r); }
                }
            }
            catch (Exception ex) { Log("读取规则失败: {0}", ex.Message); }
            return list;
        }

        public static void SaveRules(List<Rule> rules)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 每行一条规则：匹配方式(exe|title) <TAB> 匹配值 <TAB> 启动路径 <TAB> 启动参数 <TAB> 强制保持隐藏(0|1)");
                foreach (Rule r in rules)
                {
                    sb.AppendLine(string.Join("\t", new string[] {
                        r.Kind, r.Value, r.LaunchPath ?? "", r.LaunchArgs ?? "", r.Keep ? "1" : "0" }));
                }
                File.WriteAllText(RulesFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Log("保存规则失败: {0}", ex.Message); }
        }

        // ---------------- 命令队列（客户端 -> 常驻实例） ----------------

        public static void Enqueue(string command)
        {
            try
            {
                Directory.CreateDirectory(QueueDir);
                string name = string.Format("{0:yyyyMMddHHmmssfff}-{1}", DateTime.Now, Process.GetCurrentProcess().Id);
                string tmp = Path.Combine(QueueDir, name + ".tmp");
                string final = Path.Combine(QueueDir, name + ".cmd");
                // 先写 .tmp 再改名：常驻实例永远只会看到"已经写完"的命令文件
                File.WriteAllText(tmp, command, Encoding.UTF8);
                try { if (File.Exists(final)) { File.Delete(final); } }
                catch { }
                File.Move(tmp, final);
            }
            catch (Exception ex) { Log("写入命令失败: {0}", ex.Message); }
        }

        public static List<string> TakeCommands()
        {
            List<string> list = new List<string>();
            try
            {
                if (!Directory.Exists(QueueDir)) { return list; }
                // 清掉写了一半就崩掉的残留
                foreach (string stale in Directory.GetFiles(QueueDir, "*.tmp"))
                {
                    try
                    {
                        if (DateTime.Now - File.GetLastWriteTime(stale) > TimeSpan.FromMinutes(1)) { File.Delete(stale); }
                    }
                    catch { }
                }
                string[] files = Directory.GetFiles(QueueDir, "*.cmd");
                Array.Sort(files);
                foreach (string f in files)
                {
                    try
                    {
                        list.Add(File.ReadAllText(f, Encoding.UTF8).Trim());
                    }
                    catch (Exception ex)
                    {
                        // 读不到就留着下一轮再读 —— 绝不能把没读到的命令删掉
                        Log("读取命令失败（保留待重试）{0}: {1}", f, ex.Message);
                        continue;
                    }
                    try { File.Delete(f); }
                    catch { }
                }
            }
            catch (Exception ex) { Log("扫描命令队列失败: {0}", ex.Message); }
            return list;
        }

        // ---------------- 崩溃恢复用的状态文件 ----------------

        public static void SaveState(IEnumerable<IntPtr> handles)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (IntPtr h in handles) { sb.AppendLine(h.ToInt64().ToString()); }
                string tmp = StateFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(StateFile)) { File.Delete(StateFile); }
                File.Move(tmp, StateFile);
            }
            catch (Exception ex) { Log("写状态文件失败: {0}", ex.Message); }
        }

        public static void ClearState()
        {
            try { if (File.Exists(StateFile)) { File.Delete(StateFile); } }
            catch { }
        }

        public static List<IntPtr> ReadState()
        {
            List<IntPtr> list = new List<IntPtr>();
            try
            {
                if (!File.Exists(StateFile)) { return list; }
                foreach (string raw in File.ReadAllLines(StateFile, Encoding.UTF8))
                {
                    long v;
                    if (long.TryParse(raw.Trim(), out v) && v != 0) { list.Add(new IntPtr(v)); }
                }
            }
            catch { }
            return list;
        }

        // ---------------- 开机自启 ----------------

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "TrayHider";

        public static bool GetAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) { return false; }
                    return key.GetValue(RunValue) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutoStart(bool on)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) { return; }
                    if (on) { key.SetValue(RunValue, "\"" + ExePath + "\" --resident"); }
                    else { key.DeleteValue(RunValue, false); }
                }
            }
            catch (Exception ex) { Log("设置开机自启失败: {0}", ex.Message); }
        }
    }
}
