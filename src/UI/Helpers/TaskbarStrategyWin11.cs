using System;
using System.Drawing;
using System.Windows.Forms;
using static LiteMonitor.src.UI.Helpers.NativeMethods;

namespace LiteMonitor.src.UI.Helpers
{
    /// <summary>
    /// Win11 任务栏策略
    /// 机制：使用 SetParent 将窗口挂载到 Shell_TrayWnd 或 Shell_SecondaryTrayWnd
    /// 特点：依赖系统自身的布局，不需要手动挤占空间
    /// </summary>
    public class TaskbarStrategyWin11 : ITaskbarStrategy
    {
        private readonly Form _form;

        public bool IsReady => true; // Win11 模式不需要特殊的预热

        public bool HasInternalLayout => false;

        public TaskbarStrategyWin11(Form form)
        {
            _form = form;
        }

        public void Attach(IntPtr taskbarHandle)
        {
            // Win11 直接挂载到任务栏句柄
            SetParent(_form.Handle, taskbarHandle);
            TaskbarWinHelper.ApplyChildWindowStyle(_form.Handle);
        }

        public bool SetPosition(IntPtr taskbarHandle, int left, int top, int w, int h, int manualOffset, bool alignLeft)
        {
            // 从客户区原点换算，避免任务栏边框或缩放变化引入偏移。
            POINT point = new POINT { X = left, Y = top };
            return GetParent(_form.Handle) == taskbarHandle && ScreenToClient(taskbarHandle, ref point) &&
                SetWindowPos(_form.Handle, IntPtr.Zero, point.X, point.Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        public void Restore()
        {
            // Win11 模式没有修改系统窗口，不需要恢复
        }

        public IntPtr GetExpectedParent(IntPtr taskbarHandle)
        {
            return taskbarHandle;
        }
    }
}
