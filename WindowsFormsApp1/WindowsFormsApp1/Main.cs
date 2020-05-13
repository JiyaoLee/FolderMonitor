using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Threading;
using System.Windows.Forms;

namespace WindowsFormsApp1
{
    public partial class Main : Form
    {
        private IFolderMonitor folderMonitor;
        public Main()
        {
            InitializeComponent();
            SetAccess("Users", Application.StartupPath);
        }
        /// <summary>
        /// 为指定用户组，授权目录指定完全访问权限
        /// </summary>
        /// <param name="user">用户组，如Users</param>
        /// <param name="folder">实际的目录</param>
        /// <returns></returns>
        private static bool SetAccess(string user, string folder)
        {
            //定义为完全控制的权限
            const FileSystemRights Rights = FileSystemRights.FullControl;

            //添加访问规则到实际目录
            var AccessRule = new FileSystemAccessRule(user, Rights,
                InheritanceFlags.None,
                PropagationFlags.NoPropagateInherit,
                AccessControlType.Allow);

            var Info = new DirectoryInfo(folder);
            var Security = Info.GetAccessControl(AccessControlSections.Access);

            bool Result;
            Security.ModifyAccessRule(AccessControlModification.Set, AccessRule, out Result);
            if (!Result) return false;

            //总是允许再目录上进行对象继承
            const InheritanceFlags iFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            //为继承关系添加访问规则
            AccessRule = new FileSystemAccessRule(user, Rights,
                iFlags,
                PropagationFlags.InheritOnly,
                AccessControlType.Allow);

            Security.ModifyAccessRule(AccessControlModification.Add, AccessRule, out Result);
            if (!Result) return false;

            Info.SetAccessControl(Security);

            return true;
        }
        private void TextBox1_MouseClick(object sender, MouseEventArgs e)
        {
            folderBrowserDialog1.Description = "请选择EDI文件所在文件夹";
            if (folderBrowserDialog1.ShowDialog() == DialogResult.OK)
            {
                if (string.IsNullOrEmpty(folderBrowserDialog1.SelectedPath))
                {
                    MessageBox.Show(this, "文件夹路径不能为空", "提示");
                    return;
                }
                textBox1.Text = folderBrowserDialog1.SelectedPath;
                folderMonitor = new FolderMonitor(folderBrowserDialog1.SelectedPath);
                folderMonitor.CreateEvent += (path) =>
                {
                    //模拟操作监控文件
                    Thread.Sleep(1000);
                    string value = "";
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read))
                    {
                        using (StreamReader streamReader = new StreamReader(stream))
                        {
                            value = streamReader.ReadToEnd().Replace('\r', '\n');
                        }
                        stream.Close();
                    }
                    using (StreamWriter streamWriter = new StreamWriter(File.Open(path, FileMode.Truncate)))
                    {
                        if (!string.IsNullOrEmpty(value))
                        {
                            streamWriter.Write(value);
                        }
                        streamWriter.Flush();
                    }
                };
                folderMonitor.Start();
            }
        }
    }

    public interface IFolderMonitor
    {
        event Action<string> CreateEvent;
        void Start();
    }
    public class FolderMonitor : IFolderMonitor
    {
        private readonly FileSystemWatcher fileSystemWatcher = new FileSystemWatcher();
        private readonly Dictionary<string, DateTime> createPendingEvent = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> deletePendingEvent = new Dictionary<string, DateTime>();
        private readonly System.Threading.Timer change_timer;
        private bool m_timer_started = false;

        private readonly System.Threading.Timer del_timer;
        private bool m_flag_del_started = false;

        public bool IsStarted { get; set; }

        public event Action<string> CreateEvent;

        public event Action<string> DeleteEvent;

        public FolderMonitor(string dirPath)
        {
            fileSystemWatcher.Path = dirPath;
            fileSystemWatcher.IncludeSubdirectories = true;
            fileSystemWatcher.Created += new FileSystemEventHandler(OnChange);
            fileSystemWatcher.Changed += new FileSystemEventHandler(OnChange);
            change_timer = new System.Threading.Timer(OnTimeOut, null, Timeout.Infinite, Timeout.Infinite);
            del_timer = new System.Threading.Timer(OnDelTimeOut, null, Timeout.Infinite, Timeout.Infinite);
        }

        private void OnDelTimeOut(object state)
        {
            string[] delPaths;
            lock (deletePendingEvent)
            {
                delPaths = deletePendingEvent.Select(m => m.Key).ToArray();
                for (int i = 0; i < delPaths.Length; i++)
                {
                    deletePendingEvent.Remove(delPaths[i]);
                }
                if (deletePendingEvent.Count == 0)
                {
                    m_flag_del_started = false;
                    del_timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
            if (DeleteEvent != null)
            {
                for (int i = 0; i < delPaths.Length; i++)
                {
                    DeleteEvent(delPaths[i]);
                }
            }
        }

        private void OnChange(object sender, FileSystemEventArgs e)
        {
            lock (createPendingEvent)
            {
                createPendingEvent[e.FullPath] = DateTime.Now;
                if (!m_timer_started)
                {
                    change_timer.Change(1000, 100);
                    m_timer_started = true;
                }
                Trace.WriteLine(createPendingEvent.Count);
            }
        }
        private void OnDelete(object sender, FileSystemEventArgs e)
        {
            lock (deletePendingEvent)
            {
                deletePendingEvent[e.FullPath] = DateTime.Now;
                if (!m_flag_del_started)
                {
                    del_timer.Change(1000, 100);
                    m_flag_del_started = true;
                }
            }
        }
        public void Start()
        {
            fileSystemWatcher.EnableRaisingEvents = true;
            IsStarted = true;
        }

        public void Stop()
        {
            fileSystemWatcher.EnableRaisingEvents = false;
            IsStarted = false;
        }

        private void OnTimeOut(object state)
        {
            List<string> paths;
            lock (createPendingEvent)
            {
                paths = FindReadyPaths(createPendingEvent);

                paths.ForEach(m =>
                {
                    createPendingEvent.Remove(m);
                });

                if (createPendingEvent.Count == 0)
                {
                    change_timer.Change(Timeout.Infinite, Timeout.Infinite);
                    m_timer_started = false;
                }
            }
            if (CreateEvent != null)
            {
                paths.ForEach(path =>
                {
                    CreateEvent(path);
                });
            }
        }

        private List<string> FindReadyPaths(Dictionary<string, DateTime> events)
        {
            List<string> results = new List<string>();
            DateTime now = DateTime.Now;

            foreach (KeyValuePair<string, DateTime> entry in events)
            {
                // If the path has not received a new event in the last 75ms
                // an event for the path should be fired
                double diff = now.Subtract(entry.Value).TotalMilliseconds;
                if (diff >= 75)
                {
                    results.Add(entry.Key);
                }
            }
            return results;
        }
    }
}
