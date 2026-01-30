using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CopyPlusPlus.Properties;
using Gma.System.MouseKeyHook;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;

namespace CopyPlusPlus
{
    /// <summary>
    /// 只保留：合并换行 / 去除空格 / 开机启动。
    /// 其余功能（翻译、划词、手动处理、捐助、更新提示等）已移除。
    /// </summary>
    public partial class MainWindow
    {
        public static TaskbarIcon NotifyIcon;

        // 通过托盘菜单“退出”触发时，允许真正退出（不再拦截 Closing）。
        public static bool ExitRequested { get; set; }

        /// <summary>
        /// 用于全局禁用（托盘菜单“禁用软件/恢复软件”）。
        /// </summary>
        public bool GlobalSwitch = true;

        private readonly IKeyboardMouseEvents _globalMouseKeyHook;

        // 原版“保留句末换行”配置（原版可在界面右键设置；精简版保留原默认值与行为，但不再提供 UI）。
        private bool _remainChinese;
        private bool _remainEnglish;

        public MainWindow()
        {
            InitializeComponent();

            NotifyIcon = (TaskbarIcon)FindResource("MyNotifyIcon");
            NotifyIcon.Visibility = Visibility.Visible;

            RestoreSwitchStates();

            // 保留原版默认行为：句末（。 / .）可选择不去掉换行。
            _remainChinese = Settings.Default.RemainChinese;
            _remainEnglish = Settings.Default.RemainEnglish;

            // 按键绑定：Ctrl + C + C
            _globalMouseKeyHook = Hook.GlobalEvents();
            var keySequence = Sequence.FromString("Control+C,Control+C");
            _globalMouseKeyHook.OnSequence(new Dictionary<Sequence, Action>
            {
                { keySequence, AfterKeySequence }
            });
        }

        private void RestoreSwitchStates()
        {
            // 兼容旧版配置（SwitchCheck 长度可能大于/小于预期）
            var list = Settings.Default.SwitchCheck?.Cast<string>().ToList() ?? new List<string>();

            SwitchMain.IsOn = GetBool(list, 0, true);
            SwitchSpace.IsOn = GetBool(list, 1, false);
            // 原版中 AutoStart 位于 index=5
            SwitchAutoStart.IsOn = GetBool(list, 5, false);
        }

        private static bool GetBool(IReadOnlyList<string> list, int index, bool defaultValue)
        {
            if (index < 0 || index >= list.Count) return defaultValue;
            return bool.TryParse(list[index], out var b) ? b : defaultValue;
        }

        private static void EnsureCount(System.Collections.Specialized.StringCollection col, int count)
        {
            if (col == null) return;
            while (col.Count < count) col.Add("False");
        }

        private async void AfterKeySequence()
        {
            if (!GlobalSwitch) return;

            // 保持原版行为：等待剪贴板稳定后再处理。
            await Task.Delay(200);
            if (Clipboard.ContainsText()) ProcessText(Clipboard.GetText());
        }

        public void ProcessText(string text)
        {
            // 原版逻辑：去掉 CAJ viewer 造成的莫名的空格符号
            text = text.Replace("", "");

            if (SwitchMain.IsOn || SwitchSpace.IsOn)
            {
                if (text.Length > 1)
                {
                    for (var counter = 0; counter < text.Length; ++counter)
                    {
                        // 合并换行（原版仅处理 \r / \r\n）
                        if (SwitchMain.IsOn && counter >= 0 && text[counter] == '\r')
                        {
                            if (counter > 0)
                            {
                                // 原版行为：检测到句号结尾，则不去掉换行
                                if (text[counter - 1] == '。' && _remainChinese) continue;
                                if (text[counter - 1] == '.' && _remainEnglish) continue;
                            }

                            // 去除换行（优先按 CRLF 删除 2 个字符，失败则删除 1 个）
                            try
                            {
                                text = text.Remove(counter, 2);
                            }
                            catch
                            {
                                text = text.Remove(counter, 1);
                            }

                            --counter;

                            // 判断 非负数越界 或 句末
                            if (counter >= 0 && counter != text.Length - 1)
                            {
                                // 判断 非中文 结尾, 则加一个空格
                                if (!Regex.IsMatch(text[counter].ToString(), "[\n ，。？！《》\u4e00-\u9fa5]"))
                                    text = text.Insert(counter + 1, " ");
                            }
                        }

                        // 去除空格
                        if (SwitchSpace.IsOn && counter >= 0 && text[counter] == ' ')
                        {
                            text = text.Remove(counter, 1);
                            --counter;
                        }
                    }
                }
            }

            try
            {
                Clipboard.SetDataObject(text);
            }
            catch
            {
                Thread.Sleep(50);
                Clipboard.SetDataObject(text);
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            // 记录 Switch 状态（兼容旧版 SwitchCheck 长度）
            var sc = Settings.Default.SwitchCheck;
            EnsureCount(sc, 6); // 确保 index=5 存在

            sc[0] = SwitchMain.IsOn.ToString();
            sc[1] = SwitchSpace.IsOn.ToString();
            sc[5] = SwitchAutoStart.IsOn.ToString();

            Settings.Default.Save();

            try
            {
                _globalMouseKeyHook?.Dispose();
            }
            catch
            {
                // ignored
            }
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (ExitRequested) return;

            // 与原版一致：点击 X 不退出，只隐藏到托盘
            Hide();
            e.Cancel = true;
        }

        public void HideNotifyIcon()
        {
            NotifyIcon.Visibility = Visibility.Visible;
        }

        public void OnAutoStart(bool auto)
        {
            if (auto)
            {
                Show();
                Hide();
                NotifyIcon.Visibility = Visibility.Visible;
            }
            else
            {
                Show();
            }
        }

        private void SwitchAutoStart_OnToggled(object sender, RoutedEventArgs e)
        {
            const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            var key = Registry.CurrentUser.OpenSubKey(path, true);
            if (key == null) return;

            if (SwitchAutoStart.IsOn)
            {
                // 每次软件路径发生变化，系统会视为新软件，生成新的设置文件，因此不用担心路径发生变化
                key.SetValue("CopyPlusPlus", Assembly.GetExecutingAssembly().Location + " /AutoStart");
            }
            else
            {
                key.DeleteValue("CopyPlusPlus", false);
            }
        }
    }
}
