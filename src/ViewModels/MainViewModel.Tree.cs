using System;
using System.Windows;

namespace MotionApiTester.ViewModels
{
    /// <summary>搜索过滤与树数据接线。树的构建算法在 TreeBuilder。</summary>
    public partial class MainViewModel
    {
        /// <summary>
        /// 清空树与搜索状态（卸载时调用）。
        /// 注意：TreeView 绑定的是 FilteredTreeRoots，不是 Assemblies ——
        /// 只清 Assemblies 树不会消失，必须重建过滤集合。
        /// </summary>
        private void ClearTreeState()
        {
            Assemblies.Clear();

            // 搜索框文本也一并清掉（否则会残留 "🔍 xxx → 0 个匹配" 的误导状态）
            if (!string.IsNullOrEmpty(_searchText))
            {
                _searchText = string.Empty;
                OnPropertyChanged(nameof(SearchText));
            }

            SelectedTreeNode = null;   // 内部会同步清 SelectedMethod/Property/Field 与签名
            RefreshSearch();           // 按空集合重建 → FilteredTreeRoots 清空
        }

        /// <summary>刷新树数据：按 SearchText 重建 FilteredTreeRoots</summary>
        private void RefreshSearch()
        {
            FilteredTreeRoots.Clear();

            if (string.IsNullOrWhiteSpace(_searchText))
            {
                foreach (var node in _treeBuilder.Build(Assemblies)) FilteredTreeRoots.Add(node);

                // 从"搜索结果"状态回到就绪状态（首次加载路径已经设过状态文本，这里不覆盖）
                if (Assemblies.Count > 0 && StatusText.StartsWith("🔍"))
                    StatusText = "✅ 就绪";
                return;
            }

            var roots = _treeBuilder.BuildFiltered(Assemblies, _searchText, out int matchCount);
            foreach (var node in roots) FilteredTreeRoots.Add(node);

            StatusText = $"🔍 \"{_searchText}\" → {matchCount} 个匹配";
        }

        /// <summary>聚焦搜索框（由 Ctrl+F 调用）</summary>
        private void SearchFocus()
        {
            if (Application.Current?.MainWindow is MainWindow window)
                window.FocusSearchBox();
        }
    }
}
