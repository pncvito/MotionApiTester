using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MotionApiTester.Services
{
    /// <summary>
    /// MachineType.json 模糊匹配器
    /// 读取机型字符串，与设备目录中的 DLL 做 Jaccard 相似度匹配
    /// </summary>
    public static class MachineTypeMatcher
    {
        /// <summary>找出最佳匹配</summary>
        public static string FindBestMatch(string machineType, IEnumerable<string> dllNames)
        {
            var mtWords = SplitWords(machineType);
            return dllNames
                .Select(d => new { Dll = d, Sim = Jaccard(mtWords, SplitWords(d)) })
                .OrderByDescending(x => x.Sim)
                .Select(x => x.Dll)
                .FirstOrDefault();
        }

        /// <summary>返回所有候选的相似度排序（用于弹窗展示）</summary>
        public static List<(string Dll, double Similarity)> RankMatches(string machineType, IEnumerable<string> dllNames)
        {
            var mtWords = SplitWords(machineType);
            return dllNames
                .Select(d => (Dll: d, Sim: Jaccard(mtWords, SplitWords(d))))
                .OrderByDescending(x => x.Sim)
                .ToList();
        }

        /// <summary>拆词：去前缀/后缀 + 正则分词</summary>
        private static string[] SplitWords(string s) =>
            Regex.Split(
                s.ToLowerInvariant()
                 .Replace("optofidelity.", "")
                 .Replace(".dll", "")
                 .Replace("tester", "")
                 .Replace("wrapper", ""),
                @"[^a-z0-9]+")
            .Where(w => !string.IsNullOrEmpty(w))
            .ToArray();

        /// <summary>Jaccard 相似度</summary>
        private static double Jaccard(string[] a, string[] b)
        {
            var setA = new HashSet<string>(a);
            var setB = new HashSet<string>(b);
            var inter = setA.Intersect(setB).Count();
            var union = setA.Union(setB).Count();
            return union == 0 ? 0 : (double)inter / union;
        }
    }
}
