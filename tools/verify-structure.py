#!/usr/bin/env python3
"""
结构一致性校验（编译管不到的几件事）。

用法：
    python tools/verify-structure.py

做四件事：
  1. XAML 里每个 {Binding Xxx} 的根标识符，必须在 C# 里有 public 声明。
     —— partial 拆分 / 重命名 / 删成员都不会报编译错误，绑定失效是静默的。
     只指向 DataContext 的绑定参与检查：带 RelativeSource / ElementName / Source= 的跳过。
  2. csproj 的 Compile/Page 清单与磁盘文件必须互相覆盖。
     —— 旧格式 csproj 漏登记只会静默不参与编译（AssemblyInfo.cs 就漏过）。
  3. 已删除的成员不应在任何 .cs 里残留引用。
  4. XAML 里的事件处理器（Click="Xxx" 等）必须在对应 code-behind 中存在。
     —— 写错名字不会报编译错误，运行时抛 XamlParseException。

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

# 需要在 code-behind 里有同名方法的 XAML 事件属性
EVENT_ATTRS = [
    'Click', 'SelectionChanged', 'MouseDown', 'MouseUp', 'MouseDoubleClick',
    'MouseLeftButtonDown', 'MouseRightButtonDown', 'TextChanged', 'Loaded',
    'Checked', 'Unchecked', 'Drop', 'DragOver', 'GotFocus', 'LostFocus',
    'StateChanged', 'Closing', 'Closed', 'KeyDown', 'KeyUp', 'ContextMenuOpening',
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


def read(path):
    return open(path, encoding='utf-8-sig').read()


def collect_public_members():
    names = set()
    for path in source_files():
        text = read(path)
        # public [static] [readonly] <Type> Name { | ( | = | ;
        names.update(re.findall(
            r'^\s*public\s+(?:static\s+|readonly\s+|virtual\s+|override\s+|async\s+|partial\s+)*'
            r'[\w<>\[\],\.\?]+\s+(\w+)\s*[\({=;]', text, re.M))
        # public <Type> {  (属性速写)
        names.update(re.findall(r'^\s*public\s+(\w+)\s*\{', text, re.M))
    return names


def iter_bindings(text):
    """产出每个 {Binding ...} 的完整表达式（按花括号配平，支持嵌套 RelativeSource={...}）。"""
    for m in re.finditer(r'\{Binding\b', text):
        start = m.start()
        depth = 0
        i = start
        while i < len(text):
            ch = text[i]
            if ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    break
            i += 1
        yield text[start:i + 1]


def binding_root(body):
    """从 {Binding 之后的表达式里取出绑定的根标识符；取不到返回空串。"""
    m = re.search(r'\bPath\s*=\s*([^,}]*)', body)
    if m:
        raw = m.group(1)
    else:
        raw = ''
        for part in body.split(','):
            part = part.strip()
            if part and '=' not in part:
                raw = part
                break
    return raw.strip().strip('{}').split('.')[0].split('[')[0].strip()


def check_bindings(members):
    problems = []
    checked = 0
    for path in xaml_files():
        for expr in iter_bindings(read(path)):
            body = expr[len('{Binding'):].strip()
            # 不指向 DataContext 的绑定：绑定到元素自身 / 具名元素 / 指定源
            if re.search(r'\b(RelativeSource|ElementName|Source)\s*=', body):
                continue
            root = binding_root(body)
            if not root:
                continue
            checked += 1
            if root not in members:
                problems.append('%s: {Binding %s} 在 C# 中找不到 public 成员'
                                % (os.path.relpath(path, SRC), root))
    return checked, sorted(set(problems))


def check_event_handlers():
    problems = []
    checked = 0
    for path in xaml_files():
        codebehind = path + '.cs'
        if not os.path.exists(codebehind):
            continue
        code = read(codebehind)
        text = read(path)
        for attr in EVENT_ATTRS:
            for m in re.finditer(r'\b%s="([A-Za-z_]\w*)"' % attr, text):
                name = m.group(1)
                checked += 1
                if not re.search(r'\b(?:void|Task|async)\s+' + name + r'\s*\(', code):
                    problems.append('%s: %s="%s" 在 %s 中找不到对应方法'
                                    % (os.path.relpath(path, SRC), attr, name,
                                       os.path.basename(codebehind)))
    return checked, sorted(set(problems))


def check_project_items():
    proj_path = os.path.join(SRC, 'MotionApiTester.csproj')
    proj = read(proj_path)

    listed = set()
    for tag in ('Compile', 'Page', 'ApplicationDefinition', 'Resource'):
        for m in re.finditer(r'<%s Include="([^"]+)"' % tag, proj):
            listed.add(m.group(1).replace('/', os.sep))

    # on_disk      : 磁盘上的全部文件 —— 登记了就必须存在，含 .ico 等资源
    # on_disk_build: 只有这些类型必须登记进 csproj（App.config / NuGet.config 等不必）
    on_disk, on_disk_build = set(), set()
    skip_dirs = (os.sep + 'obj' + os.sep, os.sep + 'bin' + os.sep, os.sep + '.vs' + os.sep)
    for path in glob.glob(os.path.join(SRC, '**', '*'), recursive=True):
        if os.path.isdir(path) or any(d in path for d in skip_dirs):
            continue
        rel = os.path.relpath(path, SRC)
        on_disk.add(rel)
        if rel.endswith(('.cs', '.xaml')):
            on_disk_build.add(rel)

    problems = []
    for rel in sorted(listed - on_disk):
        problems.append('csproj 列了但磁盘不存在: %s' % rel)
    for rel in sorted(on_disk_build - listed):
        problems.append('磁盘存在但 csproj 未登记（不会参与编译）: %s' % rel)
    return problems


def check_removed_members():
    problems = []
    for name in REMOVED_MEMBERS:
        for path in source_files():
            if re.search(r'\b' + name + r'\b', read(path)):
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

    checked_ev, event_problems = check_event_handlers()
    print('[4] 事件处理器：检查 %d 处，问题 %d 个' % (checked_ev, len(event_problems)))
    for p in event_problems:
        print('    ' + p)

    print_sizes()

    total = len(bind_problems) + len(proj_problems) + len(removed_problems) + len(event_problems)
    print('\n结果：%s' % ('全部通过' if total == 0 else '%d 个问题' % total))
    return 1 if total else 0


if __name__ == '__main__':
    sys.exit(main())
