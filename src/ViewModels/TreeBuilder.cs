using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionApiTester.Models;

namespace MotionApiTester.ViewModels
{
    /// <summary>
    /// 把反射枚举结果（ApiAssembly）转换成 TreeView 用的节点树。
    /// 层级：Assembly → Namespace → Type → Group(构造函数/方法/属性/字段) → Member
    ///
    /// 纯数据转换：不触碰 UI、不持有 ViewModel 状态，仅依赖传入的 keyword 参数，
    /// 因此可以脱离窗口单独验证。
    /// </summary>
    public class TreeBuilder
    {
        // 节点图标配色。这是随主题固定的语法高亮色（绑到 TreeViewItem 的 Foreground），
        // 不属于主题令牌体系；若后续要支持深色主题下的对比度，需要改成资源键 + 解析器。
        private const string ColorBlue = "#0078D4";
        private const string ColorInterface = "#107C10";
        private const string ColorStruct = "#2B88D8";
        private const string ColorEnum = "#CA5010";
        private const string ColorProperty = "#8764B8";
        private const string ColorField = "#737373";
        private const string ColorStatic = "#CA5010";
        private const string ColorNamespace = "#737373";

        /// <summary>成员类叶子节点：搜索裁剪时据此判断"不匹配即可整体丢弃"</summary>
        private static readonly HashSet<string> MemberKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "Method", "StaticMethod", "InstanceMethod", "Constructor", "Property", "Field"
        };

        /// <summary>构建完整节点树（不做关键词过滤）</summary>
        public List<TreeNodeVm> Build(IEnumerable<ApiAssembly> assemblies)
        {
            var roots = new List<TreeNodeVm>();
            if (assemblies == null) return roots;

            foreach (var asm in assemblies)
            {
                if (asm != null) roots.Add(BuildAssemblyNode(asm));
            }
            return roots;
        }

        /// <summary>
        /// 构建节点树并按关键词裁剪。
        /// <paramref name="keyword"/> 为空时等同全量构建，<paramref name="matchCount"/> 为 0。
        /// </summary>
        public List<TreeNodeVm> BuildFiltered(IEnumerable<ApiAssembly> assemblies, string keyword, out int matchCount)
        {
            matchCount = 0;
            if (string.IsNullOrWhiteSpace(keyword)) return Build(assemblies);

            var roots = new List<TreeNodeVm>();
            if (assemblies == null) return roots;

            int count = 0;
            foreach (var asm in assemblies)
            {
                if (asm == null) continue;
                var pruned = Prune(BuildAssemblyNode(asm), keyword, ref count);
                if (pruned != null) roots.Add(pruned);
            }

            matchCount = count;
            return roots;
        }

        /// <summary>构建单个程序集节点（含命名空间分组）</summary>
        private TreeNodeVm BuildAssemblyNode(ApiAssembly asm)
        {
            var asmNode = new TreeNodeVm
            {
                Label = Path.GetFileName(asm.Path) + (asm.Version != null ? $" v{asm.Version}" : ""),
                Icon = "⊞",
                IconColor = ColorBlue,
                NodeKind = "Assembly",
                Badge = asm.IsCostura ? $"📦 Costura · {asm.EmbeddedCount} 嵌入" : "",
                Payload = asm
            };

            // 按命名空间分组；空命名空间的类型直接挂在 Assembly 下
            var byNamespace = asm.Types
                .GroupBy(t => string.IsNullOrEmpty(t.Namespace) ? "(全局)" : t.Namespace)
                .OrderBy(g => g.Key);

            foreach (var nsGroup in byNamespace)
            {
                TreeNodeVm nsNode;
                if (nsGroup.Key == "(全局)")
                {
                    nsNode = asmNode;
                }
                else
                {
                    nsNode = new TreeNodeVm
                    {
                        Label = nsGroup.Key,
                        Icon = "📦",
                        IconColor = ColorNamespace,
                        NodeKind = "Namespace"
                    };
                    asmNode.Children.Add(nsNode);
                }

                foreach (var t in nsGroup)
                    nsNode.Children.Add(BuildTypeNode(t));
            }

            return asmNode;
        }

