using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MotionApiTester.Models;

namespace MotionApiTester.Services
{
    /// <summary>反射枚举器（容错版）
    /// 遍历程序集中所有公共类型和方法，标记 logger 参数，过滤编译器生成成员
    /// </summary>
    public class ReflectionEnumerator
    {
        /// <summary>枚举程序集中的所有 API</summary>
        public ApiAssembly Enumerate(Assembly assembly, string path)
        {
            var info = new ApiAssembly { Path = path, Name = assembly.GetName().Name ?? "" };

            // Costura 检测：单查 Costura.AssemblyLoader 类型即可，不必 GetTypes() 全量枚举 ——
            // 后者在缺依赖时会抛 ReflectionTypeLoadException，把「是否有嵌入」也一并丢掉。
            info.IsCostura = CosturaActivator.IsCosturaPack(assembly);
            if (info.IsCostura)
                info.EmbeddedCount = CosturaActivator.CountEmbeddedResources(assembly);

            // 枚举类型（容错：每个类型 try/catch）
            IReadOnlyList<Type> types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                int errCount = ex.LoaderExceptions?.Length ?? 0;
                info.LoadErrors.Add($"部分类型加载失败: {errCount} 个 LoaderException");

                // 详细记录前 5 个 LoaderException
                if (ex.LoaderExceptions != null)
                {
                    try
                    {
                        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MotionApiTester", "enum-errors.log");
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath));
                        using (var sw = new System.IO.StreamWriter(logPath, true))
                        {
                            sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {info.Path} - {errCount} errors");
                            int shown = 0;
                            foreach (var le in ex.LoaderExceptions)
                            {
                                if (le == null) continue;
                                sw.WriteLine($"  - {le.GetType().Name}: {le.Message}");
                                if (++shown >= 5) break;
                            }
                            sw.WriteLine();
                        }
                    }
                    catch { }
                }

                types = ex.Types.Where(t => t != null).ToArray();
            }
            catch (Exception ex)
            {
                info.LoadErrors.Add($"枚举失败: {ex.Message}");
                return info;
            }

            foreach (var type in types)
            {
                if (type == null) continue;
                if (IsCompilerGenerated(type)) continue;
                if (!type.IsPublic && !type.IsNestedPublic) continue;

                var apiType = new ApiType
                {
                    Name = type.Name,
                    Namespace = type.Namespace ?? "",
                    Kind = GetTypeKind(type)
                };

                // 枚举方法(包括构造函数)
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var method in methods)
                {
                    if (IsCompilerGenerated(method)) continue;

                    var parameters = method.GetParameters();
                    var apiMethod = new ApiMethod
                    {
                        MethodInfo = method, // 写入反射入口(ApiInvoker 需要)
                        Name = method.Name,
                        ReturnType = GetFriendlyName(method.ReturnType),
                        DeclaringType = type.Name,
                        IsStatic = method.IsStatic,
                        IsPublic = method.IsPublic,
                        HasLoggerParameter = parameters.Any(p => p.ParameterType == typeof(Action<string>)),
                        IsAsync = method.ReturnType == typeof(System.Threading.Tasks.Task)
                                || (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>)),
                    };

                    foreach (var param in parameters)
                    {
                        apiMethod.Parameters.Add(new ApiParameter
                        {
                            Name = param.Name ?? "",
                            TypeName = GetFriendlyName(param.ParameterType),
                            IsOptional = param.IsOptional,
                            DefaultValue = param.DefaultValue?.ToString()
                        });
                    }

                    if (method.IsSpecialName)
                        continue; // 跳过属性 getter/setter

                    if (method.IsConstructor)
                    {
                        apiMethod.Name = method.IsStatic ? $"#cctor" : "#ctor";
                        apiType.Constructors.Add(apiMethod);
                    }
                    else
                    {
                        apiType.Methods.Add(apiMethod);
                    }
                }

                // 枚举构造函数
                // 注意:GetMethods() 不返回构造函数,必须用 GetConstructors() 单独取,
                // 否则 ApiType.Constructors 恒为空、UI 的"构造函数"分组永不出现。
                try
                {
                    var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    foreach (var ctor in ctors)
                    {
                        if (IsCompilerGenerated(ctor)) continue;

                        var ctorParams = ctor.GetParameters();
                        var apiCtor = new ApiMethod
                        {
                            ConstructorInfo = ctor, // 反射入口(ApiInvoker 需要)
                            Name = "#ctor",
                            ReturnType = "void",
                            DeclaringType = type.Name,
                            IsStatic = false,
                            IsPublic = ctor.IsPublic,
                            HasLoggerParameter = ctorParams.Any(p => p.ParameterType == typeof(Action<string>)),
                            IsAsync = false,
                        };

                        foreach (var param in ctorParams)
                        {
                            apiCtor.Parameters.Add(new ApiParameter
                            {
                                Name = param.Name ?? "",
                                TypeName = GetFriendlyName(param.ParameterType),
                                IsOptional = param.IsOptional,
                                DefaultValue = param.DefaultValue?.ToString()
                            });
                        }

                        apiType.Constructors.Add(apiCtor);
                    }
                }
                catch { }

                // 枚举属性
                try
                {
                    var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    foreach (var prop in properties)
                    {
                        if (IsCompilerGenerated(prop)) continue;
                        apiType.Properties.Add(new ApiProperty
                        {
                            PropertyInfo = prop,
                            Name = prop.Name,
                            Type = GetFriendlyName(prop.PropertyType),
                            CanRead = prop.GetGetMethod() != null,
                            CanWrite = prop.GetSetMethod() != null,
                            IsStatic = prop.GetGetMethod()?.IsStatic ?? prop.GetSetMethod()?.IsStatic ?? false
                        });
                    }
                }
                catch { }

                // 枚举字段
                try
                {
                    var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    foreach (var field in fields)
                    {
                        if (IsCompilerGenerated(field)) continue;
                        apiType.Fields.Add(new ApiField
                        {
                            FieldInfo = field,
                            Name = field.Name,
                            Type = GetFriendlyName(field.FieldType),
                            IsStatic = field.IsStatic,
                            IsLiteral = field.IsLiteral,
                            IsInitOnly = field.IsInitOnly,
                            RawConstantValue = field.IsLiteral ? field.GetRawConstantValue()?.ToString() : null
                        });
                    }
                }
                catch { }

                info.Types.Add(apiType);
            }

            return info;
        }

        /// <summary>获取类型种类（class/interface/struct/enum）</summary>
        private static string GetTypeKind(Type type)
        {
            if (type.IsInterface) return "interface";
            if (type.IsEnum) return "enum";
            if (type.IsValueType) return "struct";
            return "class";
        }

        /// <summary>获取友好类型名称</summary>
        private static string GetFriendlyName(Type type)
        {
            if (type == typeof(void)) return "void";
            if (type == typeof(string)) return "string";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(double)) return "double";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(float)) return "float";
            if (type == typeof(decimal)) return "decimal";

            // 泛型
            if (type.IsGenericType)
            {
                var args = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyName));
                var name = type.Name.Split('`')[0];
                return name + "<" + args + ">";
            }

            // 数组
            if (type.IsArray)
                return GetFriendlyName(type.GetElementType()) + "[]";

            return type.Name;
        }

        /// <summary>检查是否为编译器生成</summary>
        private static bool IsCompilerGenerated(MemberInfo member)
        {
            return member.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
                || member.Name.Contains('<');
        }

        private static bool IsCompilerGenerated(Type type)
        {
            return type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
                || type.Name.Contains('<');
        }
    }
}
