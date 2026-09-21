using System;
using System.Windows;

namespace MotionApiTester.ViewModels
{
    /// <summary>搜索过滤与树数据接线。树的构建算法在 TreeBuilder。</summary>
    public partial class MainViewModel
    {
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
