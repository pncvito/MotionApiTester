#!/usr/bin/env python3
"""
结构一致性校验（编译管不到的几件事）。

用法：
    python tools/verify-structure.py

做三件事：
  1. XAML 里每个 {Binding Xxx} 的根标识符，必须在 C# 里有 public 声明。
     —— partial 拆分 / 重命名 / 删成员都不会报编译错误，绑定失效是静默的。
  2. csproj 的 Compile/Page 清单与磁盘文件必须互相覆盖。
     —— 旧格式 csproj 漏登记只会静默不参与编译（AssemblyInfo.cs 就漏过）。
  3. 已删除的成员不应在任何 .cs 里残留引用。

退出码非 0 表示发现问题。
"""
import os
import re
import sys
import glob

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'src')

# 已确认删除、不应再出现的成员
REMOVED_MEMBERS = [
    'CurrentView', 'ViewMode',
    'SwitchToApiCommand', 'SwitchToNativeCommand',
    'TypeFullName', 'AsyncRelayCommand',
    'TryDequeueLog', 'OnLog',
]


def source_files():
    for p in glob.glob(os.path.join(SRC, '**', '*.cs'), recursive=True):
        if os.sep + 'obj' + os.sep in p or os.sep + 'bin' + os.sep in p:
            continue
        yield p


def xaml_files():
    for p in glob.glob(os.path.join(SRC, '**', '*.xaml'), recursive=True):
        if os.sep + 'obj' + os.sep in p or os.sep + 'bin' + os.sep in p:
            continue
        yield p


def collect_public_members():
    names = set()
    for path in source_files():
        text = open(path, encoding='utf-8-sig').read()
        # public [static] [readonly] <Type> Name { | ( | = | ;
        names.update(re.findall(
            r'^\s*public\s+(?:static\s+|readonly\s+|virtual\s+|override\s+|async\s+|partial\s+)*'
            r'[\w<>\[\],\.\?]+\s+(\w+)\s*[\({=;]', text, re.M))
        # public <Type> {  (属性速写)
        names.update(re.findall(r'^\s*public\s+(\w+)\s*\{', text, re.M))
    return names


def check_bindings(members):
    problems = []
    checked = 0
    for path in xaml_files():
        text = open(path, encoding='utf-8-sig').read()
        for m in re.finditer(r'\{Binding\s+(?:Path=)?([^},]*)', text):
            root = m.group(1).strip().split('.')[0].split('[')[0].strip()
            if not root or root in ('RelativeSource', 'ElementName'):
                continue
            checked += 1
            if root not in members:
                problems.append('%s: {Binding %s} 在 C# 中找不到 public 成员' % (os.path.basename(path), root))
    return checked, sorted(set(problems))


def check_project_items():
    proj_path = os.path.join(SRC, 'MotionApiTester.csproj')
    proj = open(proj_path, encoding='utf-8-sig').read()

    listed = set()
    for tag in ('Compile', 'Page', 'ApplicationDefinition', 'Resource'):
        for m in re.finditer(r'<%s Include="([^"]+)"' % tag, proj):
            listed.add(m.group(1).replace('/', os.sep))

    on_disk = set()
    for path in glob.glob(os.path.join(SRC, '**', '*'), recursive=True):
        if os.path.isdir(path) or os.sep + 'obj' + os.sep in path or os.sep + 'bin' + os.sep in path:
            continue
        rel = os.path.relpath(path, SRC)
        if rel.endswith(('.cs', '.xaml')):
            on_disk.add(rel)

    problems = []
    for rel in sorted(listed - on_disk):
        problems.append('csproj 列了但磁盘不存在: %s' % rel)
    for rel in sorted(on_disk - listed):
        problems.append('磁盘存在但 csproj 未登记（不会参与编译）: %s' % rel)
    return problems


def check_removed_members():
    problems = []
    for name in REMOVED_MEMBERS:
        for path in source_files():
            text = open(path, encoding='utf-8-sig').read()
            if re.search(r'\b' + name + r'\b', text):
                problems.append('已删除成员 %s 仍被 %s 引用' % (name, os.path.relpath(path, SRC)))
    return problems


def print_sizes():
    rows = []
    for path in source_files():
        with open(path, encoding='utf-8-sig') as fh:
            rows.append((sum(1 for _ in fh), os.path.relpath(path, SRC)))
    print('\n--- 文件规模 Top 12 ---')
    for count, rel in sorted(rows, reverse=True)[:12]:
        print('  %5d  %s' % (count, rel))


def main():
    members = collect_public_members()

    checked, bind_problems = check_bindings(members)
    print('[1] 绑定面：检查 %d 处，问题 %d 个' % (checked, len(bind_problems)))
    for p in bind_problems:
        print('    ' + p)

    proj_problems = check_project_items()
    print('[2] csproj 清单：问题 %d 个' % len(proj_problems))
    for p in proj_problems:
        print('    ' + p)

    removed_problems = check_removed_members()
    print('[3] 已删除成员残留：问题 %d 个' % len(removed_problems))
    for p in removed_problems:
        print('    ' + p)

    print_sizes()

    total = len(bind_problems) + len(proj_problems) + len(removed_problems)
    print('\n结果：%s' % ('全部通过' if total == 0 else '%d 个问题' % total))
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main())
