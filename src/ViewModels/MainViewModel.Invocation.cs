using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MotionApiTester.Models;
using MotionApiTester.Services;

namespace MotionApiTester.ViewModels
{
    /// <summary>调用分发、选中态同步、调用历史。</summary>
    public partial class MainViewModel
    {
        /// <summary>
        /// 写入调用历史（界面集合 + 磁盘）。方法与属性/字段读取共用 ——
        /// 属性/字段以前不记录，历史面板里只有方法，与"按一下键就调用"的实际操作对不上。
        /// </summary>
        private void RecordHistory(CallHistoryItem item)
        {
            _historyService.Record(item);
            _historyItems.Insert(0, item);
            if (_historyItems.Count > 200) _historyItems.RemoveAt(_historyItems.Count - 1);
            _historyService.Save();
        }

        /// <summary>构造函数的调用完成回调：写历史 + 刷新结构化结果字段</summary>
        private void OnInvocationCompleted(ApiMethod method, InvokeResult result, object instance)
        {
            RecordHistory(new CallHistoryItem
            {
                MethodName = method.FullName,
                Parameters = string.Join(", ", method.Parameters.Select(p =>
                    p.IsLogger ? "[logger]" : $"{p.Name}={p.Value}")),
                Result = result.Message,
                ElapsedMs = result.ElapsedMs,
                Success = result.Success,
                Timestamp = DateTime.Now
            });

            LastElapsedMs = result.ElapsedMs;
            LastCallTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            ResultSuccessFlag = result.Success;

            // 结构化字段
            ResultReturnType = method.IsConstructor
                ? "(构造函数)"
                : (method.MethodInfo?.ReturnType.FullName ?? "void");
            ResultReturnValue = result.ReturnValue?.ToString() ?? "(null)";
            ResultInstanceType = method.Target?.DeclaringType?.FullName ?? "—";
            ResultThread = "Background (Task.Run)";
            ResultFullMessage = result.Message;

            ResultText = (result.Success ? "✅ " : "❌ ") +
                $"{method.Name} ({result.ElapsedMs}ms)\n{result.Message}";
            IsInvoking = false;
        }

        /// <summary>清空调用历史（界面 + 磁盘）</summary>
        private void ClearHistory()
        {
            _historyService.Clear();
            _historyItems.Clear();
            StatusText = "已清空调用历史";
        }

        /// <summary>调用当前选中的方法 / 构造函数 / 属性 / 字段</summary>
        private async Task InvokeSelectedAsync()
        {
            // 1. 方法与构造函数（统一走 Target 入口）
            var method = SelectedMethod;
            if (method?.Target != null)
            {
                IsInvoking = true;
                try { await _invoker.InvokeAsync(method, JsonArgsOverride); }
                catch (Exception ex) { StatusText = $"❌ 调用异常: {ex.Message}"; }
                finally { IsInvoking = false; }
                return;
            }

            // 2. 属性 get
            var prop = SelectedProperty;
            if (prop?.PropertyInfo != null)
            {
                if (!prop.CanRead)
                {
                    StatusText = $"⚠️ 属性 {prop.Name} 为只写，无法读取";
                    return;
                }

                IsInvoking = true;
                try
                {
                    var pi = prop.PropertyInfo;
                    var value = await ReadMemberAsync(pi.DeclaringType, prop.IsStatic, inst => pi.GetValue(inst));
                    ReportMemberReadSuccess("属性", prop.Name, value, pi.PropertyType, pi.DeclaringType, prop.IsStatic);
                }
                catch (Exception ex) { ReportMemberReadFailure("属性", prop.Name, prop.PropertyInfo, ex); }
                finally { IsInvoking = false; }
                return;
            }

            // 3. 字段 get
            var field = SelectedField;
            if (field?.FieldInfo != null)
            {
                IsInvoking = true;
                try
                {
                    var fi = field.FieldInfo;
                    var value = await ReadMemberAsync(fi.DeclaringType, field.IsStatic, inst => fi.GetValue(inst));
                    ReportMemberReadSuccess("字段", field.Name, value, fi.FieldType, fi.DeclaringType, fi.IsStatic);
                }
                catch (Exception ex) { ReportMemberReadFailure("字段", field.Name, field.FieldInfo, ex); }
                finally { IsInvoking = false; }
                return;
            }

            StatusText = "❌ 未选中可调用项（请选择方法 / 构造函数 / 属性 / 字段）";
        }

        /// <summary>读取属性 / 字段的公共路径：后台线程 + 接口实现解析 + 统一计时</summary>
        private async Task<object> ReadMemberAsync(Type declaringType, bool isStatic, Func<object, object> read)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var value = await Task.Run(() =>
            {
                var instance = isStatic ? null : _invoker.ResolveInstance(declaringType);
                return read(instance);
            });
            sw.Stop();