        /// <summary>构建类型节点（含构造函数/方法/属性/字段四个分组）</summary>
        private TreeNodeVm BuildTypeNode(ApiType t)
        {
            var typeNode = new TreeNodeVm
            {
                Label = t.Name,
                Icon = TypeIcon(t.Kind),
                IconColor = TypeColor(t.Kind),
                NodeKind = "Type",
                Badge = t.Kind,
                Payload = t
            };

            if (t.Constructors.Count > 0)
            {
                var grp = NewGroup("构造函数", "🔷", ColorBlue);
                foreach (var ctor in t.Constructors)
                {
                    grp.Children.Add(NewMember(ctor.Signature, "🔷", ColorBlue, "Constructor", ctor));
                }
                typeNode.Children.Add(grp);
            }

            if (t.Methods.Count > 0)
            {
                var grp = NewGroup($"方法 ({t.Methods.Count})", "⚙", ColorStatic);
                foreach (var m in t.Methods)
                {
                    grp.Children.Add(NewMember(
                        m.Signature,
                        m.IsStatic ? "⚡" : "⚙",
                        m.IsStatic ? ColorStatic : ColorBlue,
                        m.IsStatic ? "StaticMethod" : "InstanceMethod",
                        m));
                }
                typeNode.Children.Add(grp);
            }

            if (t.Properties.Count > 0)
            {
                var grp = NewGroup($"属性 ({t.Properties.Count})", "🔮", ColorProperty);
                foreach (var p in t.Properties)
                {
                    grp.Children.Add(NewMember(p.Signature, "🔮", ColorProperty, "Property", p));
                }
                typeNode.Children.Add(grp);
            }

            if (t.Fields.Count > 0)
            {
                var grp = NewGroup($"字段 ({t.Fields.Count})", "▣", ColorField);
                foreach (var f in t.Fields)
                {
                    grp.Children.Add(NewMember(f.Signature, "▣", ColorField, "Field", f));
                }
                typeNode.Children.Add(grp);
            }

            return typeNode;
        }

        private static TreeNodeVm NewGroup(string label, string icon, string color) => new TreeNodeVm
        {
            Label = label,
            Icon = icon,
            IconColor = color,
            NodeKind = "Group"
        };

        private static TreeNodeVm NewMember(string label, string icon, string color, string nodeKind, object payload)
            => new TreeNodeVm
            {
                Label = label,
                Icon = icon,
                IconColor = color,
                NodeKind = nodeKind,
                Payload = payload
            };

        /// <summary>
        /// 递归裁剪：只保留含匹配成员的节点。
        /// 返回 null 表示整棵子树可以丢弃；匹配到的成员计入 matchCount。
        /// </summary>
        private TreeNodeVm Prune(TreeNodeVm node, string keyword, ref int matchCount)
        {
            var kept = new List<TreeNodeVm>();
            foreach (var child in node.Children)
            {
                var pruned = Prune(child, keyword, ref matchCount);
                if (pruned != null) kept.Add(pruned);
            }

            node.Children.Clear();
            foreach (var c in kept) node.Children.Add(c);

            // 成员叶子：不匹配就丢弃
            if (MemberKinds.Contains(node.NodeKind))
            {
                if (!Matches(node.Label, keyword)) return null;
                matchCount++;
                return node;
            }

            // 中间节点：保留下来了子节点，或者自身标签命中
            if (node.Children.Count > 0 || Matches(node.Label, keyword)) return node;
            return null;
        }

        private static bool Matches(string text, string keyword) =>
            !string.IsNullOrEmpty(text)
            && text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;

        private static string TypeIcon(string kind)
        {
            switch (kind?.ToLowerInvariant())
            {
                case "interface": return "🟩";
                case "struct": return "🟦";
                case "enum": return "🟧";
                default: return "🟦"; // class
            }
        }

        private static string TypeColor(string kind)
        {
            switch (kind?.ToLowerInvariant())
            {
                case "interface": return ColorInterface;
                case "struct": return ColorStruct;
                case "enum": return ColorEnum;
                default: return ColorBlue; // class
            }
        }
    }
}
