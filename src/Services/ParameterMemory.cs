using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MotionApiTester.Services
{
    /// <summary>
    /// 方法参数值记忆。
    ///
    /// <para>设备调试的日常就是反复调那几个方法、反复填同样的参数（配方名、轴号、位置索引、偏移量……）。
    /// 不记住的话每次都要重敲，而且很容易敲错 —— 敲错的参数往往要等设备动起来才发现。</para>
    ///
    /// <para>键用<b>方法签名</b>（<c>MethodBase.ToString()</c>，含声明类型与全部参数类型），
    /// 这样重载之间不会串味，重新加载设备 DLL 后也依然对得上。</para>
    /// </summary>
    public class ParameterMemory
    {
        private readonly string _path;
        private Dictionary<string, RememberedParameters> _store =
            new Dictionary<string, RememberedParameters>(StringComparer.Ordinal);

        /// <summary>记忆条目数（设置/日志里显示用）</summary>
        public int Count => _store.Count;

        public ParameterMemory(string path = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MotionApiTester");
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, "param-values.json");
            }

            _path = path;
            Load();
        }

        /// <summary>取出某个方法上次用过的参数值；没有记过返回 null</summary>
        public RememberedParameters Get(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return null;
            return _store.TryGetValue(signature, out var v) ? v : null;
        }

        /// <summary>
        /// 记住一次调用用过的参数值。
        /// 只记用户真正填过的（空值不记），否则"什么都是空"会把上一次有效值覆盖掉。
        /// </summary>
        public void Remember(string signature, IEnumerable<RememberedParameters.ParameterValue> values)
        {
            if (string.IsNullOrEmpty(signature) || values == null) return;

            var entry = new RememberedParameters();
            foreach (var v in values)
            {
                if (v == null || string.IsNullOrEmpty(v.Name)) continue;
                if (v.PassNull) entry.Nulls.Add(v.Name);
                else if (!string.IsNullOrEmpty(v.Value)) entry.Values[v.Name] = v.Value;
            }

            if (entry.Values.Count == 0 && entry.Nulls.Count == 0) return;

            // 值完全没变就不写盘（调用频繁时避免每次调用都落一次盘）
            var old = Get(signature);
            if (old != null && old.SameAs(entry)) return;

            _store[signature] = entry;
            Save();
        }

        /// <summary>清掉全部记忆（界面上的"清空参数记忆"用）</summary>
        public void Clear()
        {
            _store.Clear();
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, RememberedParameters>>(json);
                if (loaded != null) _store = new Dictionary<string, RememberedParameters>(loaded, StringComparer.Ordinal);
            }
            catch { /* 损坏就当作没有记忆，不能因此影响启动 */ }
        }

        private void Save()
        {
            try
            {
                File.WriteAllText(_path,
                    JsonSerializer.Serialize(_store, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 写不了就算了，记忆不是关键路径 */ }
        }
    }

    /// <summary>某一个方法上次用过的参数值</summary>
    public class RememberedParameters
    {
        /// <summary>参数名 → 值</summary>
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>勾了"传 null"的参数名（out/ref 场景常用）</summary>
        public List<string> Nulls { get; set; } = new List<string>();

        /// <summary>界面回调用的参数快照（不参与序列化，因为字段名刻意与 JSON 不同）</summary>
        public sealed class ParameterValue
        {
            public string Name { get; set; } = "";
            public string Value { get; set; } = "";
            public bool PassNull { get; set; }
        }

        public bool SameAs(RememberedParameters other)
        {
            if (other == null) return false;
            if (Values.Count != other.Values.Count || Nulls.Count != other.Nulls.Count) return false;

            foreach (var kv in Values)
                if (!other.Values.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;

            return !Nulls.Except(other.Nulls, StringComparer.Ordinal).Any()
                && !other.Nulls.Except(Nulls, StringComparer.Ordinal).Any();
        }
    }
}