            LastElapsedMs = sw.ElapsedMilliseconds;
            LastCallTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            return value;
        }

        private void ReportMemberReadSuccess(string kind, string name, object value,
                                              Type valueType, Type declaringType, bool isStatic)
        {
            // 复用 ApiInvoker 的格式化：数组 / 集合会打成 "[3 项] 1.2, 3.4, 5.6"，而不是 "System.Double[]"
            var text = ApiInvoker.FormatResult(value);
            var member = $"{declaringType?.Name ?? "?"}.{name}{(isStatic ? " (static)" : "")}";

            ResultSuccessFlag = true;
            ResultReturnValue = text;
            ResultReturnType = valueType?.FullName ?? "—";
            ResultInstanceType = declaringType?.FullName ?? "—";
            ResultThread = "Background (Task.Run)";
            ResultFullMessage = $"{kind} {member} = {text}";
            ResultText = $"✅ {kind} {member} ({LastElapsedMs}ms)\n{ResultFullMessage}";
            StatusText = $"✅ {name} = {text}";

            RecordHistory(new CallHistoryItem
            {
                MethodName = member,
                Parameters = $"{kind}读取",
                Result = text,
                ElapsedMs = LastElapsedMs,
                Success = true,
                Timestamp = DateTime.Now
            });
        }

        private void ReportMemberReadFailure(string kind, string name, MemberInfo memberInfo, Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            var member = $"{memberInfo?.DeclaringType?.Name ?? "?"}.{name}";

            ResultSuccessFlag = false;
            ResultFullMessage = $"{inner.GetType().Name}: {inner.Message}";
            ResultText = $"❌ {kind} {member}\n{ResultFullMessage}";
            StatusText = $"❌ {kind}读取异常: {inner.Message}";

            // 失败也记 —— 只有成功的记录会让历史看起来"怎么调都顺"
            RecordHistory(new CallHistoryItem
            {
                MethodName = member,
                Parameters = $"{kind}读取",
                Result = ResultFullMessage,
                ElapsedMs = LastElapsedMs,
                Success = false,
                Timestamp = DateTime.Now
            });
        }

        /// <summary>选中节点后，更新 SelectedMethod / SelectedProperty / SelectedField / 签名 / 依赖项</summary>
        private void UpdateSelection()
        {
            var node = _selectedTreeNode;
            SelectedMethod = node?.Method;
            SelectedProperty = node?.Property;
            SelectedField = node?.Field;

            // 换了选中项，上一组 JSON 参数不再适用，清空避免误调用
            JsonArgsOverride = "";

            if (node?.Method != null) SignatureText = node.Method.Signature;
            else if (node?.Property != null) SignatureText = node.Property.Signature;
            else if (node?.Field != null) SignatureText = node.Field.Signature;
            else SignatureText = "";

            MethodDependencies = BuildDependencies(node);

            // 选中类型节点时，直接把类型概览写进结果区
            if (node?.NodeKind == "Type" && node.Payload is ApiType t)
            {
                ResultText = $"类型: {t.FullName}\n命名空间: {t.Namespace}\n种类: {t.Kind}\n" +
                             $"成员数: 方法 {t.Methods.Count}, 属性 {t.Properties.Count}, " +
                             $"字段 {t.Fields.Count}, 构造函数 {t.Constructors.Count}";
            }
        }

        /// <summary>根据选中节点构造"依赖项" Tab 的内容</summary>
        private static List<DependencyInfo> BuildDependencies(TreeNodeVm node)
        {
            var deps = new List<DependencyInfo>();

            if (node?.Method?.Target != null)
            {
                var target = node.Method.Target;

                if (node.Method.IsConstructor)
                    deps.Add(new DependencyInfo { Kind = "构造类型", Name = target.DeclaringType?.FullName ?? "—" });
                else if (target is MethodInfo mi)
                    deps.Add(new DependencyInfo { Kind = "返回类型", Name = mi.ReturnType.FullName });

                foreach (var p in target.GetParameters())
                {
                    deps.Add(new DependencyInfo
                    {
                        Kind = p.IsOut ? "参数(out)" : "参数",
                        Name = $"{p.ParameterType.FullName} {p.Name}"
                    });
                }
            }
            else if (node?.Property?.PropertyInfo != null)
            {
                var pi = node.Property.PropertyInfo;
                deps.Add(new DependencyInfo { Kind = "属性类型", Name = pi.PropertyType.FullName });
                deps.Add(new DependencyInfo { Kind = "声明类型", Name = pi.DeclaringType.FullName });
            }
            else if (node?.Field?.FieldInfo != null)
            {
                var fi = node.Field.FieldInfo;
                deps.Add(new DependencyInfo { Kind = "字段类型", Name = fi.FieldType.FullName });
                deps.Add(new DependencyInfo { Kind = "声明类型", Name = fi.DeclaringType.FullName });
            }

            return deps;
        }
    }
}
