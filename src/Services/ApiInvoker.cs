using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>
    /// API 方法调用器
    /// 支持:logger 自动注入、参数自动转换、接口/抽象类型自动解析具体实现、构造函数调用
    /// </summary>
    public class ApiInvoker
    {
        private readonly Dispatcher _dispatcher;
        private readonly LogTextBuffer _log;
        private CancellationTokenSource _cts;

        /// <summary>可用于解析接口实现的候选程序集(由加载流程登记,机型 DLL 放在最后)</summary>
        private readonly List<Assembly> _candidateAssemblies = new List<Assembly>();

        /// <summary>契约类型 → 具体实现类型 的缓存</summary>
        private readonly Dictionary<Type, Type> _implementationCache = new Dictionary<Type, Type>();

        /// <summary>
        /// 类型 → 实例 的缓存（重复调用复用同一实例，保留设备内部状态）。
        /// 键可能是"显式构造过的派生类型"，取用时按可赋值关系匹配，见 <see cref="FindReusable"/>。
        /// </summary>
        private readonly Dictionary<Type, object> _instanceCache = new Dictionary<Type, object>();

        /// <summary>调用完成回调（参数：方法 + 结果 + 实例）</summary>
        public Action<ApiMethod, InvokeResult, object> OnCompleted { get; set; }

        /// <summary>状态变化回调（用于 UI 进度反馈）</summary>
        public Action<string> OnStatusChanged { get; set; }

        /// <param name="log">日志出口，由 UI 层提供缓冲（见 LogTextBuffer）</param>
        public ApiInvoker(Dispatcher dispatcher, LogTextBuffer log)
        {
            _dispatcher = dispatcher;
            _log = log ?? new LogTextBuffer();
        }

        /// <summary>登记候选程序集(顺序有意义:越靠后越优先用于实现匹配)</summary>
        public void SetCandidateAssemblies(IEnumerable<Assembly> assemblies)
        {
            _candidateAssemblies.Clear();
            if (assemblies == null) return;

            foreach (var asm in assemblies)
            {
                if (asm == null) continue;
                if (!_candidateAssemblies.Contains(asm)) _candidateAssemblies.Add(asm);
            }
        }

        /// <summary>清空实例与实现缓存(卸载 / 切换设备时调用)</summary>
        public void ClearInstances()
        {
            _instanceCache.Clear();
            _implementationCache.Clear();
        }

        /// <summary>
        /// 解析可调用实例。
        ///
        /// <para>顺序：① 复用已建好、且能赋给 <paramref name="declaringType"/> 的实例；
        /// ② 否则找该类型「派生得最深的可无参构造实现」并实例化（接口 / 抽象类 / 具体基类一视同仁）。</para>
        /// </summary>
        public object ResolveInstance(Type declaringType)
        {
            if (declaringType == null) return null;

            // ① 已有实例优先 —— 设备侧只有一个对象，详见 FindReusable
            var reusable = FindReusable(declaringType);
            if (reusable != null) return reusable;

            // ② 找最深的可构造实现。
            //    ⚠️ 不能写成"具体类型直接 Activator.CreateInstance(declaringType)"：
            //    设备的方法按声明类型分散在继承链上，各建各的实例会让初始化作用不到动作方法上。
            var impl = FindImplementation(declaringType);
            if (impl == null)
            {
                throw new InvalidOperationException(
                    $"{declaringType.FullName} 是{(declaringType.IsInterface ? "接口" : declaringType.IsAbstract ? "抽象类" : "类型")}，"
                    + "且未在已加载的 DLL 中找到可无参构造的具体实现。"
                    + "请确认机型 DLL 已正确加载，或改用静态方法调用。");
            }

            if (impl != declaringType)
                EnqueueLog($"✓ 解析实现 {declaringType.Name} → {impl.FullName}");
            return GetOrCreateInstance(impl);
        }

        /// <summary>异步调用方法 / 构造函数（自动处理实例创建 + logger 注入 + 参数转换）</summary>
        public async Task<InvokeResult> InvokeAsync(ApiMethod method, string jsonArgsOverride = null)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var sw = Stopwatch.StartNew();
            object instance = null;

            try
            {
                var target = method.Target;
                if (target == null)
                    throw new InvalidOperationException("该成员没有可用的反射入口(MethodBase 为空)");

                OnStatusChanged?.Invoke($"⏳ 调用 {method.FullName}...");
                EnqueueLog($"=== 开始调用 {method.FullName} ===");

                // 1. 参数装配
                var paramInfos = target.GetParameters();
                var parameters = BuildParameters(method, paramInfos, jsonArgsOverride);

                // 2. 构造函数:Invoke 直接返回新建的实例
                if (method.IsConstructor)
                {
                    var created = await Task.Run(() => method.ConstructorInfo.Invoke(parameters), token);
                    sw.Stop();

                    // 把构造出来的实例登记成该类型的当前实例：用户点"构造函数"要的就是这个对象，
                    // 之后的实例方法调用必须落在同一个对象上 —— 否则带参数的构造等于白点
                    // （静态构造函数 #cctor 返回 null，天然被挡在外面）。
                    if (created != null)
                    {
                        _instanceCache[created.GetType()] = created;
                        EnqueueLog($"✓ 已登记为 {created.GetType().FullName} 的当前实例，后续调用将复用它");
                    }

                    var ctorResult = new InvokeResult
                    {
                        Success = true,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        ReturnValue = created,
                        Message = $"新建实例 {created?.GetType().FullName ?? "(null)"}"
                    };
                    EnqueueLog($"✅ 构造成功 ({sw.ElapsedMilliseconds}ms) → {ctorResult.Message}");
                    Post(() => OnCompleted?.Invoke(method, ctorResult, created));
                    OnStatusChanged?.Invoke($"✅ {method.FullName} → {ctorResult.Message}");
                    return ctorResult;
                }

                // 3. 实例方法:解析实例(接口 / 抽象类自动找实现)
                if (!method.IsStatic)
                {
                    instance = await Task.Run(() => ResolveInstance(target.DeclaringType), token);
                    EnqueueLog($"✓ 实例就绪 {instance?.GetType().FullName ?? "(null)"}");
                }

                // 4. 实际反射调用
                var rawResult = await Task.Run(() => target.Invoke(instance, parameters), token);
                sw.Stop();

                // 5. 设备侧"自报失败"：OptoFidelity 的 API 普遍把失败包在返回值里
                //    （典型签名 (bool ok, string message)），内部 catch 掉异常后
                //    return (false, ex.Message)，并不向调用方抛出。
                //    ⚠️ 只看异常的话这里会打出"✅ 调用成功" + 绿色结果卡片，用户会以为万事大吉 ——
                //    这比直接报错更误导：排障注意力会被引到依赖、参数这些无关方向去。
                string apiError;
                if (TryReadApiFailure(rawResult, out apiError))
                {
                    var failMsg = string.IsNullOrWhiteSpace(apiError) ? "(API 未给出错误信息)" : apiError;
                    var hint = BuildApiFailureHint(failMsg);

                    EnqueueLog($"❌ 调用未抛异常，但 API 自报失败 ({sw.ElapsedMilliseconds}ms)");
                    EnqueueLog($"   返回值: {FormatResult(rawResult)}");
                    EnqueueLog("   ⚠️ 这是设备侧方法体的执行结果（内部把异常转成了返回值），不是依赖缺失。");
                    if (hint != null) EnqueueLog($"   ⚠️ {hint}");

                    var failedResult = new InvokeResult
                    {
                        Success = false,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        ReturnValue = rawResult,
                        Message = $"API 返回失败: {failMsg}"
                    };
                    Post(() => OnCompleted?.Invoke(method, failedResult, instance));
                    OnStatusChanged?.Invoke($"❌ {method.Name} → API 返回失败: {failMsg}");
                    return failedResult;
                }

                var invokeResult = new InvokeResult
                {
                    Success = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    ReturnValue = rawResult,
                    Message = FormatResult(rawResult)
                };

                EnqueueLog($"✅ 调用成功 ({sw.ElapsedMilliseconds}ms)");
                EnqueueLog($"   返回值: {invokeResult.Message}");
                Post(() => OnCompleted?.Invoke(method, invokeResult, instance));
                OnStatusChanged?.Invoke($"✅ {method.Name} → {invokeResult.Message}");
                return invokeResult;
            }
            catch (TargetInvocationException ex)
            {
                sw.Stop();
                var inner = ex.InnerException ?? ex;
                var msg = $"{inner.GetType().Name}: {inner.Message}";
                EnqueueLog($"❌ 调用异常: {msg}");
                if (inner.StackTrace != null)
                {
                    foreach (var line in inner.StackTrace.Split('\n').Take(5))
                        EnqueueLog($"   {line.Trim()}");
                }

                // "缺运行库"和"API 自己出错"是两类问题，日志里必须分开说 ——
                // 否则很容易被误判成接口实现有 bug，跑去改代码。
                if (inner is FileNotFoundException || inner is FileLoadException || inner is TypeLoadException)
                    EnqueueLog("   ⚠️ 这是设备目录缺运行库（依赖解析失败），不是该 API 本身的问题；"
                             + "请看上方「依赖体检」一行，补齐 DLL 后重新加载设备。");

                // 原生 P/Invoke 与托管依赖是两套独立机制，报错也长得不一样，必须分开说。
                if (inner is DllNotFoundException)
                    EnqueueLog("   ⚠️ 这是原生 DLL 找不到（P/Invoke 失败），不是该 API 本身的问题。"
                             + "加载设备时会自动把设备目录加入原生搜索路径，"
                             + "若仍报错请确认目标 .dll 确实在该目录下。"
                             + "HRESULT 0x8007007E = ERROR_MOD_NOT_FOUND。");

                if (inner is BadImageFormatException)
                    EnqueueLog("   ⚠️ 位宽不匹配：该原生 DLL 是 32 位而本工具是 x64（或反之）。"
                             + "设备目录下所有 DLL（含 LTSMC.dll）都必须是 x64 版本。");

                var invokeResult = new InvokeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = msg
                };
                Post(() => OnCompleted?.Invoke(method, invokeResult, instance));
                OnStatusChanged?.Invoke($"❌ {method.Name}: {msg}");
                return invokeResult;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                var invokeResult = new InvokeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = "用户取消"
                };
                EnqueueLog("⏹ 用户取消");
                Post(() => OnCompleted?.Invoke(method, invokeResult, instance));
                OnStatusChanged?.Invoke("⏹ 调用已取消");
                return invokeResult;
            }
            catch (Exception ex)
            {
                sw.Stop();
                var msg = $"{ex.GetType().Name}: {ex.Message}";
                EnqueueLog($"❌ 异常: {msg}");

                var invokeResult = new InvokeResult
                {
                    Success = false,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = msg
                };
                Post(() => OnCompleted?.Invoke(method, invokeResult, instance));
                OnStatusChanged?.Invoke($"❌ {method.Name}: {msg}");
                return invokeResult;
            }
        }

        /// <summary>取消当前调用</summary>
        public void Cancel()
        {
            _cts?.Cancel();
        }

        /// <summary>写日志（线程安全；缓冲与保留行数由 LogTextBuffer 负责）</summary>
        private void EnqueueLog(string message) => _log.Write(message);

        /// <summary>把回调切回 UI 线程执行</summary>
        private void Post(Action action)
        {
            if (action == null) return;
            if (_dispatcher == null || _dispatcher.CheckAccess()) action();
            else _dispatcher.BeginInvoke(action);
        }

        // ============== 参数装配 ==============

        private object[] BuildParameters(ApiMethod method, ParameterInfo[] paramInfos, string jsonArgsOverride)
        {
            var parameters = new object[paramInfos.Length];

            // 高级输入:整组 JSON 覆盖逐参数输入
            if (!string.IsNullOrWhiteSpace(jsonArgsOverride))
            {
                if (TryApplyJsonArguments(jsonArgsOverride, paramInfos, parameters))
                    return parameters;

                EnqueueLog("⚠️ JSON 参数无效或个数不匹配，已回退到逐参数输入");
            }

            for (int i = 0; i < paramInfos.Length; i++)
            {
                var pi = paramInfos[i];
                var pm = method.Parameters.FirstOrDefault(p => p.Name == pi.Name);

                // Action<string> → 注入回调
                if (pi.ParameterType == typeof(Action<string>))
                {
                    parameters[i] = new Action<string>(msg => EnqueueLog($"[设备日志] {msg}"));
                    EnqueueLog($"✓ 注入 logger 参数: {pi.Name}");
                }
                else if (pm != null && pm.PassNull)
                {
                    // 用户勾选"传 null"
                    parameters[i] = null;
                    EnqueueLog($"  参数 {pi.Name} = null（用户勾选）");
                }
                else if (string.IsNullOrEmpty(pm?.Value))
                {
                    // 空值：用默认值
                    if (pi.HasDefaultValue)
                    {
                        parameters[i] = pi.DefaultValue;
                        EnqueueLog($"  参数 {pi.Name} 使用默认值: {pi.DefaultValue}");
                    }
                    else
                    {
                        parameters[i] = GetTypeDefault(pi.ParameterType);
                        EnqueueLog($"  参数 {pi.Name} 为空，使用类型默认值: {parameters[i] ?? "null"}");
                    }
                }
                else
                {
                    parameters[i] = ConvertValue(pm.Value, pi.ParameterType);
                    EnqueueLog($"  参数 {pi.Name} = {pm.Value} ({pi.ParameterType.Name})");
                }
            }

            return parameters;
        }

        /// <summary>把 JSON 数组按位置映射到参数。个数不匹配或解析失败返回 false</summary>
        private bool TryApplyJsonArguments(string json, ParameterInfo[] paramInfos, object[] parameters)
        {
            try
            {
                var elements = JsonSerializer.Deserialize<JsonElement[]>(json);
                if (elements == null || elements.Length != paramInfos.Length)
                {
                    EnqueueLog($"⚠️ JSON 参数个数 {elements?.Length ?? 0} 与方法参数个数 {paramInfos.Length} 不一致");
                    return false;
                }

                for (int i = 0; i < elements.Length; i++)
                    parameters[i] = ConvertJsonValue(elements[i], paramInfos[i].ParameterType);

                EnqueueLog($"✓ 已应用 JSON 参数（{elements.Length} 个）");
                return true;
            }
            catch (Exception ex)
            {
                EnqueueLog($"⚠️ JSON 解析失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>JSON 值 → 目标类型（复杂对象暂不支持，回退为类型默认值）</summary>
        private static object ConvertJsonValue(JsonElement element, Type targetType)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    return GetTypeDefault(targetType);

                case JsonValueKind.String:
                    return targetType == typeof(string)
                        ? (object)element.GetString()
                        : ConvertValue(element.GetString(), targetType);

                case JsonValueKind.Number:
                    return ConvertValue(element.GetRawText(), targetType);

                case JsonValueKind.True:
                case JsonValueKind.False:
                    return targetType == typeof(bool)
                        ? (object)element.GetBoolean()
                        : ConvertValue(element.ToString(), targetType);

                default:
                    return GetTypeDefault(targetType);
            }
        }

        /// <summary>将字符串转换为目标类型</summary>
        private static object ConvertValue(string raw, Type targetType)
        {
            if (targetType == typeof(string)) return raw;
            if (string.IsNullOrEmpty(raw))
                return GetTypeDefault(targetType);

            try
            {
                if (targetType.IsEnum)
                    return Enum.Parse(targetType, raw, ignoreCase: true);

                if (targetType == typeof(bool)) return bool.Parse(raw);
                if (targetType == typeof(int)) return int.Parse(raw);
                if (targetType == typeof(long)) return long.Parse(raw);
                if (targetType == typeof(short)) return short.Parse(raw);
                if (targetType == typeof(byte)) return byte.Parse(raw);
                if (targetType == typeof(double)) return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (targetType == typeof(float)) return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (targetType == typeof(decimal)) return decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
                if (targetType == typeof(DateTime)) return DateTime.Parse(raw);
                if (targetType == typeof(Guid)) return Guid.Parse(raw);

                return Convert.ChangeType(raw, targetType);
            }
            catch
            {
                // 转换失败，返回类型默认值
                return GetTypeDefault(targetType);
            }
        }

        /// <summary>获取类型默认值</summary>
        private static object GetTypeDefault(Type type)
        {
            if (type == typeof(string)) return "";
            if (type.IsValueType) return Activator.CreateInstance(type);
            return null;
        }

        // ============== 实例解析 ==============

        /// <summary>
        /// 在已建好的实例里找「能赋给 <paramref name="declaringType"/> 的那个对象」（即派生类实例）。
        ///
        /// <para><b>为什么必须这么做</b>：设备侧真正工作的只有一个对象，但它的成员按声明类型
        /// 分散在继承链上 —— 例如 <c>BaseInterface.InitializeFixture(Action&lt;string&gt;, string)</c>（初始化）
        /// 与 <c>EolSeriesBaseInterface.FixtureMoveToLoadUnloadingPosition(Action&lt;string&gt;)</c>（动作）。
        /// 若按"成员的声明类型"各建各的实例，初始化就永远作用不到动作上，
        /// 表现为动作方法一路 NullReferenceException —— 而日志里两个实例都显示"创建成功"，
        /// 光看日志根本发现不了。仓库里 <c>ReflectionEnumerator</c> 用的是
        /// <c>DeclaredOnly</c>，成员只会挂在声明它的那个类型节点下，所以这种错位一定会发生。</para>
        /// </summary>
        private object FindReusable(Type declaringType)
        {
            if (_instanceCache.TryGetValue(declaringType, out var exact) && exact != null)
                return exact;

            // 有多个派生实例时取派生最深的（最接近设备真正使用的那个类型）
            object best = null;
            int bestDepth = -1;
            foreach (var entry in _instanceCache)
            {
                var value = entry.Value;
                if (value == null) continue;

                var type = value.GetType();
                if (!declaringType.IsAssignableFrom(type)) continue;

                var depth = DerivationDepth(type);
                if (depth <= bestDepth) continue;

                best = value;
                bestDepth = depth;
            }

            return best;
        }

        /// <summary>继承链深度（<see cref="object"/> 记 0，越靠近叶子越大）</summary>
        private static int DerivationDepth(Type type)
        {
            int depth = 0;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType) depth++;
            return depth;
        }

        /// <summary>
        /// 在候选程序集中查找 <paramref name="contract"/> 的具体实现。
        ///
        /// <para>优先「派生得最深」的那个 —— 设备只认一个对象：基类上的 InitializeFixture
        /// 必须落到派生类的动作方法上，选基类本身等于白初始化。同深度时沿用原优先级
        /// （程序集登记顺序 → 类名最短）。</para>
        /// </summary>
        private Type FindImplementation(Type contract)
        {
            if (_implementationCache.TryGetValue(contract, out var cached)) return cached;

            var candidates = new List<Type>();
            foreach (var asm in EnumerateCandidateAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var t in types)
                {
                    if (t == null || !t.IsClass || t.IsAbstract) continue;
                    if (!contract.IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) == null) continue; // 只接受可无参构造的实现
                    candidates.Add(t);
                }
            }

            var impl = candidates
                .OrderByDescending(DerivationDepth)                              // 越靠近叶子越优先：设备真正在用的就是那个对象
                .ThenByDescending(t => _candidateAssemblies.IndexOf(t.Assembly)) // 同深度再按原来的程序集优先级
                .ThenBy(t => t.Name.Length)
                .FirstOrDefault();

            if (impl != null) _implementationCache[contract] = impl;
            return impl;
        }

        private IEnumerable<Assembly> EnumerateCandidateAssemblies()
        {
            foreach (var asm in _candidateAssemblies)
                yield return asm;

            // 兜底:Costura 展开后可能挂在当前 AppDomain 但没被登记的依赖
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm == null || asm.IsDynamic) continue;
                if (_candidateAssemblies.Contains(asm)) continue;
                yield return asm;
            }
        }

        /// <summary>取得实例(同类型复用,保留设备内部状态)</summary>
        private object GetOrCreateInstance(Type type)
        {
            if (_instanceCache.TryGetValue(type, out var existing) && existing != null)
                return existing;

            var created = Activator.CreateInstance(type);
            _instanceCache[type] = created;
            EnqueueLog($"✓ 实例化 {type.FullName}");
            return created;
        }

        /// <summary>
        /// 识别「设备 API 自报失败」的返回值约定：元组首项为 bool 且为 false。
        ///
        /// <para>不写死 <c>(bool, string)</c> 这一种形态，而是按 <c>Item1/Item2</c> 反射取值 ——
        /// 这样 <c>ValueTuple&lt;bool,string&gt;</c> / <c>Tuple&lt;bool,string&gt;</c> 以及更长元组都能认，
        /// 也不会因为设备侧换了 TFM 就编不过。</para>
        /// </summary>
        private static bool TryReadApiFailure(object result, out string message)
        {
            message = null;
            if (result == null) return false;

            var type = result.GetType();
            if (!type.IsGenericType) return false;

            var fullName = type.FullName ?? "";
            if (!fullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal)
                && !fullName.StartsWith("System.Tuple`", StringComparison.Ordinal))
                return false;

            object first;
            try
            {
                // ValueTuple 是公共字段，Tuple 是属性 —— 两条都试，避免判型
                first = type.GetField("Item1")?.GetValue(result)
                        ?? type.GetProperty("Item1")?.GetValue(result);
            }
            catch { return false; }

            if (!(first is bool ok) || ok) return false;

            try
            {
                var second = type.GetField("Item2")?.GetValue(result)
                             ?? type.GetProperty("Item2")?.GetValue(result);
                message = second?.ToString();
            }
            catch { message = null; }

            return true;
        }

        /// <summary>
        /// 设备 API 自报失败时补一句最可能的成因。
        ///
        /// <para>目前只认 NullReferenceException 这一种 —— 它在这类 API 里几乎总是
        /// 「实例没被初始化」（无参构造出来的对象，内部夹具 / 硬件 / 日志字段还是 null），
        /// 而不是缺 DLL：真缺 DLL 会在进入方法体之前就抛 FileNotFoundException，
        /// 根本轮不到方法体里的 catch。</para>
        /// </summary>
        private static string BuildApiFailureHint(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;

            var isNre = message.IndexOf("未将对象引用设置到对象的实例", StringComparison.Ordinal) >= 0
                     || message.IndexOf("Object reference not set to an instance", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isNre) return null;

            return "NullReferenceException 是设备 API 内部抛出并自行捕获的。最常见原因是实例未初始化 ——"
                 + "本工具用无参构造创建实例，不会执行设备侧的初始化流程（从 DI 容器取实例 / Init / Open / Connect 之类）。"
                 + "请先调用该类型的初始化方法再重试；这与「依赖缺失」无关。";
        }

        /// <summary>格式化返回值</summary>
        private static string FormatResult(object result)
        {
            if (result == null) return "(null)";
            if (result is string s) return $"\"{s}\"";
            if (result is System.Collections.IEnumerable enumerable && !(result is string))
            {
                var items = new List<string>();
                foreach (var item in enumerable) items.Add(item?.ToString() ?? "null");
                return $"[{items.Count} 项] {string.Join(", ", items.Take(10))}{(items.Count > 10 ? "..." : "")}";
            }
            return result.ToString();
        }
    }

    public class InvokeResult
    {
        /// <summary>
        /// 调用成功 = 反射调用未抛异常 **且** 设备 API 未在返回值里自报失败
        /// （见 <c>ApiInvoker.TryReadApiFailure</c>）。只看异常会把
        /// <c>(false, ex.Message)</c> 这类失败误报成成功。
        /// </summary>
        public bool Success { get; set; }
        public long ElapsedMs { get; set; }
        public string Message { get; set; } = "";
        public object ReturnValue { get; set; }
    }
}
