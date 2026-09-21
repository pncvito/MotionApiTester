using System;
using System.Windows.Input;

namespace MotionApiTester.ViewModels
{
    /// <summary>
    /// 同步 ICommand 实现。
    /// CanExecuteChanged 转发到 CommandManager.RequerySuggested，
    /// 因此 ViewModel 只需在状态变化时调用 CommandManager.InvalidateRequerySuggested()。
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();

        /// <summary>触发一次 CanExecute 重新求值（等价于 CommandManager.InvalidateRequerySuggested）</summary>
        public static void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
    }
}
