#!/usr/bin/env python3
"""校验 .ico 的结构自洽性（纯标准库，不依赖任何第三方包）。

背景
----
`src/app.ico` 曾出现「目录表损坏」：ICONDIRENTRY 的后 8 字节
（dwBytesInRes / dwImageOffset）被写成了 1 / 118,119,120… 的递增值，
而像素数据完全正常。Windows 资源管理器对坏目录比较宽容，肉眼看不出来；
但 WPF 的 ImageSource 走 WIC，读目录拿到垃圾后直接抛
XamlParseException + FileFormatException（0x88982F60 图像无法识别），
表现为「程序一启动就崩」。改图标或换图标文件后务必跑一遍本脚本。

用法
----
    python tools/verify-ico.py                 # 默认校验 src/app.ico
    python tools/verify-ico.py 路径1 路径2 ...  # 校验指定文件

退出码 0 = 通过，1 = 有问题。
"""

import os
import struct
import sys

# Windows 控制台编码可能是 cp1252/cp936，直接 print 中文会抛 UnicodeEncodeError。
# 统一切到 UTF-8；若终端本身不是 UTF-8（chcp 不等于 65001），中文会显示成乱码，
# 先执行 chcp 65001，或用 VS Code / Windows Terminal 的内置终端。
try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

PNG_SIG = b'\x89PNG\r\n\x1a\n'
DIB_SIG = b'\x28\x00\x00\x00'


def check(path):
    problems = []
    d = open(path, 'rb').read()
    if len(d) < 6:
        return ['文件过短，不是 ICO']

    reserved, typ, count = struct.unpack_from('<HHH', d, 0)
    if reserved != 0:
        problems.append('ICONDIR.reserved 应为 0，实际 %d' % reserved)
    if typ != 1:
        problems.append('ICONDIR.type 应为 1(ICO)，实际 %d' % typ)
    if count <= 0:
        return problems + ['图像数为 0']

    dir_end = 6 + 16 * count
    if dir_end > len(d):
        return problems + ['目录区需要 %d 字节，文件只有 %d' % (dir_end, len(d))]

    entries = []
    for i in range(count):
        bw, bh, _cc, _r, _pl, bpp, size, ofs = struct.unpack_from('<BBBBHHII', d, 6 + i * 16)
        entries.append(dict(i=i, w=bw or 256, h=bh or 256, bpp=bpp, size=size, ofs=ofs))

    for e in entries:
        tag = '#%d %dx%d' % (e['i'], e['w'], e['h'])
        if e['bpp'] not in (1, 4, 8, 24, 32):
            problems.append('%s: bpp=%d 不是合法位深' % (tag, e['bpp']))
        if e['size'] == 0:
            problems.append('%s: dwBytesInRes = 0（目录表损坏的典型症状）' % tag)
        elif e['ofs'] < dir_end or e['ofs'] + e['size'] > len(d):
            problems.append('%s: 数据区间 [%d, %d) 越界或与目录区重叠'
                            % (tag, e['ofs'], e['ofs'] + e['size']))

    ordered = sorted(entries, key=lambda e: e['ofs'])
    cur = dir_end
    for e in ordered:
        if e['size'] and e['ofs'] != cur:
            problems.append('#%d: 数据区不连续，期望 offset=%d，实际 %d'
                            % (e['i'], cur, e['ofs']))
        cur = e['ofs'] + e['size']
    if cur != len(d):
        problems.append('数据区末尾 %d 与文件长度 %d 不符（有未声明的内容或被截断）'
                        % (cur, len(d)))

    for e in entries:
        if not e['size']:
            continue
        tag = '#%d %dx%d' % (e['i'], e['w'], e['h'])
        blob = d[e['ofs']:e['ofs'] + e['size']]
        if blob[:8] == PNG_SIG:
            end = blob.rfind(b'IEND')
            if end < 0:
                problems.append('%s: 声明为 PNG 却找不到 IEND' % tag)
            elif end + 8 != e['size']:
                problems.append('%s: PNG 实际 %d 字节，目录声明 %d'
                                % (tag, end + 8, e['size']))
        elif blob[:4] == DIB_SIG:
            iw, ih = struct.unpack_from('<ii', blob, 4)
            if iw != e['w']:
                problems.append('%s: DIB 的 biWidth=%d 与目录不符' % (tag, iw))
            if ih != e['h'] * 2:
                problems.append('%s: DIB 的 biHeight=%d，应为高度的 2 倍(%d)'
                                % (tag, ih, e['h'] * 2))
            expect = 40 + e['w'] * e['h'] * 4 + ((e['w'] + 31) // 32) * 4 * e['h']
            if e['size'] != expect:
                problems.append('%s: 32bpp DIB 应为 %d 字节(含 AND 掩码)，目录声明 %d'
                                % (tag, expect, e['size']))
        else:
            problems.append('%s: 数据既非 PNG 也非 DIB，首 4 字节 = %s'
                            % (tag, blob[:4].hex()))

    sizes = ['%dx%d' % (e['w'], e['h']) for e in entries]
    dup = sorted(set(s for s in sizes if sizes.count(s) > 1))
    if dup:
        problems.append('重复尺寸: %s' % ', '.join(dup))

    print('%-44s %d 个尺寸: %s' % (path, count, ', '.join(sizes)))
    return problems


def main():
    targets = sys.argv[1:]
    if not targets:
        root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        targets = [os.path.join(root, 'src', 'app.ico')]

    total = 0
    for t in targets:
        if not os.path.exists(t):
            print('!! 文件不存在: %s' % t)
            total += 1
            continue
        problems = check(t)
        for p in problems:
            print('   ! %s' % p)
        total += len(problems)

    print()
    print('结果: %s' % ('全部通过' if total == 0 else '%d 个问题' % total))
    return 0 if total == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
